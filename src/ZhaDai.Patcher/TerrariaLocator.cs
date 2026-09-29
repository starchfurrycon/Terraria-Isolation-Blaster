using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Locates Terraria.exe so `--terraria` can be omitted.
    ///
    /// Order: environment variable -> Steam registry -> Steam library folders -> well known
    /// install paths. There is deliberately no whole-disk search: it takes minutes and stalls
    /// the machine, and a missing path is better reported as "specify it yourself".
    /// </summary>
    public static class TerrariaLocator
    {
        /// <summary>Steam app id of Terraria.</summary>
        public const string TerrariaAppId = "105600";

        /// <summary>Environment override, checked first.</summary>
        public const string EnvironmentVariable = "ZHAODAI_TERRARIA";

        public static string FindTerrariaExe()
        {
            foreach (var candidate in Candidates())
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

            foreach (var root in SteamRoots())
            {
                yield return Path.Combine(root, "steamapps", "common", "Terraria", "Terraria.exe");

                foreach (var library in LibraryFolders(Path.Combine(root, "steamapps", "libraryfolders.vdf")))
                    yield return Path.Combine(library, "steamapps", "common", "Terraria", "Terraria.exe");
            }

            yield return @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"C:\Program Files\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
            yield return @"D:\SteamLibrary\steamapps\common\Terraria\Terraria.exe";
            yield return @"E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe";
        }

        private static IEnumerable<string> SteamRoots()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string value = null;
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (var key = baseKey.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        if (key != null) value = key.GetValue("SteamPath") as string;
                    }
                }
                catch (Exception)
                {
                    // A missing or unreadable key just means this candidate is unavailable.
                }

                if (!string.IsNullOrEmpty(value))
                    yield return value.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Shallow parse of Steam's `libraryfolders.vdf`: only `"path" "X:\\..."` lines matter,
        /// and pulling in a VDF parser for that would be overkill.
        /// </summary>
        public static IEnumerable<string> LibraryFolders(string vdfPath)
        {
            if (!File.Exists(vdfPath)) yield break;
            foreach (var line in File.ReadAllLines(vdfPath))
            {
                var text = line.Trim();
                if (!text.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var first = text.IndexOf('"', 6);
                if (first < 0) continue;
                var second = text.IndexOf('"', first + 1);
                if (second < 0) continue;
                var value = text.Substring(first + 1, second - first - 1);
                yield return value.Replace("\\\\", "\\").Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }
}
