using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// The one and only description of the patcher/runtime contract.
    ///
    /// Every hook is a **public static** method on `ZhaDai.Runtime.Hooks` in the
    /// `ZhaDai.Runtime` assembly. The patcher adds an `AssemblyNameReference` for that
    /// assembly and declares each `call` with the runtime assembly as the scope, so the
    /// plugin DLL must sit next to Terraria.exe (the CLR probes only the app directory
    /// and the GAC).
    /// </summary>
    public static class HookContract
    {
        /// <summary>Fully qualified name of the type that hosts the hooks.</summary>
        public const string HooksType = "ZhaDai.Runtime.Hooks";

        /// <summary>Simple assembly name the injected reference points at.</summary>
        public const string RuntimeAssemblyName = "ZhaDai.Runtime";

        /// <summary>File name the runtime is expected to be built as (next to Terraria.exe).</summary>
        public const string RuntimeAssemblyFileName = "ZhaDai.Runtime.dll";

        /// <summary>A1: first instruction of Main.UpdateWorld_Players.</summary>
        public const string BeforePlayerUpdate = "BeforePlayerUpdate";

        /// <summary>A2: immediately before the last ret of Player.Update(int).</summary>
        public const string AfterPlayerUpdate = "AfterPlayerUpdate";

        /// <summary>A3: first instruction of Player.ItemCheck_PlayInstruments(Terraria.Item).</summary>
        public const string BeforeItemCheck = "BeforeItemCheck";

        /// <summary>All hook names, in injection order.</summary>
        public static readonly string[] All = { BeforePlayerUpdate, AfterPlayerUpdate, BeforeItemCheck };

        /// <summary>
        /// Hook signature table. `true` means the hook returns bool; at A3 the value is
        /// consumed by a `brfalse` so "true = skip the vanilla body".
        /// </summary>
        public static IEnumerable<HookSpec> Specs()
        {
            yield return new HookSpec(BeforePlayerUpdate, "Main.UpdateWorld_Players 入口", "void", false,
                "帧开始。此时 Main.player[] 尚未更新，运行时应当在这里准备本 tick 的决策。");
            yield return new HookSpec(AfterPlayerUpdate, "Player.Update(int) 最后一个 ret 之前", "void", false,
                "帧结束。在 Player.Update 的正常出口前调用，用来收尾/回写状态。");
            yield return new HookSpec(BeforeItemCheck, "Player.ItemCheck_PlayInstruments 入口", "bool", true,
                "接手物品检查。返回 true 表示「已接管」，原版方法体被跳过；返回 false 走原版。");
        }

        /// <summary>
        /// Resolves the hooks type in the plugin module. Throws a PatchException that
        /// lists what was looked for when the type is absent.
        /// </summary>
        public static TypeDefinition ResolveHooksType(ModuleDefinition pluginModule)
        {
            var type = pluginModule.GetType(HooksType);
            if (type == null)
            {
                type = pluginModule.Types.FirstOrDefault(t => t.FullName == HooksType);
            }
            if (type == null)
            {
                throw new PatchException(
                    "插件里找不到类型 " + HooksType + "。\n" +
                    "  插件：" + pluginModule.FileName + "\n" +
                    "  插件里的顶层类型：" + string.Join("、", pluginModule.Types.Select(t => t.FullName).ToArray()));
            }
            return type;
        }

        /// <summary>
        /// Finds a hook method by name and validates its shape. `bool` hooks must return
        /// bool, `void` hooks must return void; everything must be public and static.
        /// </summary>
        public static MethodDefinition ResolveHook(TypeDefinition hooksType, HookSpec spec)
        {
            var candidates = hooksType.Methods.Where(m => m.Name == spec.Name).ToList();
            if (candidates.Count == 0)
            {
                throw new PatchException(
                    "插件缺少钩子 " + HooksType + "." + spec.Name + "。\n" +
                    "  该类型里现有的方法：" + string.Join("、", hooksType.Methods.Select(m => m.Name).Distinct().ToArray()));
            }

            var matched = candidates.Where(m => IsUsable(m, spec)).ToList();
            if (matched.Count == 1) return matched[0];

            if (matched.Count == 0)
            {
                throw new PatchException(
                    "插件里的 " + HooksType + "." + spec.Name + " 签名不符合契约（要求 public static " + spec.ReturnType + " " +
                    spec.Name + "()）。实际：" +
                    string.Join(" / ", candidates.Select(Describe).ToArray()));
            }

            throw new PatchException(
                "插件里的 " + HooksType + "." + spec.Name + " 有 " + matched.Count + " 个符合契约的重载，拒绝猜测：" +
                string.Join(" / ", matched.Select(Describe).ToArray()));
        }

        public static bool IsUsable(MethodDefinition method, HookSpec spec)
        {
            if (!method.IsPublic || !method.IsStatic) return false;
            if (method.Parameters.Count != 0) return false;
            if (method.GenericParameters.Count != 0) return false;
            return spec.ReturnsBool
                ? method.ReturnType.MetadataType == MetadataType.Boolean
                : method.ReturnType.MetadataType == MetadataType.Void;
        }

        public static string Describe(MethodDefinition method)
        {
            return (method.IsPublic ? "public " : "") + (method.IsStatic ? "static " : "") +
                   method.ReturnType.Name + " " + method.Name + "(" +
                   string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName).ToArray()) + ")";
        }

        /// <summary>
        /// Creates the `call` instruction for a hook, importing the method reference into
        /// the target module first.
        /// </summary>
        public static Instruction CreateCall(ModuleDefinition targetModule, MethodDefinition hook)
        {
            return Instruction.Create(OpCodes.Call, targetModule.ImportReference(hook));
        }

        public sealed class HookSpec
        {
            public HookSpec(string name, string anchor, string returnType, bool returnsBool, string purpose)
            {
                Name = name;
                Anchor = anchor;
                ReturnType = returnType;
                ReturnsBool = returnsBool;
                Purpose = purpose;
            }

            public string Name { get; private set; }
            public string Anchor { get; private set; }
            public string ReturnType { get; private set; }
            public bool ReturnsBool { get; private set; }
            public string Purpose { get; private set; }

            /// <summary>Exact signature text, used by README generation and reports.</summary>
            public string Signature
            {
                get { return "public static " + ReturnType + " " + Name + "()"; }
            }
        }
    }
}
