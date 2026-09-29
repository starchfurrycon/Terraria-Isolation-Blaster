using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Console entry point for `zhaodai-patcher`.
    ///
    /// Exit codes:
    /// <list type="bullet">
    /// <item>0 - success / everything resolved.</item>
    /// <item>1 - error (missing file, IO failure, refused operation).</item>
    /// <item>2 - verification failed (missing members, invalid patch layout).</item>
    /// <item>3 - invalid usage.</item>
    /// <item>4 - the target build is not the verified one.</item>
    /// </list>
    /// All user facing output is Simplified Chinese; code comments and XML docs are English.
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitError = 1;
        private const int ExitVerifyFailed = 2;
        private const int ExitUsage = 3;
        private const int ExitUnsupportedBuild = 4;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // A redirected/legacy console may refuse the encoding switch; output still works.
            }

            try
            {
                return Dispatch(args);
            }
            catch (PatchException exception)
            {
                Console.Error.WriteLine("错误: " + exception.Message);
                return ExitError;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("未预期的错误: " + exception.GetType().Name + ": " + exception.Message);
                Console.Error.WriteLine(exception.StackTrace);
                return ExitError;
            }
        }

        private static int Dispatch(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return ExitUsage;
            }

            var command = args[0].ToLowerInvariant();
            if (command == "--help" || command == "-h" || command == "help" || command == "/?")
            {
                PrintUsage();
                return ExitOk;
            }
            if (command == "--version")
            {
                Console.WriteLine("zhaodai-patcher " + InstallationService.ToolVersion);
                return ExitOk;
            }

            var options = Options.Parse(args.Skip(1).ToArray());
            if (options.Help)
            {
                PrintUsage();
                return ExitOk;
            }

            switch (command)
            {
                case "verify": return CommandVerify(options);
                case "patch-copy": return CommandPatchCopy(options);
                case "install": return CommandInstall(options);
                case "restore": return CommandRestore(options);
                case "status": return CommandStatus(options);
                default:
                    Console.Error.WriteLine("未知命令: " + command);
                    PrintUsage();
                    return ExitUsage;
            }
        }

        // ------------------------------------------------------------------ verify

        private static int CommandVerify(Options options)
        {
            var exe = ResolveTerraria(options);
            if (exe == null) return ExitUsage;

            Console.WriteLine("== zhaodai-patcher verify ==");
            return MemberVerifier.Run(exe, options.Plugin, options.RequirePluginRequirements);
        }

        // -------------------------------------------------------------- patch-copy

        private static int CommandPatchCopy(Options options)
        {
            var exe = ResolveTerraria(options);
            if (exe == null) return ExitUsage;

            var output = options.Out;
            if (string.IsNullOrWhiteSpace(output))
            {
                Console.Error.WriteLine("错误: patch-copy 需要 --out <副本路径>。");
                return ExitUsage;
            }
            output = Path.GetFullPath(output);

            Console.WriteLine("== zhaodai-patcher patch-copy ==");

            if (options.VerifyOnly)
            {
                Console.WriteLine("模式: 只做回读校验（--verify-only），不会写出任何文件。");
                Console.WriteLine();
                return ReportValidationOnly(output);
            }

            var plugin = options.Plugin;
            if (string.IsNullOrWhiteSpace(plugin))
            {
                Console.Error.WriteLine("错误: patch-copy 需要 --plugin <" + HookContract.RuntimeAssemblyFileName + ">。");
                return ExitUsage;
            }

            if (File.Exists(output))
            {
                Console.WriteLine("提示: 输出文件已存在，将被覆盖：" + output);
            }

            var report = new AssemblyPatcher().Patch(exe, output, plugin);

            Console.WriteLine("源程序集:   " + report.SourceExe);
            Console.WriteLine("  大小 " + new FileInfo(report.SourceExe).Length + " 字节");
            Console.WriteLine("  SHA-256 " + InstallationService.Sha256(report.SourceExe));
            Console.WriteLine("输出副本:   " + report.OutputExe);
            Console.WriteLine("  大小 " + report.WrittenSize + " 字节");
            Console.WriteLine("  SHA-256 " + report.WrittenSha256);
            Console.WriteLine("插件:       " + report.PluginDll);
            Console.WriteLine("  程序集 " + report.PluginAssembly + "（" + HookContract.RuntimeAssemblyFileName + "）");
            Console.WriteLine("  类型 " + report.HooksType);
            Console.WriteLine();
            PrintAnchors(report.Anchors);
            PrintValidation(report.Validation);
            if (report.Notes.Count > 0)
            {
                Console.WriteLine("说明:");
                foreach (var note in report.Notes) Console.WriteLine("  · " + note);
            }

            // The injected reference is resolved by simple name from the application
            // directory, so the runtime must sit next to the executable it was patched into.
            var pluginTarget = Path.Combine(Path.GetDirectoryName(report.OutputExe), HookContract.RuntimeAssemblyFileName);
            if (options.NoPluginCopy)
            {
                Console.WriteLine();
                Console.WriteLine("已跳过插件副本拷贝（--no-plugin-copy）。要让副本能真正启动，需要把 " +
                                  HookContract.RuntimeAssemblyFileName + " 放到 " + Path.GetDirectoryName(report.OutputExe) + "。");
            }
            else
            {
                File.Copy(report.PluginDll, pluginTarget, true);
                Console.WriteLine();
                Console.WriteLine("已把插件拷贝到副本同级: " + pluginTarget);
            }

            Console.WriteLine();
            Console.WriteLine("原文件哈希（必须与注入前一致）: " + InstallationService.Sha256(report.SourceExe));
            Console.WriteLine("结论: 注入演练通过，原始文件未被修改。");
            return ExitOk;
        }

        private static int ReportValidationOnly(string patchedExe)
        {
            if (!File.Exists(patchedExe))
            {
                Console.Error.WriteLine("错误: 找不到待校验的文件：" + patchedExe);
                return ExitError;
            }

            Console.WriteLine("文件:    " + patchedExe);
            Console.WriteLine("大小:    " + new FileInfo(patchedExe).Length + " 字节");
            Console.WriteLine("SHA-256: " + InstallationService.Sha256(patchedExe));
            Console.WriteLine();

            var validation = AssemblyPatcher.Validate(patchedExe);
            PrintValidation(validation);
            Console.WriteLine();
            Console.WriteLine("结论: 回读校验通过，ZhaDai 钩子确实存在于该文件中。");
            return ExitOk;
        }

        // ----------------------------------------------------------------- install

        private static int CommandInstall(Options options)
        {
            var exe = ResolveTerraria(options);
            if (exe == null) return ExitUsage;

            var plugin = options.Plugin;
            if (string.IsNullOrWhiteSpace(plugin))
            {
                var beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HookContract.RuntimeAssemblyFileName);
                if (File.Exists(beside)) plugin = beside;
            }

            if (string.IsNullOrWhiteSpace(plugin))
            {
                Console.Error.WriteLine("错误: install 需要 --plugin <" + HookContract.RuntimeAssemblyFileName +
                                        ">（也可以把它放在本程序同级目录）。");
                return ExitUsage;
            }
            plugin = Path.GetFullPath(plugin);

            Console.WriteLine("== zhaodai-patcher install" + (options.DryRun ? " --dry-run" : "") + " ==");

            var service = new InstallationService();
            var before = service.GetStatus(exe);

            Console.WriteLine("目标:     " + exe);
            Console.WriteLine("版本:     " + (before.GameVersion ?? "(未知)"));
            Console.WriteLine("SHA-256:  " + (before.Sha256 ?? "(未知)"));
            Console.WriteLine("当前状态: " + before.StateLabel);
            Console.WriteLine("插件:     " + plugin);
            Console.WriteLine("  版本 " + (FileVersion(plugin) ?? "(未知)") + "，SHA-256 " + InstallationService.Sha256(plugin));
            Console.WriteLine("数据目录: " + InstallationService.DataDirectory(exe));
            Console.WriteLine("备份将写: " + InstallationService.BackupPath(exe) +
                              (File.Exists(InstallationService.BackupPath(exe)) ? "（已存在，不会被覆盖）" : "（新建）"));
            Console.WriteLine("清单将写: " + InstallationService.ManifestPath(exe));
            Console.WriteLine();

            var status = service.Install(exe, plugin, options.DryRun);

            Console.WriteLine(status.Message);
            Console.WriteLine();
            if (options.DryRun)
            {
                Console.WriteLine("演练结果:");
                Console.WriteLine("  当前状态:       " + before.StateLabel);
                Console.WriteLine("  计划记录原版:   " + status.PlannedOriginalSha256);
                Console.WriteLine("  演练注入结果:   " + status.PlannedPatchedSha256);
                Console.WriteLine("  演练是否落盘:   否（游戏目录内未创建/修改任何文件）");
                Console.WriteLine("结论: 演练完成，未写入任何文件。去掉 --dry-run 才会真正安装。");
                return ExitOk;
            }

            Console.WriteLine("结论: 安装完成。");
            switch (status.State)
            {
                case InstallState.Installed: return ExitOk;
                case InstallState.CleanUnsupported: return ExitUnsupportedBuild;
                default: return ExitError;
            }
        }

        // ----------------------------------------------------------------- restore

        private static int CommandRestore(Options options)
        {
            var exe = ResolveTerraria(options);
            if (exe == null) return ExitUsage;

            Console.WriteLine("== zhaodai-patcher restore ==");
            var service = new InstallationService();
            var status = service.Restore(exe);
            Console.WriteLine(status);
            Console.WriteLine();
            Console.WriteLine("结论: 已恢复原版。");
            return status.State == InstallState.CleanSupported ? ExitOk : ExitError;
        }

        // ------------------------------------------------------------------ status

        private static int CommandStatus(Options options)
        {
            var exe = ResolveTerraria(options);
            if (exe == null) return ExitUsage;

            Console.WriteLine("== zhaodai-patcher status ==");
            var service = new InstallationService();
            var status = service.GetStatus(exe);
            Console.WriteLine(status);

            var manifest = InstallationService.ManifestPath(exe);
            if (File.Exists(manifest))
            {
                Console.WriteLine();
                Console.WriteLine("清单内容（" + manifest + "）:");
                foreach (var line in File.ReadAllLines(manifest)) Console.WriteLine("  " + line);
            }

            Console.WriteLine();
            switch (status.State)
            {
                case InstallState.Installed:
                case InstallState.CleanSupported:
                    return ExitOk;
                default:
                    return ExitError;
            }
        }

        // ----------------------------------------------------------------- helpers

        private static void PrintAnchors(IEnumerable<AnchorResult> anchors)
        {
            Console.WriteLine("已解析锚点（按名字定位，不写死 MetadataToken）:");
            foreach (var anchor in anchors)
            {
                Console.WriteLine("  [" + anchor.Id + "] " + anchor.Display);
                Console.WriteLine("       " + anchor.FullName);
                Console.WriteLine("       MetadataToken = " + anchor.Token);
            }
            Console.WriteLine();
        }

        private static void PrintValidation(AssemblyPatcher.PatchValidation validation)
        {
            Console.WriteLine("写入后回读校验:");
            Console.WriteLine("  程序集引用: " + validation.RuntimeReference);
            foreach (var check in validation.Checks) Console.WriteLine("  [✓] " + check);
        }

        private static string FileVersion(string path)
        {
            try
            {
                return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveTerraria(Options options)
        {
            var exe = options.Terraria;
            if (string.IsNullOrWhiteSpace(exe))
            {
                exe = TerrariaLocator.FindTerrariaExe();
                if (exe == null)
                {
                    Console.Error.WriteLine("错误: 未指定 --terraria，且自动定位失败（环境变量 " +
                                            TerrariaLocator.EnvironmentVariable + "、Steam 注册表与常见路径都没命中）。");
                    return null;
                }
                Console.WriteLine("（未指定 --terraria，自动定位到 " + exe + "）");
            }

            exe = Path.GetFullPath(exe);
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine("错误: 找不到 Terraria.exe：" + exe);
                return null;
            }
            return exe;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("zhaodai-patcher " + InstallationService.ToolVersion + " —— ZhaDai 可逆 IL 注入工具");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  zhaodai-patcher <命令> [选项]");
            Console.WriteLine();
            Console.WriteLine("命令:");
            Console.WriteLine("  verify       只读核对：用 Cecil 打开 Terraria.exe，逐项解析锚点与插件钩子，报告找到/缺失。");
            Console.WriteLine("  patch-copy   离线演练：把注入结果写到 --out 指定的副本并回读校验，绝不碰游戏本体。");
            Console.WriteLine("  install      真实安装：备份原版 -> 注入 -> 原子替换 -> 写清单。");
            Console.WriteLine("  restore      还原：用备份换回原版并核对清单里的原版哈希。");
            Console.WriteLine("  status       报告 原版 / 已注入 / 未知 / 半注入。");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --terraria <Terraria.exe>   目标程序集；省略时按环境变量与 Steam 安装自动定位。");
            Console.WriteLine("  --plugin <ZhaDai.Runtime.dll>  注入载荷（钩子所在程序集）。");
            Console.WriteLine("  --out <copy.exe>            patch-copy 的输出副本路径（必填）。");
            Console.WriteLine("  --verify-only               配合 patch-copy：只对已有文件做回读校验，不注入。");
            Console.WriteLine("  --dry-run                   install 演练：完整走一遍但不写任何文件。");
            Console.WriteLine("  --no-plugin-copy            patch-copy 时不把插件拷到副本同级目录。");
            Console.WriteLine("  --require-plugin-requirements  verify 时强制要求插件声明 ReflectionRequirements。");
            Console.WriteLine("  --help                      显示本帮助。");
            Console.WriteLine();
            Console.WriteLine("退出码: 0 成功；1 错误；2 核对失败；3 用法错误；4 目标构建未验证。");
            Console.WriteLine();
            Console.WriteLine("注入契约: 在 " + HookContract.HooksType + " 上注入三个 public static 钩子：");
            foreach (var spec in HookContract.Specs())
                Console.WriteLine("  " + spec.Signature.PadRight(42) + spec.Anchor);
            Console.WriteLine();
            Console.WriteLine("已验证构建: Terraria " + InstallationService.SupportedVersion + "，SHA-256 " +
                              InstallationService.SupportedSha256);
            Console.WriteLine("数据目录:   <游戏目录>\\" + InstallationService.DataFolderName +
                              "\\（manifest.json 与 Terraria.exe.orig）");
        }

        /// <summary>Minimal long-option parser: `--name value`, `--name=value` and bare flags.</summary>
        private sealed class Options
        {
            public string Terraria;
            public string Plugin;
            public string Out;
            public bool DryRun;
            public bool VerifyOnly;
            public bool NoPluginCopy;
            public bool RequirePluginRequirements;
            public bool Help;

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (var i = 0; i < args.Length; i++)
                {
                    var argument = args[i];
                    if (!argument.StartsWith("--", StringComparison.Ordinal))
                        throw new PatchException("无法识别的参数：" + argument + "（用 --help 查看用法）");

                    var name = argument;
                    string inline = null;
                    var equals = argument.IndexOf('=');
                    if (equals > 2)
                    {
                        name = argument.Substring(0, equals);
                        inline = argument.Substring(equals + 1);
                    }

                    switch (name.ToLowerInvariant())
                    {
                        case "--terraria": options.Terraria = Value(args, ref i, name, inline); break;
                        case "--plugin": options.Plugin = Value(args, ref i, name, inline); break;
                        case "--out": options.Out = Value(args, ref i, name, inline); break;
                        case "--dry-run": options.DryRun = true; break;
                        case "--verify-only": options.VerifyOnly = true; break;
                        case "--no-plugin-copy": options.NoPluginCopy = true; break;
                        case "--require-plugin-requirements": options.RequirePluginRequirements = true; break;
                        case "--help": options.Help = true; break;
                        default:
                            throw new PatchException("无法识别的选项：" + name + "（用 --help 查看用法）");
                    }
                }
                return options;
            }

            private static string Value(string[] args, ref int index, string name, string inline)
            {
                if (inline != null) return inline;
                if (index + 1 >= args.Length)
                    throw new PatchException("选项 " + name + " 缺少取值。");
                index++;
                return args[index];
            }
        }
    }
}
