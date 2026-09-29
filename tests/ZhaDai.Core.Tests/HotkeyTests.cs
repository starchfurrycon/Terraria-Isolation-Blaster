using System;
using ZhaDai.Automation;

namespace ZhaDai.Core.Tests;

/// <summary>
/// The hotkey matcher, and the bug that made it worth testing.
///
/// The runtime reads the pressed keys out of the game's <c>KeyboardState</c> by reflection. Its first
/// version did <c>as object[]</c> on the result; the game hands back an array of an enum, and a value-type
/// array is never an <c>object[]</c>, so that cast quietly returned null and F10 did nothing in game --
/// no crash, no log line, nothing to explain it. These checks pin the behaviour the runtime relies on.
/// </summary>
internal static class HotkeyTests
{
    private enum FakeKey
    {
        F1,
        F8,
        F10,
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("按键接管");

        check(HotkeyMatch.Matches("F10", "F10"), "同名按键匹配");
        check(HotkeyMatch.Matches("f10", "F10"), "大小写不影响匹配");
        check(HotkeyMatch.Matches(" F10 ", "F10"), "两侧空格不影响匹配");
        check(!HotkeyMatch.Matches("F1", "F10"), "F1 不能当成 F10（不比对前缀）");
        check(!HotkeyMatch.Matches("F10", "F1"), "反过来也一样");
        check(!HotkeyMatch.Matches("", "F10"), "空按键名不匹配");
        check(!HotkeyMatch.Matches("F10", ""), "空热键不匹配");

        // 真机故障点：Keys[] 是值类型数组，`as object[]` 是 null，只有 Array 能遍历。
        Array pressed = new[] { FakeKey.F1, FakeKey.F10 };
        check(HotkeyMatch.Any(pressed, "F10"), "枚举数组（游戏里的 Keys[]）里找得到 F10");
        check(!HotkeyMatch.Any(new[] { FakeKey.F1, FakeKey.F8 }, "F10"), "没按 F10 就不能算按下");
        check(!HotkeyMatch.Any(new FakeKey[0], "F10"), "一个键都没按就是没按");
        check(!HotkeyMatch.Any(null, "F10"), "拿不到数组时按没按都当没按");
    }
}
