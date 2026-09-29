using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace ZhaDai.Patcher
{
    /// <summary>Install state of a Terraria.exe, derived from the manifest plus the live hash.</summary>
    public enum InstallState
    {
        /// <summary>The exe does not exist.</summary>
        NotFound,

        /// <summary>Original file, byte-for-byte identical to the verified 1.4.5.8 build.</summary>
        CleanSupported,

        /// <summary>No manifest, and the hash is neither the verified original nor our patched output.</summary>
        CleanUnsupported,

        /// <summary>Manifest present and the live hash matches the recorded patched hash.</summary>
        Installed,

        /// <summary>Manifest present but the live hash matches neither the original nor the patched hash.</summary>
        Changed,

        /// <summary>Our hooks are detectable in the exe but the manifest is missing or unreadable.</summary>
        PartiallyPatched,

        /// <summary>Unreadable file, bad manifest, or another error.</summary>
        Invalid
    }

    /// <summary>Status snapshot returned by every install/restore/status entry point.</summary>
    public sealed class InstallStatus
    {
        public InstallState State { get; set; }
        public string TerrariaExe { get; set; }
        public string GameVersion { get; set; }
        public string Sha256 { get; set; }
        public string Message { get; set; }
        public string FreshHash { get; set; }
        public string ManifestPath { get; set; }
        public string BackupPath { get; set; }

        /// <summary>True when the call was a dry run and nothing was written.</summary>
        public bool DryRun { get; set; }

        /// <summary>Hash that would have been recorded as the original (dry run only).</summary>
        public string PlannedOriginalSha256 { get; set; }

        /// <summary>Hash of the injected scratch output (dry run only; never persisted).</summary>
        public string PlannedPatchedSha256 { get; set; }

        public bool CanInstall
        {
            get { return State == InstallState.CleanSupported || State == InstallState.Installed; }
        }

        /// <summary>Chinese state label used in console reports.</summary>
        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case InstallState.NotFound: return "未找到";
                    case InstallState.CleanSupported: return "原版（受支持）";
                    case InstallState.CleanUnsupported: return "原版（未验证的构建）";
                    case InstallState.Installed: return "已注入";
                    case InstallState.Changed: return "已注入但被改动";
                    case InstallState.PartiallyPatched: return "半注入/清单缺失";
                    default: return "无法判定";
                }
            }
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.AppendLine("状态: " + StateLabel);
            builder.AppendLine("路径: " + (TerrariaExe ?? "未找到"));
            if (!string.IsNullOrEmpty(GameVersion)) builder.AppendLine("版本: " + GameVersion);
            if (!string.IsNullOrEmpty(Sha256)) builder.AppendLine("SHA-256: " + Sha256);
            if (!string.IsNullOrEmpty(ManifestPath)) builder.AppendLine("清单: " + ManifestPath);
            if (!string.IsNullOrEmpty(BackupPath)) builder.AppendLine("备份: " + BackupPath);
            builder.Append(Message);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Install / restore / status.
    ///
    /// Three rules copied from the reference implementation because they are the only
    /// reason the operation is reversible at all:
    /// 1. **Only verified builds are writable.** Version string *and* whole-file SHA-256
    ///    must match the 1.4.5.8 build that the anchors were reverse engineered against.
    /// 2. **Back up before writing, hash before restoring.** A backup is never overwritten
    ///    and a backup whose hash does not match the manifest is never used.
    /// 3. **The game must not be running.**
    /// </summary>
    public sealed class InstallationService
    {
        public const string SupportedVersion = "1.4.5.8";
        public const string SupportedSha256 = "960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3";
        public const string DataFolderName = "ZhaDai";

        private const string ManifestFileName = "manifest.json";
        private const string BackupFileName = "Terraria.exe.orig";

        public static string DataDirectory(string terrariaExe)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(terrariaExe)), DataFolderName);
        }

        public static string ManifestPath(string terrariaExe)
        {
            return Path.Combine(DataDirectory(terrariaExe), ManifestFileName);
        }

        public static string BackupPath(string terrariaExe)
        {
            return Path.Combine(DataDirectory(terrariaExe), BackupFileName);
        }

        // ------------------------------------------------------------------ status

        public InstallStatus GetStatus(string terrariaExe)
        {
            if (string.IsNullOrWhiteSpace(terrariaExe) || !File.Exists(terrariaExe))
                return New(InstallState.NotFound, terrariaExe, null, null, "未找到 Terraria.exe。");

            try
            {
                terrariaExe = Path.GetFullPath(terrariaExe);
                var version = FileVersionInfo.GetVersionInfo(terrariaExe).FileVersion;
                var hash = Sha256(terrariaExe);
                var manifest = ReadManifest(terrariaExe);
                var hooksPresent = ContainsRuntimeReference(terrariaExe);

                if (manifest != null)
                {
                    var status = New(InstallState.Changed, terrariaExe, version, hash,
                        "清单存在，但当前 Terraria.exe 的哈希既不是记录的注入结果、也不是记录的原版。");
                    status.ManifestPath = ManifestPath(terrariaExe);
                    status.BackupPath = File.Exists(BackupPath(terrariaExe)) ? BackupPath(terrariaExe) : null;

                    if (Same(hash, manifest.PatchedSha256))
                    {
                        status.State = InstallState.Installed;
                        status.Message = "ZhaDai 已注入（哈希与清单一致）。";
                    }
                    else if (Same(hash, manifest.OriginalSha256))
                    {
                        status.State = InstallState.CleanSupported;
                        status.Message = "当前是清单记录的原版 Terraria.exe（已还原或安装被回滚）。";
                    }
                    else if (!hooksPresent)
                    {
                        status.State = InstallState.Changed;
                        status.Message = "文件被 Steam 或别的工具改动过，且检测不到 ZhaDai 钩子。请先还原原版再重装。";
                    }
                    else
                    {
                        status.State = InstallState.Changed;
                        status.Message = "文件被改动过，但仍能检测到 ZhaDai 钩子。请先用备份还原，再重新安装。";
                    }
                    return status;
                }

                var supported = version == SupportedVersion && Same(hash, SupportedSha256);
                if (supported)
                {
                    var clean = New(InstallState.CleanSupported, terrariaExe, version, hash,
                        "检测到受支持的 Terraria " + SupportedVersion + "（原版，可以注入）。");
                    clean.FreshHash = "与已验证哈希一致。";
                    return clean;
                }

                if (hooksPresent)
                {
                    var partial = New(InstallState.PartiallyPatched, terrariaExe, version, hash,
                        "程序集里存在 ZhaDai 钩子，但缺少 " + DataFolderName + "\\" + ManifestFileName +
                        "，无法还原也无法安全重装。请用 Steam「验证游戏文件完整性」恢复原版。");
                    partial.FreshHash = "与已验证哈希不一致。";
                    return partial;
                }

                var unsupported = New(InstallState.CleanUnsupported, terrariaExe, version, hash,
                    "这个 Terraria.exe 不在已验证清单里（期望版本 " + SupportedVersion + "，期望哈希 " +
                    SupportedSha256 + "）。拒绝盲注入。");
                unsupported.FreshHash = "与已验证哈希不一致。";
                return unsupported;
            }
            catch (Exception exception)
            {
                return New(InstallState.Invalid, terrariaExe, null, null, "读取状态失败：" + exception.Message);
            }
        }

        // ----------------------------------------------------------------- install

        public InstallStatus Install(string terrariaExe, string pluginDll, bool dryRun)
        {
            terrariaExe = Path.GetFullPath(terrariaExe);
            pluginDll = Path.GetFullPath(pluginDll);

            EnsureGameClosed();

            if (!File.Exists(pluginDll))
                throw new PatchException("缺少注入载荷：" + pluginDll);
            if (!File.Exists(terrariaExe))
                throw new PatchException("找不到 Terraria.exe：" + terrariaExe);

            var before = GetStatus(terrariaExe);
            var backup = BackupPath(terrariaExe);
            var manifestPath = ManifestPath(terrariaExe);

            // A leftover install is re-based on the original backup, never stacked on top.
            var reinstall = before.State == InstallState.Installed || before.State == InstallState.Changed;

            if (reinstall && !File.Exists(backup))
                throw new PatchException("原版备份不见了（" + backup + "），拒绝在已注入的文件上再注入。请用 Steam 校验游戏文件完整性。");

            if (!before.CanInstall)
                throw new PatchException(before.Message);

            if (File.Exists(backup))
            {
                // Never overwrite a backup. It is only acceptable to reuse it when the live
                // exe is still recognisably ours (manifest says so) or is already the original.
                if (!reinstall && before.State != InstallState.CleanSupported)
                    throw new PatchException("备份已存在（" + backup + "），但当前 Terraria.exe 既不是原版也不是本工具的注入结果，拒绝覆盖备份。");
                if (reinstall)
                {
                    var manifest = ReadManifest(terrariaExe);
                    var backupHash = Sha256(backup);
                    if (manifest != null && !Same(backupHash, manifest.OriginalSha256))
                        throw new PatchException("已有备份的哈希与清单不符（备份 " + backupHash + "），拒绝使用来路不明的备份。");
                }
            }

            var status = New(before.State, terrariaExe, before.GameVersion, before.Sha256, "");
            status.ManifestPath = manifestPath;
            status.BackupPath = backup;

            var patchedTemp = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.zhaodai-new");
            var cleanTemp = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.zhaodai-clean");
            var log = new List<string>();

            try
            {
                // 1. Produce a clean original at a temporary path to patch from.
                string patchSource;
                if (reinstall)
                {
                    if (!dryRun) File.Copy(backup, cleanTemp, true);
                    patchSource = dryRun ? backup : cleanTemp;
                    log.Add("以已有备份为基准重新注入，原备份保持不变：" + backup);
                }
                else
                {
                    patchSource = terrariaExe;
                    log.Add("以当前原版 Terraria.exe 为基准注入。");
                }

                // 2. Patch into a temporary file and validate the written bytes.
                PatchReport reportSource = null;
                if (dryRun)
                {
                    // Dry run still performs the full injection into a scratch copy so the
                    // report is the real thing, but nothing inside the game directory changes.
                    var scratch = Path.Combine(Path.GetTempPath(), "zhaodai-dryrun-" + Guid.NewGuid().ToString("N") + ".exe");
                    var report = new AssemblyPatcher().Patch(patchSource, scratch, pluginDll);
                    reportSource = report;
                    log.Add("演练模式：注入结果写到了临时文件 " + scratch + "（未进入游戏目录）。");
                    try { File.Delete(scratch); } catch (IOException) { }
                }
                else
                {
                    var report = new AssemblyPatcher().Patch(patchSource, patchedTemp, pluginDll);
                    reportSource = report;
                    log.Add("注入副本已写出并校验：" + patchedTemp);
                }

                var originalHash = reinstall ? Sha256(backup) : Sha256(terrariaExe);
                var patchedHash = dryRun ? "(演练未落盘)" : reportSource.WrittenSha256;

                if (dryRun)
                {
                    status.State = before.State;
                    status.Sha256 = before.Sha256;
                    status.Message = "演练完成，未写入任何文件。\n" + string.Join("\n", log.ToArray());
                    status.DryRun = true;
                    status.PlannedOriginalSha256 = originalHash;
                    status.PlannedPatchedSha256 = patchedHash;
                    return status;
                }

                // 3. Back up the original (never overwriting an existing backup).
                Directory.CreateDirectory(DataDirectory(terrariaExe));
                if (!File.Exists(backup))
                {
                    File.Copy(terrariaExe, backup, true);
                    log.Add("已备份原版到 " + backup);
                }
                else
                {
                    log.Add("备份已存在，保持不动：" + backup);
                }

                // 4. Atomic replace.
                File.Replace(patchedTemp, terrariaExe, null, true);
                log.Add("已用 File.Replace 原子替换 Terraria.exe。");

                // 5. Manifest (UTF-8 without BOM).
                var manifestWrite = new InstallManifest
                {
                    ToolVersion = ToolVersion,
                    OriginalSha256 = originalHash,
                    PatchedSha256 = Sha256(terrariaExe),
                    GameVersion = before.GameVersion,
                    PluginVersion = ReadFileVersion(pluginDll),
                    PluginSha256 = Sha256(pluginDll),
                    Anchors = reportSource.Anchors.Select(a => a.Id + "=" + a.FullName + " token=" + a.Token).ToList(),
                    Hooks = HookContract.All.ToList(),
                    InstalledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                WriteManifest(terrariaExe, manifestWrite);
                log.Add("已写入清单 " + manifestPath);

                var after = GetStatus(terrariaExe);
                after.Message = (after.State == InstallState.Installed
                    ? "注入完成，回读校验通过。"
                    : "注入完成，但回读校验未通过（状态：" + after.StateLabel + "），请立即还原。")
                    + "\n" + string.Join("\n", log.ToArray());
                after.FreshHash = null;
                return after;
            }
            finally
            {
                if (!dryRun)
                {
                    DeleteIfExists(patchedTemp);
                    DeleteIfExists(cleanTemp);
                }
            }
        }

        // ----------------------------------------------------------------- restore

        public InstallStatus Restore(string terrariaExe)
        {
            terrariaExe = Path.GetFullPath(terrariaExe);
            EnsureGameClosed();

            if (!File.Exists(terrariaExe))
                return New(InstallState.NotFound, terrariaExe, null, null, "未找到 Terraria.exe。");

            var backup = BackupPath(terrariaExe);
            var manifest = ReadManifest(terrariaExe);
            if (manifest == null)
                throw new PatchException("没有找到 ZhaDai 清单（" + ManifestPath(terrariaExe) + "），无法确认原版哈希，拒绝还原。");
            if (!File.Exists(backup))
                throw new PatchException("没有找到原版备份（" + backup + "），无法还原。请用 Steam 校验游戏文件完整性。");

            var backupHash = Sha256(backup);
            if (!Same(backupHash, manifest.OriginalSha256))
                throw new PatchException("原版备份的哈希与清单不符（备份 " + backupHash + "，清单 " + manifest.OriginalSha256 +
                                         "），拒绝用一个来路不明的备份覆盖游戏。");

            var temporary = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.zhaodai-restore");
            try
            {
                File.Copy(backup, temporary, true);
                File.Replace(temporary, terrariaExe, null, true);
            }
            finally
            {
                DeleteIfExists(temporary);
            }

            var restoredHash = Sha256(terrariaExe);
            if (!Same(restoredHash, manifest.OriginalSha256))
                throw new PatchException("还原后的哈希与清单记录的原版不符（" + restoredHash + "）。请用 Steam 校验游戏文件完整性。");

            var status = GetStatus(terrariaExe);
            status.Message = "已还原成原版 Terraria（哈希 " + restoredHash + "，与清单一致）。备份与清单保留，可再次安装。";
            return status;
        }

        // ----------------------------------------------------------------- helpers

        public static string ToolVersion
        {
            get
            {
                var assembly = typeof(InstallationService).Assembly;
                var attribute = (System.Reflection.AssemblyFileVersionAttribute[])
                    assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false);
                if (attribute.Length > 0) return attribute[0].Version;
                return assembly.GetName().Version.ToString();
            }
        }

        public static void EnsureGameClosed()
        {
            var running = Process.GetProcessesByName("Terraria");
            try
            {
                if (running.Length > 0)
                    throw new PatchException("Terraria 正在运行（" + running.Length + " 个进程），请先退出游戏再安装或还原。");
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
        }

        /// <summary>True when the assembly references ZhaDai.Runtime, i.e. our hooks are in there.</summary>
        public static bool ContainsRuntimeReference(string exePath)
        {
            try
            {
                using (var module = ModuleDefinitionReader.Open(exePath))
                {
                    return module.AssemblyReferences.Any(r => r.Name == HookContract.RuntimeAssemblyName);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("X2"));
                return builder.ToString();
            }
        }

        public static bool Same(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                   left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadFileVersion(string path)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temporary file is harmless.
            }
        }

        internal sealed class InstallManifest
        {
            public string ToolVersion;
            public string OriginalSha256;
            public string PatchedSha256;
            public string GameVersion;
            public string PluginVersion;
            public string PluginSha256;
            public List<string> Anchors = new List<string>();
            public List<string> Hooks = new List<string>();
            public string InstalledUtc;
        }

        /// <summary>
        /// Reads manifest.json. The file is written by this tool in a fixed field order, so a
        /// small hand-rolled reader keeps the patcher free of a JSON dependency while still
        /// producing a file that any JSON parser accepts.
        /// </summary>
        private static InstallManifest ReadManifest(string terrariaExe)
        {
            var path = ManifestPath(terrariaExe);
            if (!File.Exists(path)) return null;

            var manifest = new InstallManifest();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (!line.StartsWith("\"", StringComparison.Ordinal)) continue;
                var separator = line.IndexOf("\":", StringComparison.Ordinal);
                if (separator < 1) continue;

                var key = line.Substring(1, separator - 1);
                var value = line.Substring(separator + 2).Trim().TrimEnd(',');
                value = Unquote(value);

                switch (key)
                {
                    case "toolVersion": manifest.ToolVersion = value; break;
                    case "originalSha256": manifest.OriginalSha256 = value; break;
                    case "patchedSha256": manifest.PatchedSha256 = value; break;
                    case "gameVersion": manifest.GameVersion = value; break;
                    case "pluginVersion": manifest.PluginVersion = value; break;
                    case "pluginSha256": manifest.PluginSha256 = value; break;
                    case "installedUtc": manifest.InstalledUtc = value; break;
                    case "anchors": manifest.Anchors = ReadStringArray(value); break;
                    case "hooks": manifest.Hooks = ReadStringArray(value); break;
                }
            }

            return string.IsNullOrEmpty(manifest.PatchedSha256) ? null : manifest;
        }

        private static void WriteManifest(string terrariaExe, InstallManifest manifest)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"tool\": \"zhaodai-patcher\",");
            builder.AppendLine("  \"toolVersion\": \"" + Escape(manifest.ToolVersion) + "\",");
            builder.AppendLine("  \"gameVersion\": \"" + Escape(manifest.GameVersion) + "\",");
            builder.AppendLine("  \"originalSha256\": \"" + Escape(manifest.OriginalSha256) + "\",");
            builder.AppendLine("  \"patchedSha256\": \"" + Escape(manifest.PatchedSha256) + "\",");
            builder.AppendLine("  \"pluginVersion\": \"" + Escape(manifest.PluginVersion) + "\",");
            builder.AppendLine("  \"pluginSha256\": \"" + Escape(manifest.PluginSha256) + "\",");
            builder.AppendLine("  \"installedUtc\": \"" + Escape(manifest.InstalledUtc) + "\",");
            builder.AppendLine("  \"hooks\": " + WriteStringArray(manifest.Hooks) + ",");
            builder.AppendLine("  \"anchors\": " + WriteStringArray(manifest.Anchors));
            builder.Append("}");
            builder.AppendLine();

            var directory = DataDirectory(terrariaExe);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ManifestFileName), builder.ToString(), new UTF8Encoding(false));
        }

        private static string WriteStringArray(IEnumerable<string> values)
        {
            var items = values == null ? new List<string>() : values.ToList();
            if (items.Count == 0) return "[]";
            return "[" + string.Join(", ", items.Select(v => "\"" + Escape(v) + "\"").ToArray()) + "]";
        }

        private static List<string> ReadStringArray(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(value)) return result;
            var body = value.Trim();
            if (body.StartsWith("[", StringComparison.Ordinal)) body = body.Substring(1);
            if (body.EndsWith("]", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            foreach (var piece in body.Split(new[] { "\", \"" }, StringSplitOptions.RemoveEmptyEntries))
                result.Add(Unquote(piece.Trim()));
            return result;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static InstallStatus New(InstallState state, string exe, string version, string hash, string message)
        {
            return new InstallStatus
            {
                State = state,
                TerrariaExe = exe,
                GameVersion = version,
                Sha256 = hash,
                Message = message,
                BackupPath = string.IsNullOrWhiteSpace(exe) ? null : BackupPath(exe),
                ManifestPath = string.IsNullOrWhiteSpace(exe) ? null : ManifestPath(exe)
            };
        }
    }
}
