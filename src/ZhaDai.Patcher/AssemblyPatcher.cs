using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Writes the ZhaDai hooks into Terraria.exe.
    ///
    /// Three anchors, all inside the normal update path:
    ///
    /// | Anchor | Position | Hook | Purpose |
    /// |---|---|---|---|
    /// | A1 | `Main.UpdateWorld_Players` entry | `Hooks.BeforePlayerUpdate()` | Frame start |
    /// | A2 | before the last `ret` of `Player.Update(int)` | `Hooks.AfterPlayerUpdate()` | Frame end |
    /// | A3 | `Player.ItemCheck_PlayInstruments(Item)` entry | `Hooks.BeforeItemCheck()` | Take over the vanilla body |
    ///
    /// The injected calls are emitted as `call` instructions whose operand is a
    /// MethodReference scoped to the `ZhaDai.Runtime` assembly; an AssemblyNameReference
    /// for that assembly is added to Terraria.exe so the CLR resolves the plugin at load
    /// time. Hooks that return bool are wrapped in `call / brfalse <original first insn> /
    /// ret`, so a `true` return skips the vanilla body entirely.
    /// </summary>
    public sealed class AssemblyPatcher
    {
        /// <summary>
        /// Writes the patched assembly to <paramref name="outputExe"/> and validates it by
        /// re-opening the written file. Never touches <paramref name="sourceExe"/>.
        /// </summary>
        public PatchReport Patch(string sourceExe, string outputExe, string pluginDll)
        {
            var source = Path.GetFullPath(sourceExe);
            var output = Path.GetFullPath(outputExe);
            var plugin = Path.GetFullPath(pluginDll);

            if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("注入输出不能覆盖输入文件，必须写到另一个路径。");
            if (!File.Exists(source)) throw new PatchException("找不到目标程序集：" + source);
            if (!File.Exists(plugin)) throw new PatchException("缺少注入载荷：" + plugin);

            var report = new PatchReport { SourceExe = source, OutputExe = output, PluginDll = plugin };

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(plugin));
            resolver.AddSearchDirectory(Path.GetDirectoryName(source));

            using (var pluginModule = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver }))
            using (var module = ModuleDefinition.ReadModule(source, new ReaderParameters
            {
                InMemory = true,
                ReadWrite = false,
                AssemblyResolver = resolver,
                ReadSymbols = false
            }))
            {
                report.PluginAssembly = pluginModule.Assembly.Name.Name;
                report.RuntimeReference = BuildAssemblyReference(module, pluginModule.Assembly.Name);

                var hooksType = HookContract.ResolveHooksType(pluginModule);
                report.HooksType = hooksType.FullName;

                var hooks = new Dictionary<string, MethodDefinition>();
                foreach (var spec in HookContract.Specs())
                    hooks[spec.Name] = HookContract.ResolveHook(hooksType, spec);

                var anchors = AnchorResolver.ResolveAll(module);
                report.Anchors.AddRange(anchors);
                foreach (var anchor in anchors)
                {
                    if (!anchor.Found)
                        throw new PatchException("锚点 " + anchor.Id + " 未解析：" + anchor.Display);
                }

                var a1 = anchors[0].Method;
                var a2 = anchors[1].Method;
                var a3 = anchors[2].Method;

                EnsureNotAlreadyPatched(module, a1, a2, a3);
                report.AlreadyPatched = false;

                // A1 - entry of the world player update.
                InjectAtStart(a1, new[]
                {
                    HookContract.CreateCall(module, hooks[HookContract.BeforePlayerUpdate])
                });

                // A2 - before the last ret (the normal exit) of Player.Update(int).
                InjectBeforeExit(a2, new[]
                {
                    HookContract.CreateCall(module, hooks[HookContract.AfterPlayerUpdate])
                });

                // A3 - entry of the instrument/item check, with an early-out branch.
                PatchPredicateEntry(a3, HookContract.CreateCall(module, hooks[HookContract.BeforeItemCheck]));

                // Cecil will not widen Br_S on its own; inserting instructions can push a
                // short branch past its 127 byte encoding limit and produce an unloadable exe.
                foreach (var method in new[] { a1, a2, a3 })
                {
                    var widened = WidenShortBranches(method);
                    if (widened > 0)
                        report.Notes.Add("加宽短跳转：" + method.FullName + " 共 " + widened + " 条。");
                }

                if (!module.AssemblyReferences.Any(r => r.Name == report.RuntimeReference.Name))
                    module.AssemblyReferences.Add(report.RuntimeReference);

                var directory = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                module.Write(output, new WriterParameters { WriteSymbols = false });
            }

            report.Validation = Validate(output);
            report.WrittenSize = new FileInfo(output).Length;
            report.WrittenSha256 = InstallationService.Sha256(output);
            return report;
        }

        /// <summary>
        /// Re-opens a written assembly and asserts the exact injected layout. Only the
        /// bytes on disk count; "I think I inserted it" is not evidence.
        /// </summary>
        public static PatchValidation Validate(string patchedExe)
        {
            var validation = new PatchValidation();

            using (var module = ModuleDefinition.ReadModule(patchedExe))
            {
                var reference = module.AssemblyReferences.FirstOrDefault(r => r.Name == HookContract.RuntimeAssemblyName);
                if (reference == null)
                {
                    throw new PatchException("写入后的程序集里没有 " + HookContract.RuntimeAssemblyName +
                                             " 的 AssemblyNameReference。");
                }
                validation.RuntimeReference = reference.FullName;
                validation.Checks.Add("程序集引用 " + reference.FullName + " 存在。");

                var anchors = AnchorResolver.ResolveAll(module);
                foreach (var anchor in anchors)
                {
                    validation.Checks.Add("锚点 " + anchor.Id + " " + anchor.FullName + " token=" + anchor.Token);
                    validation.Anchors.Add(anchor);
                }

                // A1: the very first instruction is the frame-start hook.
                var head = anchors[0].Method.Body.Instructions[0];
                RequireHookCall(head, HookContract.BeforePlayerUpdate, "锚点 A1 的入口第一条指令");
                validation.Checks.Add("A1 入口第一条指令 = call " + HookContract.BeforePlayerUpdate + "。");

                // A2: the instruction before the last ret is the frame-end hook.
                var exit = FindLastExit(anchors[1].Method);
                if (exit == null || exit.Previous == null)
                    throw new PatchException("锚点 A2 的出口结构不符合预期：找不到 ret 或它前面没有指令。");
                RequireHookCall(exit.Previous, HookContract.AfterPlayerUpdate, "锚点 A2 最后一个 ret 之前");
                validation.Checks.Add("A2 最后一个 ret 之前 = call " + HookContract.AfterPlayerUpdate +
                                      "（ret 位于 IL_" + exit.Offset.ToString("X4") + "）。");

                // A3: call / brfalse <original first> / ret at the head.
                var body = anchors[2].Method.Body.Instructions;
                if (body.Count < 4)
                    throw new PatchException("锚点 A3 的方法体只有 " + body.Count + " 条指令，注入点校验失败。");
                if (body[0].OpCode != OpCodes.Call || body[1].OpCode != OpCodes.Brfalse || body[2].OpCode != OpCodes.Ret)
                {
                    throw new PatchException("锚点 A3 的接管分支布局不符合预期，实际：" +
                                             body[0].OpCode + " / " + body[1].OpCode + " / " + body[2].OpCode);
                }
                RequireHookCall(body[0], HookContract.BeforeItemCheck, "锚点 A3 的入口");
                if (!ReferenceEquals(body[1].Operand, body[3]))
                    throw new PatchException("锚点 A3 的 brfalse 目标不是原版方法体的第一条指令。");
                validation.Checks.Add("A3 头部布局 = call " + HookContract.BeforeItemCheck + " / brfalse <原版首指令> / ret。");
            }

            return validation;
        }

        /// <summary>
        /// Builds the AssemblyNameReference for the plugin.
        ///
        /// Cecil 0.11 does not bake `TypeReference.Scope` into the written metadata, so a
        /// reference imported straight from the plugin module would silently end up bound
        /// to the *target* module (`Terraria`) and the hook would never resolve at runtime.
        /// Declaring a real AssemblyNameReference and importing through its MainModule makes
        /// the hook's TypeRef scope point at `ZhaDai.Runtime` in the written file.
        /// </summary>
        private static AssemblyNameReference BuildAssemblyReference(ModuleDefinition target, AssemblyNameDefinition pluginName)
        {
            var reference = new AssemblyNameReference(pluginName.Name, pluginName.Version);
            reference.PublicKeyToken = pluginName.PublicKeyToken;
            reference.Culture = pluginName.Culture;
            reference.IsRetargetable = pluginName.IsRetargetable;

            // Importing the reference itself registers it in target.AssemblyReferences.
            target.ImportReference(reference);
            return reference;
        }

        private static void EnsureNotAlreadyPatched(ModuleDefinition module, MethodDefinition a1, MethodDefinition a2, MethodDefinition a3)
        {
            if (module.AssemblyReferences.Any(r => r.Name == HookContract.RuntimeAssemblyName))
                throw new PatchException("这个 Terraria.exe 已经注入过 ZhaDai（存在 " + HookContract.RuntimeAssemblyName +
                                         " 程序集引用），拒绝重复注入。请先还原。");

            var foreign = new[] { "TingYu.Plugin", "Chaite.Plugin" }
                .FirstOrDefault(name => module.AssemblyReferences.Any(r => r.Name == name));
            if (foreign != null)
                throw new PatchException("检测到其它工具的注入（" + foreign + "），拒绝叠加。请先还原那个工具再安装 ZhaDai。");

            foreach (var method in new[] { a1, a2, a3 })
            {
                if (ContainsHookCall(method))
                    throw new PatchException("方法 " + method.FullName + " 里已经存在 ZhaDai 钩子调用，拒绝重复注入。");
            }
        }

        private static bool ContainsHookCall(MethodDefinition method)
        {
            if (!method.HasBody) return false;
            return method.Body.Instructions.Any(i =>
            {
                var reference = i.Operand as MethodReference;
                return i.OpCode == OpCodes.Call && reference != null &&
                       reference.DeclaringType.FullName == HookContract.HooksType &&
                       HookContract.All.Contains(reference.Name);
            });
        }

        /// <summary>
        /// Rewrites the head of a predicate hook target to:
        /// <code>
        /// if (!Hooks.BeforeItemCheck()) { ...vanilla body... }
        /// return;
        /// </code>
        /// Insertion order matters: the `ret` goes in first so the vanilla body entry stays
        /// the original first instruction and the branch target needs no fix-up.
        /// </summary>
        private static void PatchPredicateEntry(MethodDefinition method, Instruction hookCall)
        {
            if (!method.HasBody) throw new PatchException(method.FullName + " 没有方法体。");
            var instructions = method.Body.Instructions;
            var processor = method.Body.GetILProcessor();

            var originalFirst = instructions[0];
            var ret = Instruction.Create(OpCodes.Ret);
            processor.InsertBefore(originalFirst, ret);

            processor.InsertBefore(ret, hookCall);
            processor.InsertBefore(ret, Instruction.Create(OpCodes.Brfalse, originalFirst));

            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1);
        }

        private static void InjectAtStart(MethodDefinition method, IEnumerable<Instruction> instructions)
        {
            if (!method.HasBody) throw new PatchException(method.FullName + " 没有方法体。");
            var first = method.Body.Instructions[0];
            var processor = method.Body.GetILProcessor();
            foreach (var instruction in instructions)
                processor.InsertBefore(first, instruction);
        }

        /// <summary>Inserts before the last `ret`, i.e. the normal exit of the method.</summary>
        private static void InjectBeforeExit(MethodDefinition method, IEnumerable<Instruction> instructions)
        {
            var exit = FindLastExit(method);
            if (exit == null) throw new PatchException(method.FullName + " 里找不到 ret，无法确定正常出口。");

            var processor = method.Body.GetILProcessor();
            foreach (var instruction in instructions)
                processor.InsertBefore(exit, instruction);
        }

        private static Instruction FindLastExit(MethodDefinition method)
        {
            if (!method.HasBody) return null;
            var instructions = method.Body.Instructions;
            for (var i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode == OpCodes.Ret) return instructions[i];
            }
            return null;
        }

        /// <summary>
        /// Converts every short branch to its long form. Cecil does not do this on its own,
        /// so a branch that happened to be under 127 bytes before the insertion can overflow
        /// its encoding afterwards and CorExitProcess / CLR rejects the image.
        /// </summary>
        internal static int WidenShortBranches(MethodDefinition method)
        {
            if (!method.HasBody) return 0;
            var widened = 0;
            foreach (var instruction in method.Body.Instructions)
            {
                OpCode replacement;
                switch (instruction.OpCode.Code)
                {
                    case Code.Br_S: replacement = OpCodes.Br; break;
                    case Code.Brfalse_S: replacement = OpCodes.Brfalse; break;
                    case Code.Brtrue_S: replacement = OpCodes.Brtrue; break;
                    case Code.Beq_S: replacement = OpCodes.Beq; break;
                    case Code.Bge_S: replacement = OpCodes.Bge; break;
                    case Code.Bge_Un_S: replacement = OpCodes.Bge_Un; break;
                    case Code.Bgt_S: replacement = OpCodes.Bgt; break;
                    case Code.Bgt_Un_S: replacement = OpCodes.Bgt_Un; break;
                    case Code.Ble_S: replacement = OpCodes.Ble; break;
                    case Code.Ble_Un_S: replacement = OpCodes.Ble_Un; break;
                    case Code.Blt_S: replacement = OpCodes.Blt; break;
                    case Code.Blt_Un_S: replacement = OpCodes.Blt_Un; break;
                    case Code.Bne_Un_S: replacement = OpCodes.Bne_Un; break;
                    case Code.Leave_S: replacement = OpCodes.Leave; break;
                    default: continue;
                }
                instruction.OpCode = replacement;
                widened++;
            }
            return widened;
        }

        private static void RequireHookCall(Instruction instruction, string name, string where)
        {
            if (instruction == null)
                throw new PatchException(where + " 缺少 " + name + " 调用。");

            var reference = instruction.Operand as MethodReference;
            if (instruction.OpCode != OpCodes.Call || reference == null ||
                reference.DeclaringType.FullName != HookContract.HooksType || reference.Name != name)
            {
                throw new PatchException(where + " 不是预期的 " + HookContract.HooksType + "." + name + "，实际：" +
                                         instruction.OpCode + " " +
                                         (reference == null ? "-" : reference.FullName));
            }

            // The scope must point at the runtime assembly, otherwise the hook would be
            // resolved against Terraria.exe itself and fail at load time.
            var scope = reference.DeclaringType.Scope;
            var scopeName = scope == null ? null : scope.Name;
            if (scopeName == HookContract.RuntimeAssemblyName) return;
            if (scope is ModuleDefinition && ((ModuleDefinition)scope).Assembly.Name.Name == HookContract.RuntimeAssemblyName) return;

            throw new PatchException(where + " 的钩子作用域不是 " + HookContract.RuntimeAssemblyName + "，实际：" +
                                     (scopeName ?? "(null)") + "。");
        }

        /// <summary>Everything the caller needs to print a truthful report about a patch run.</summary>
        public sealed class PatchReport
        {
            public PatchReport()
            {
                Anchors = new List<AnchorResult>();
                Notes = new List<string>();
            }

            public string SourceExe { get; set; }
            public string OutputExe { get; set; }
            public string PluginDll { get; set; }
            public string PluginAssembly { get; set; }
            public string HooksType { get; set; }
            public AssemblyNameReference RuntimeReference { get; set; }
            public List<AnchorResult> Anchors { get; private set; }
            public List<string> Notes { get; private set; }
            public bool AlreadyPatched { get; set; }
            public long WrittenSize { get; set; }
            public string WrittenSha256 { get; set; }
            public PatchValidation Validation { get; set; }
        }

        public sealed class PatchValidation
        {
            public PatchValidation()
            {
                Checks = new List<string>();
                Anchors = new List<AnchorResult>();
            }

            public string RuntimeReference { get; set; }
            public List<string> Checks { get; private set; }
            public List<AnchorResult> Anchors { get; private set; }
        }
    }
}
