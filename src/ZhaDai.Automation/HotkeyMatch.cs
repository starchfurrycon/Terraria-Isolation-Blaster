using System;

namespace ZhaDai.Automation
{
    /// <summary>
    /// Matching the game's pressed keys against the configured hotkey.
    ///
    /// This lives here rather than in the runtime plugin so it can be tested: the plugin can only be
    /// exercised inside a running Terraria, and this exact logic shipped a bug that no offline check
    /// noticed -- <c>Xna's Keys[]</c> is an array of a value type, so <c>as object[]</c> returned null
    /// and the hotkey never fired, while the plugin otherwise looked completely healthy.
    /// </summary>
    public static class HotkeyMatch
    {
        /// <summary>True when the pressed key name is the configured hotkey, ignoring case and padding.</summary>
        public static bool Matches(string pressed, string hotkey)
        {
            if (string.IsNullOrEmpty(pressed) || string.IsNullOrEmpty(hotkey))
            {
                return false;
            }

            return string.Equals(pressed.Trim(), hotkey.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when any entry of a pressed-key array matches. Takes <see cref="Array"/> on purpose: this
        /// accepts a value-type array such as an enum array, which an <c>object[]</c> parameter would reject.
        /// </summary>
        public static bool Any(Array pressed, string hotkey)
        {
            if (pressed == null || string.IsNullOrEmpty(hotkey))
            {
                return false;
            }

            foreach (object key in pressed)
            {
                if (key != null && Matches(key.ToString(), hotkey))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
