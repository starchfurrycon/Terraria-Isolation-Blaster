using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Result of resolving one injection anchor. Immutable, carries the metadata token
    /// so every report can quote exactly which method definition was picked.
    /// </summary>
    public sealed class AnchorResult
    {
        public AnchorResult(string id, string display, string searchedFor, MethodDefinition method, string note)
        {
            Id = id;
            Display = display;
            SearchedFor = searchedFor;
            Method = method;
            Note = note;
        }

        /// <summary>Stable short id used in reports (A1 / A2 / A3).</summary>
        public string Id { get; private set; }

        /// <summary>Human readable anchor name, e.g. "Main.UpdateWorld_Players entry".</summary>
        public string Display { get; private set; }

        /// <summary>What the resolver looked for, quoted verbatim in failure messages.</summary>
        public string SearchedFor { get; private set; }

        /// <summary>Resolved method definition, or null when the anchor is missing.</summary>
        public MethodDefinition Method { get; private set; }

        /// <summary>Free-form resolution note (which overload matched, substitutes, ...).</summary>
        public string Note { get; private set; }

        public bool Found { get { return Method != null; } }

        public string FullName { get { return Method == null ? "(missing)" : Method.FullName; } }

        /// <summary>Metadata token rendered the same way Cecil prints it (0x06000D2B).</summary>
        public string Token
        {
            get
            {
                return Method == null
                    ? "-"
                    : "0x" + Method.MetadataToken.ToUInt32().ToString("X8");
            }
        }
    }

    /// <summary>
    /// Locates the game methods the hooks are injected into.
    ///
    /// Two hard rules, both learned the expensive way in the reference project:
    /// 1. **Never hardcode metadata tokens.** Tokens change with every build; the
    ///    resolver matches on declaring type name + method name + parameter shape and
    ///    reports the token it actually found.
    /// 2. **Never guess.** If an anchor is ambiguous or absent, fail loudly and list
    ///    every candidate name that was searched, instead of writing a possibly
    ///    unloadable Terraria.exe.
    /// </summary>
    public static class AnchorResolver
    {
        public const string MainType = "Terraria.Main";
        public const string PlayerType = "Terraria.Player";

        /// <summary>
        /// Anchor A1 - frame start. Resolved type name candidates are tried in order so a
        /// renamed/relocated method can still be found without touching code.
        /// </summary>
        public static readonly string[] WorldPlayersCandidates = { "UpdateWorld_Players", "UpdateWorld_Players_Inner" };

        /// <summary>Anchor A2 - the per-player update pass that consumes the shared input state.</summary>
        public static readonly string[] PlayerUpdateCandidates = { "Update" };

        /// <summary>Anchor A3 - the vanilla instrument/inventory playback tick used for hand takeover.</summary>
        public static readonly string[] ItemCheckInstrumentsCandidates = { "ItemCheck_PlayInstruments", "ItemCheck_PlayInstruments_Inner" };

        public static AnchorResult ResolveWorldPlayers(ModuleDefinition module)
        {
            return ResolveById(
                "A1",
                "Main.UpdateWorld_Players 入口（帧开始）",
                MainType + "::" + string.Join(" | " + MainType + "::", WorldPlayersCandidates) + "()",
                module,
                MainType,
                WorldPlayersCandidates,
                m => m.Parameters.Count == 0 && m.HasBody,
                "取「无参数 + 有方法体」的那一个重载。");
        }

        public static AnchorResult ResolvePlayerUpdate(ModuleDefinition module)
        {
            return ResolveById(
                "A2",
                "Player.Update(int) 最后一个 ret 之前（帧结束）",
                PlayerType + "::Update(System.Int32)",
                module,
                PlayerType,
                PlayerUpdateCandidates,
                m => m.Parameters.Count == 1
                     && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32
                     && m.HasBody,
                "只接受单参数 int 的重载，与 1.4.5.8 的 Player.Update(int i) 对应。");
        }

        public static AnchorResult ResolveItemCheckInstruments(ModuleDefinition module)
        {
            return ResolveById(
                "A3",
                "Player.ItemCheck_PlayInstruments 入口（接手物品检查）",
                PlayerType + "::" + string.Join(" | " + PlayerType + "::", ItemCheckInstrumentsCandidates) + "(Terraria.Item)",
                module,
                PlayerType,
                ItemCheckInstrumentsCandidates,
                m => m.Parameters.Count == 1
                     && m.Parameters[0].ParameterType.FullName == "Terraria.Item"
                     && m.HasBody,
                "只接受单参数 Terraria.Item 的重载。");
        }

        public static IList<AnchorResult> ResolveAll(ModuleDefinition module)
        {
            return new List<AnchorResult>
            {
                ResolveWorldPlayers(module),
                ResolvePlayerUpdate(module),
                ResolveItemCheckInstruments(module)
            };
        }

        private static AnchorResult ResolveById(
            string id,
            string display,
            string searchedFor,
            ModuleDefinition module,
            string declaringType,
            string[] names,
            Func<MethodDefinition, bool> shape,
            string shapeNote)
        {
            var type = module.GetType(declaringType);
            if (type == null)
            {
                throw new PatchException(
                    "锚点 " + id + "（" + display + "）解析失败：程序集里没有类型 " + declaringType + "。\n" +
                    "  查找的方法名：" + string.Join("、", names) + "\n" +
                    "  这不是受支持的 Terraria 程序集。");
            }

            var seen = new List<string>();
            foreach (var name in names)
            {
                var sameName = type.Methods.Where(m => m.Name == name).ToList();
                if (sameName.Count == 0)
                {
                    seen.Add(name + "（不存在）");
                    continue;
                }

                var matched = sameName.Where(shape).ToList();
                if (matched.Count == 1)
                {
                    return new AnchorResult(id, display, searchedFor, matched[0],
                        "按名字 " + name + " 定位；" + shapeNote);
                }

                if (matched.Count > 1)
                {
                    throw new PatchException(
                        "锚点 " + id + "（" + display + "）解析失败：名字 " + name + " 匹配到 " + matched.Count +
                        " 个签名相同的方法，拒绝猜测。\n" +
                        "  候选：" + string.Join("、", matched.Select(m => m.FullName + " token=" + Token(m)).ToArray()) + "\n" +
                        "  查找的方法名：" + string.Join("、", names));
                }

                seen.Add(name + "（存在 " + sameName.Count + " 个重载，但没有一个符合签名：" +
                         string.Join(" / ", sameName.Select(Signature).ToArray()) + "）");
            }

            throw new PatchException(
                "锚点 " + id + "（" + display + "）解析失败：在 " + declaringType + " 里找不到符合签名的方法。\n" +
                "  查找的方法名及其结果：" + string.Join("；", seen) + "\n" +
                "  期望签名：" + searchedFor);
        }

        private static string Signature(MethodDefinition method)
        {
            return method.Name + "(" +
                   string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName).ToArray()) + ")";
        }

        private static string Token(MethodDefinition method)
        {
            return "0x" + method.MetadataToken.ToUInt32().ToString("X8");
        }
    }
}
