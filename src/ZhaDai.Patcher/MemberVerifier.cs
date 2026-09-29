using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Offline contract check: open Terraria.exe with Cecil, resolve every method / field /
    /// type the injected runtime needs, and report each one as found or missing. The game is
    /// never launched and nothing is written.
    ///
    /// The requirement set has two layers:
    /// 1. The **injection contract** owned by this tool (the three anchors plus the three
    ///    hook signatures). These are mandatory.
    /// 2. Optional **runtime requirements** declared by the plugin itself, if it exposes a
    ///    `ZhaDai.Runtime.ReflectionRequirements` type with a static `All` property whose
    ///    items carry `Type` / `Member` / `Kind` / `Required` / `Parameters` fields. When the
    ///    runtime grows that type, `verify` widens automatically, exactly like the reference
    ///    implementation, so the tool and the plugin can never disagree about the list.
    /// </summary>
    public static class MemberVerifier
    {
        public const string RequirementsTypeName = "ZhaDai.Runtime.ReflectionRequirements";

        public static int Run(string terrariaExe, string pluginDll, bool strictPlugin)
        {
            terrariaExe = Path.GetFullPath(terrariaExe);

            if (!File.Exists(terrariaExe))
            {
                Console.Error.WriteLine("错误: 找不到 Terraria.exe：" + terrariaExe);
                return 1;
            }

            var lines = new List<string>();
            var failures = new List<string>();
            var problems = new List<string>();

            lines.Add("目标程序集: " + terrariaExe);
            lines.Add("文件大小:   " + new FileInfo(terrariaExe).Length + " 字节");
            lines.Add("SHA-256:    " + InstallationService.Sha256(terrariaExe));
            lines.Add("");

            // ---------------------------------------------------------- plugin presence
            var pluginPresent = !string.IsNullOrWhiteSpace(pluginDll) && File.Exists(pluginDll);
            if (!pluginPresent)
            {
                var missing = "插件缺失：" +
                              (string.IsNullOrWhiteSpace(pluginDll) ? "(未指定 --plugin)" : Path.GetFullPath(pluginDll));
                lines.Add("[载荷] " + missing);
                failures.Add(missing);
            }
            else
            {
                pluginDll = Path.GetFullPath(pluginDll);
                lines.Add("[载荷] 插件: " + pluginDll + "（" + new FileInfo(pluginDll).Length + " 字节，SHA-256 " +
                          InstallationService.Sha256(pluginDll) + "）");
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(terrariaExe));
            if (pluginPresent) resolver.AddSearchDirectory(Path.GetDirectoryName(pluginDll));

            using (var module = ModuleDefinition.ReadModule(terrariaExe, new ReaderParameters
            {
                InMemory = true,
                ReadWrite = false,
                AssemblyResolver = resolver,
                ReadSymbols = false
            }))
            {
                var version = module.Assembly.Name.Version;
                lines.Add("[程序集] " + module.Assembly.Name.FullName + "，运行时 " + module.Runtime);
                lines.Add("");

                // ------------------------------------------------- injection anchors
                lines.Add("== 注入锚点（本工具拥有，必需） ==");
                var anchors = new List<AnchorResult>();
                foreach (var resolve in new Func<ModuleDefinition, AnchorResult>[]
                {
                    AnchorResolver.ResolveWorldPlayers,
                    AnchorResolver.ResolvePlayerUpdate,
                    AnchorResolver.ResolveItemCheckInstruments
                })
                {
                    try
                    {
                        var anchor = resolve(module);
                        anchors.Add(anchor);
                        lines.Add("  [✓] " + anchor.Id + " " + anchor.Display);
                        lines.Add("        " + anchor.FullName);
                        lines.Add("        MetadataToken = " + anchor.Token + "；" + anchor.Note);
                    }
                    catch (PatchException exception)
                    {
                        anchors.Add(null);
                        lines.Add("  [✗] 锚点解析失败");
                        foreach (var line in exception.Message.Split('\n'))
                            lines.Add("        " + line.TrimEnd());
                        failures.Add("锚点解析失败：" + exception.Message.Split('\n')[0]);
                    }
                }
                lines.Add("");

                // ------------------------------------------------- hook contract
                lines.Add("== 钩子契约（" + HookContract.HooksType + "，必需） ==");
                if (!pluginPresent)
                {
                    lines.Add("  [✗] 插件不存在，无法核对钩子方法。请先用 --plugin 指定 " +
                              HookContract.RuntimeAssemblyFileName + "。");
                }
                else
                {
                    try
                    {
                        using (var pluginModule = ModuleDefinition.ReadModule(pluginDll, new ReaderParameters
                        {
                            AssemblyResolver = resolver,
                            ReadSymbols = false
                        }))
                        {
                            var hooksType = HookContract.ResolveHooksType(pluginModule);
                            lines.Add("  [✓] 类型 " + hooksType.FullName + " 存在（程序集 " +
                                      pluginModule.Assembly.Name.Name + "）");

                            foreach (var spec in HookContract.Specs())
                            {
                                try
                                {
                                    var hook = HookContract.ResolveHook(hooksType, spec);
                                    lines.Add("  [✓] " + spec.Signature + "  —— " + spec.Anchor);
                                    lines.Add("        " + HookContract.Describe(hook) + "；" + spec.Purpose);
                                }
                                catch (PatchException exception)
                                {
                                    lines.Add("  [✗] 钩子 " + spec.Name + " 不符合契约");
                                    foreach (var line in exception.Message.Split('\n'))
                                        lines.Add("        " + line.TrimEnd());
                                    failures.Add("钩子不符合契约：" + spec.Name);
                                }
                            }

                            var expectedNames = pluginModule.Assembly.Name.Name;
                            if (!string.Equals(expectedNames, HookContract.RuntimeAssemblyName, StringComparison.Ordinal))
                                lines.Add("  [!] 插件程序集名是 " + expectedNames + "，不是 " +
                                          HookContract.RuntimeAssemblyName + "；注入时会按插件自身的名字建立程序集引用。");
                        }
                    }
                    catch (PatchException exception)
                    {
                        foreach (var line in exception.Message.Split('\n'))
                            lines.Add("  [✗] " + line.TrimEnd());
                        failures.Add("插件契约核对失败：" + exception.Message.Split('\n')[0]);
                    }
                    catch (Exception exception)
                    {
                        lines.Add("  [✗] 读取插件失败：" + exception.GetType().Name + ": " + exception.Message);
                        failures.Add("读取插件失败：" + exception.Message);
                    }
                }
                lines.Add("");

                // ------------------------------------------------- runtime requirements
                var requirements = pluginPresent ? LoadRuntimeRequirements(pluginDll, lines) : null;
                var checkedCount = 0;
                var mandatoryMissing = new List<string>();
                var optionalMissing = new List<string>();

                if (requirements != null && requirements.Count > 0)
                {
                    lines.Add("== 运行时成员清单（来自插件 " + RequirementsTypeName + "，共 " + requirements.Count + " 项） ==");
                    foreach (var requirement in requirements)
                    {
                        checkedCount++;
                        string problem;
                        if (Verify(module, requirement, out problem))
                        {
                            lines.Add("  [✓] " + requirement.Display);
                            continue;
                        }
                        lines.Add("  [" + (requirement.Required ? "✗" : "!") + "] " + requirement.Display);
                        lines.Add("        " + problem);
                        if (requirement.Required) mandatoryMissing.Add(problem);
                        else optionalMissing.Add(problem);
                    }
                    lines.Add("");
                }

                // ------------------------------------------------- baseline game members
                lines.Add("== 基础游戏成员（钩子落地所必需） ==");
                foreach (var requirement in BaselineRequirements())
                {
                    checkedCount++;
                    string problem;
                    if (Verify(module, requirement, out problem))
                    {
                        lines.Add("  [✓] " + requirement.Display);
                        continue;
                    }
                    lines.Add("  [✗] " + requirement.Display);
                    lines.Add("        " + problem);
                    mandatoryMissing.Add(problem);
                }
                lines.Add("");

                lines.Add("模块版本: " + version);
                lines.Add("共核对 " + checkedCount + " 项成员；必需缺失 " + mandatoryMissing.Count +
                          " 项；可选缺失 " + optionalMissing.Count + " 项。");

                foreach (var line in lines) Console.WriteLine(line);

                if (failures.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("结论: 核对失败，" + failures.Count + " 个致命问题：");
                    foreach (var failure in failures) Console.WriteLine("  ✗ " + failure);
                    return 2;
                }

                if (mandatoryMissing.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("结论: 核对失败，" + mandatoryMissing.Count + " 项必需成员缺失。");
                    return 2;
                }

                if (optionalMissing.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("结论: 必需项全部通过，但有 " + optionalMissing.Count + " 项可选成员缺失（相关功能会退化）：");
                    foreach (var missing in optionalMissing) Console.WriteLine("  ! " + missing);
                }

                if (strictPlugin && (requirements == null || requirements.Count == 0))
                {
                    Console.WriteLine();
                    Console.WriteLine("结论: --require-plugin-requirements 已启用，但插件没有声明 " + RequirementsTypeName +
                                      "，核对失败。");
                    return 2;
                }

                Console.WriteLine();
                Console.WriteLine("结论: 全部必需项核对通过，可以注入。");
                return 0;
            }
        }

        /// <summary>
        /// Members the injected code itself depends on, independent of what the runtime does.
        /// These are deliberately a small, stable set: if one of them moves, the anchor or the
        /// hook shape has changed and the patch must not be attempted.
        /// </summary>
        private static IEnumerable<RequirementView> BaselineRequirements()
        {
            yield return new RequirementView("Terraria.Main", "", "Type", true, null);
            yield return new RequirementView("Terraria.Player", "", "Type", true, null);
            yield return new RequirementView("Terraria.Item", "", "Type", true, null);
            yield return new RequirementView("Terraria.GameInput.TriggersSet", "", "Type", true, null);
            yield return new RequirementView("Terraria.GameInput.TriggersSet", "CopyInto", "Method", true,
                new[] { "Terraria.Player" });
            yield return new RequirementView("Terraria.Main", "player", "Field", true, null);
            yield return new RequirementView("Terraria.Main", "myPlayer", "Field", true, null);
            yield return new RequirementView("Terraria.Main", "GameUpdateCount", "Property", false, null);
            yield return new RequirementView("Terraria.Entity", "whoAmI", "Field", false, null);
            yield return new RequirementView("Terraria.Player", "selectedItem", "Property", false, null);
        }

        /// <summary>
        /// Reads the plugin's optional self-describing requirement list.
        ///
        /// Requirements are decoded from IL with Cecil rather than by loading the assembly with
        /// <c>Assembly.LoadFrom</c>. That keeps the tool offline and deterministic: the list is
        /// built in the type initializer out of literal factory calls, so the values are readable
        /// without running anything, and no version of the plugin ever leaks into this process.
        /// A reflection pass is kept as a fallback for a differently shaped list.
        /// </summary>
        private static List<RequirementView> LoadRuntimeRequirements(string pluginDll, List<string> lines)
        {
            var fromIl = LoadRuntimeRequirementsFromIl(pluginDll, lines);
            if (fromIl != null) return fromIl;
            return LoadRuntimeRequirementsByReflection(pluginDll, lines);
        }

        /// <summary>
        /// Factory method names accepted when decoding the requirement list. The full names are the
        /// documented shape; the single-letter forms are what Roslyn emits when it shortens a
        /// private helper name, so both must be accepted.
        /// </summary>
        private static readonly string[] FactoryMethods =
        {
            "Field", "Property", "Method", "F", "P", "M"
        };

        private static string KindFromFactory(string name)
        {
            switch (name)
            {
                case "Field":
                case "F":
                    return "Field";
                case "Property":
                case "P":
                    return "Property";
                default:
                    return "Method";
            }
        }

        private static List<RequirementView> LoadRuntimeRequirementsFromIl(string pluginDll, List<string> lines)
        {
            try
            {
                using (var module = ModuleDefinition.ReadModule(pluginDll, new ReaderParameters { ReadSymbols = false }))
                {
                    var type = FindType(module, RequirementsTypeName);
                    if (type == null) return null;

                    var initializer = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
                    if (initializer == null || !initializer.HasBody) return null;

                    var instructions = initializer.Body.Instructions;
                    var result = new List<RequirementView>();
                    var undecodable = 0;
                    for (var i = 0; i < instructions.Count; i++)
                    {
                        // The factory helpers take (Type, Member, Required[, Note]) and are always
                        // static, so a MethodReference with HasThis false is enough to spot them.
                        var reference = instructions[i].Operand as MethodReference;
                        if (reference == null || reference.HasThis) continue;
                        if (reference.DeclaringType.FullName != type.FullName) continue;
                        if (!FactoryMethods.Contains(reference.Name)) continue;
                        if (reference.ReturnType.Name != "Requirement") continue;

                        var values = ReadStraightLineLiterals(instructions, i);
                        if (values == null) { undecodable++; continue; }
                        if (Environment.GetEnvironmentVariable("ZHAODAI_DEBUG_IL") == "1")
                            Console.WriteLine("[il] " + reference.Name + "@" + i + " " + string.Join(" | ", values.ToArray()));

                        // The payload is two strings (Type, Member) followed by the required flag;
                        // any further strings are the params array. Positions are not used, because
                        // the compiler keeps a duplicated array element index on the stack that
                        // would otherwise be mistaken for the flag.
                        var strings = values.Where(v => v.StartsWith("str:", StringComparison.Ordinal)).ToList();
                        if (strings.Count < 2) { undecodable++; continue; }

                        var required = true;
                        var memberIndex = values.IndexOf(strings[1]);
                        for (var v = memberIndex + 1; v < values.Count; v++)
                        {
                            if (!values[v].StartsWith("num:", StringComparison.Ordinal)) continue;
                            required = int.Parse(values[v].Substring(4),
                                System.Globalization.CultureInfo.InvariantCulture) != 0;
                            break;
                        }

                        result.Add(new RequirementView(
                            strings[0].Substring(4),
                            strings[1].Substring(4),
                            KindFromFactory(reference.Name),
                            required,
                            strings.Skip(2).Select(s => s.Substring(4)).ToArray()));
                    }

                    if (result.Count == 0)
                    {
                        lines.Add("（" + RequirementsTypeName + " 存在，但 IL 解码没有得到任何成员项（无法解码 " +
                                  undecodable + " 处调用点）。改用反射。）");
                        return null;
                    }

                    lines.Add("（成员清单来源：" + RequirementsTypeName + " 的类型初始化器，用 Cecil 离线解码 IL" +
                              (undecodable > 0 ? "；其中 " + undecodable + " 处调用点无法解码，已跳过" : "") +
                              "。未加载插件程序集。）");
                    return result;
                }
            }
            catch (Exception exception)
            {
                lines.Add("（用 Cecil 解码 " + RequirementsTypeName + " 失败：" + exception.Message + "，改走反射。）");
                return null;
            }
        }

        /// <summary>
        /// Reads the literal arguments of the factory call at <paramref name="callIndex"/> and
        /// returns them in push order tagged <c>str:</c> or <c>num:</c>.
        ///
        /// The run is bounded by the enclosing statement (a <c>dup</c>/<c>call</c>/<c>stsfld</c>
        /// boundary). When a <c>newarr</c> appears in the run the params-array literal belongs to
        /// this call and is captured from its first stored element; integers are otherwise kept
        /// because one of them is the required flag. Only literals are understood - anything else
        /// makes the call site decode as null, which is reported as "cannot decode" rather than as
        /// a wrong list.
        /// </summary>
        private static List<string> ReadStraightLineLiterals(IList<Instruction> instructions, int callIndex)
        {
            var start = 0;
            var arrayStart = -1;
            for (var i = callIndex - 1; i >= 0; i--)
            {
                var code = instructions[i].OpCode.Code;
                if (code == Code.Newarr)
                {
                    for (var j = i + 1; j < callIndex; j++)
                    {
                        if (instructions[j].OpCode.Code == Code.Ldstr)
                        {
                            arrayStart = j;
                            break;
                        }
                    }
                    break;
                }
                if (code == Code.Dup || code == Code.Call || code == Code.Callvirt || code == Code.Stsfld)
                {
                    start = i + 1;
                    break;
                }
            }
            if (arrayStart >= 0) start = arrayStart;

            var values = new List<string>();
            for (var i = start; i < callIndex; i++)
            {
                var instruction = instructions[i];
                var code = instruction.OpCode.Code;
                if (code == Code.Ldstr) values.Add("str:" + (instruction.Operand as string));
                else if (code == Code.Ldc_I4_0) values.Add("num:0");
                else if (code == Code.Ldc_I4_1) values.Add("num:1");
                else if (code == Code.Ldc_I4_2) values.Add("num:2");
                else if (code == Code.Ldc_I4_3) values.Add("num:3");
                else if (code == Code.Ldc_I4_4) values.Add("num:4");
                else if (code == Code.Ldc_I4_5) values.Add("num:5");
                else if (code == Code.Ldc_I4_6) values.Add("num:6");
                else if (code == Code.Ldc_I4_7) values.Add("num:7");
                else if (code == Code.Ldc_I4_8) values.Add("num:8");
                else if (code == Code.Ldc_I4_S && instruction.Operand is sbyte)
                    values.Add("num:" + ((sbyte)instruction.Operand).ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (code == Code.Ldc_I4 && instruction.Operand is int)
                    values.Add("num:" + ((int)instruction.Operand).ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (code == Code.Dup || code == Code.Stelem_Ref || code == Code.Nop || code == Code.Newarr)
                {
                    // Array plumbing carries no value of its own.
                }
                else
                {
                    return null;
                }
            }
            return values;
        }

        /// <summary>Fallback: reflect the requirement list out of the plugin assembly.</summary>
        private static List<RequirementView> LoadRuntimeRequirementsByReflection(string pluginDll, List<string> lines)
        {
            try
            {
                var assembly = Assembly.LoadFrom(pluginDll);
                var type = assembly.GetType(RequirementsTypeName, false);
                if (type == null)
                {
                    lines.Add("（插件没有声明 " + RequirementsTypeName + "，本次只核对注入契约与基础成员。）");
                    lines.Add("");
                    return null;
                }

                var property = type.GetProperty("All", BindingFlags.Public | BindingFlags.Static);
                var value = property != null
                    ? property.GetValue(null, null)
                    : type.GetField("All", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                if (value == null)
                {
                    lines.Add("（" + RequirementsTypeName + ".All 为 null，本次只核对注入契约与基础成员。）");
                    lines.Add("");
                    return null;
                }

                var result = new List<RequirementView>();
                foreach (var item in (IEnumerable)value)
                {
                    var itemType = item.GetType();
                    var kind = Read<object>(itemType, item, "Kind");
                    result.Add(new RequirementView(
                        Read<string>(itemType, item, "Type"),
                        Read<string>(itemType, item, "Member"),
                        kind == null ? "Field" : kind.ToString(),
                        Read<bool>(itemType, item, "Required"),
                        Read<string[]>(itemType, item, "Parameters") ?? new string[0]));
                }
                lines.Add("（成员清单来源：反射加载 " + RequirementsTypeName + "。此方式需要加载插件程序集，" +
                          "只在 Cecil 解码失败时使用。）");
                return result;
            }
            catch (Exception exception)
            {
                lines.Add("（读取插件成员清单失败：" + exception.Message + "；本次只核对注入契约与基础成员。）");
                lines.Add("");
                return null;
            }
        }

        private static T Read<T>(Type itemType, object item, string name)
        {
            var field = itemType.GetField(name);
            if (field != null) return (T)field.GetValue(item);
            var property = itemType.GetProperty(name);
            if (property != null) return (T)property.GetValue(item, null);
            return default(T);
        }

        private sealed class RequirementView
        {
            public RequirementView(string type, string member, string kind, bool required, string[] parameters)
            {
                Type = type;
                Member = member;
                Kind = kind;
                Required = required;
                Parameters = parameters;
            }

            public string Type;
            public string Member;
            public string Kind;
            public bool Required;
            public string[] Parameters;

            public string Display
            {
                get
                {
                    var suffix = Parameters != null && Parameters.Length > 0
                        ? "(" + string.Join(", ", Parameters) + ")"
                        : "";
                    return Type + (string.IsNullOrEmpty(Member) ? "" : "." + Member + suffix) + "（" + Kind + "）";
                }
            }
        }

        // ------------------------------------------------------------- resolution

        private static bool Verify(ModuleDefinition module, RequirementView requirement, out string problem)
        {
            problem = null;
            var type = FindType(module, requirement.Type);
            if (type == null)
            {
                problem = requirement.Display + " 所在的类型不存在";
                return false;
            }

            if (requirement.Kind == "Type") return true;
            if (requirement.Kind == "Method") return VerifyMethod(type, requirement, out problem);

            var field = FindField(type, requirement.Member);
            var property = FindProperty(type, requirement.Member);

            if (requirement.Kind == "Field")
            {
                if (field != null) return true;
                problem = requirement.Display + " 不存在" +
                          (property != null
                              ? "，但存在同名属性（定义在 " + property.DeclaringType.FullName + "），应声明为 Property"
                              : "，继承链上也没有同名属性");
                return false;
            }

            if (property != null)
            {
                if (property.GetMethod == null)
                {
                    problem = requirement.Display + " 没有 getter（反射读会失败）";
                    return false;
                }
                return true;
            }

            problem = requirement.Display + " 不存在" +
                      (field != null
                          ? "，但存在同名字段（定义在 " + field.DeclaringType.FullName + "），应声明为 Field"
                          : "，继承链上也没有同名字段");
            return false;
        }

        private static bool VerifyMethod(TypeDefinition type, RequirementView requirement, out string problem)
        {
            problem = null;
            var candidates = AllMethods(type).Where(m => m.Name == requirement.Member).ToArray();

            foreach (var candidate in candidates)
            {
                var parameters = candidate.Parameters;
                if (parameters.Count < requirement.Parameters.Length) continue;

                var matched = true;
                for (var i = 0; i < requirement.Parameters.Length && matched; i++)
                    matched = parameters[i].ParameterType.FullName == requirement.Parameters[i];
                if (!matched) continue;

                // Extra parameters must be optional, otherwise a reflection call with only
                // the listed arguments throws.
                for (var i = requirement.Parameters.Length; i < parameters.Count && matched; i++)
                    matched = parameters[i].IsOptional;
                if (!matched) continue;

                return true;
            }

            if (candidates.Length == 0)
            {
                problem = requirement.Display + " 方法不存在";
            }
            else
            {
                var actual = candidates.Select(m =>
                    m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName).ToArray()) + ")");
                problem = requirement.Display + " 没有匹配的重载（清单要求参数 " +
                          string.Join(", ", requirement.Parameters) + "）。实际存在：" + string.Join(" | ", actual.ToArray());
            }
            return false;
        }

        /// <summary>Finds a field along the inheritance chain (Player.Center lives on Entity).</summary>
        private static FieldDefinition FindField(TypeDefinition type, string name)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                var field = current.Fields.FirstOrDefault(f => f.Name == name);
                if (field != null) return field;
            }
            return null;
        }

        private static PropertyDefinition FindProperty(TypeDefinition type, string name)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                var property = current.Properties.FirstOrDefault(p => p.Name == name);
                if (property != null) return property;
            }
            return null;
        }

        private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                foreach (var method in current.Methods) yield return method;
            }
        }

        private static TypeDefinition ResolveBase(TypeDefinition type)
        {
            if (type.BaseType == null) return null;
            try
            {
                return type.BaseType.Resolve();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Finds a type; `Outer+Inner` denotes a nested type, matching reflection syntax.</summary>
        private static TypeDefinition FindType(ModuleDefinition module, string fullName)
        {
            var direct = module.GetType(fullName);
            if (direct != null) return direct;

            var separator = fullName.IndexOf('+');
            if (separator < 0) return null;
            var outer = module.GetType(fullName.Substring(0, separator));
            if (outer == null) return null;
            var nestedName = fullName.Substring(separator + 1);
            return outer.NestedTypes.FirstOrDefault(t => t.Name == nestedName);
        }
    }
}
