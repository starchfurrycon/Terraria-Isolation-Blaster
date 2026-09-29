using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZhaDai.Core.Analysis;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Cli;

/// <summary>
/// Command line front end. Everything it prints is reproducible from the same world file, so a plan
/// is an artefact that can be re-derived and diffed rather than a one-off.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitFailure = 2;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        try
        {
            return args[0] switch
            {
                "plan" => RunPlan(args[1..]),
                "arm" => RunArm(args[1..]),
                "info" => RunInfo(args[1..]),
                _ => Fail($"未知命令：{args[0]}"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return ExitFailure;
        }
    }

    private static int RunInfo(string[] args)
    {
        Options options = Options.Parse(args);
        if (options.Positional.Count != 1)
        {
            return Fail("用法：zhaodai info <world.wld>");
        }

        LoadedWorld world = WorldFileReader.Read(options.Positional[0]);
        InfectionModel model = InfectionModel.Build(world);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0}：{1}x{2}，格式 {3}，{4}，{5}",
            world.Metadata.Title,
            world.Metadata.Width,
            world.Metadata.Height,
            world.Metadata.Version,
            world.Metadata.HardMode ? "困难模式" : "肉前",
            world.Metadata.IsCrimson ? "猩红世界" : "腐化世界"));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "可感染物块 {0} 格；邪恶感染源 {1} 格；神圣感染源 {2} 格",
            model.NodeCount,
            model.EvilSeedCount,
            model.HallowSeedCount));
        return ExitOk;
    }

    private static int RunPlan(string[] args)
    {
        Options options = Options.Parse(args);
        if (options.Positional.Count != 1)
        {
            return Fail("用法：zhaodai plan <world.wld> [--json=...] [--html=...] [--zplan=...]");
        }

        string worldPath = options.Positional[0];
        BlastPlanOptions planOptions = new()
        {
            Clearance = options.Clearance,
            MergeLinkDistance = options.MergeLinkDistance,
            MaxSections = options.MaxSections,
            VineReach = options.VineReach,
            ExtraSeedRects = options.ExtraSeedRects,
            Protection = options.Protection,
            ProtectionBuffer = options.ProtectionBuffer,
            PlugVines = options.PlugVines,
            PlugItemId = options.PlugItemId,
            PickPower = options.PickPower,
        };

        Stopwatch watch = Stopwatch.StartNew();
        LoadedWorld world = WorldFileReader.Read(worldPath);
        long readMs = watch.ElapsedMilliseconds;

        BlastPlan plan = BlastPlanner.Plan(world, planOptions);
        long planMs = watch.ElapsedMilliseconds - readMs;

        Console.Write(PlanWriter.ToTextReport(plan));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "耗时：读档 {0} ms，规划 {1} ms",
            readMs,
            planMs));

        foreach (string path in options.JsonPaths)
        {
            PlanWriter.WriteJson(plan, path);
            Console.WriteLine("已写出 JSON：" + Path.GetFullPath(path));
        }

        if (options.ExecutionPath is { } executionPath)
        {
            PlanWriter.WriteExecutionFile(plan, executionPath, worldPath);
            Console.WriteLine("已写出施工文件：" + Path.GetFullPath(executionPath));
        }

        foreach (string path in options.HtmlPaths)
        {
            PlanHtmlWriter.Write(plan, path, worldPath);
            Console.WriteLine("已写出地图：" + Path.GetFullPath(path));
        }

        return plan.Summary.AllSectionsSealed ? ExitOk : ExitFailure;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return ExitUsage;
    }

    /// <summary>
    /// Plans a world and leaves the runtime everything it needs to run it: the work order and a
    /// config file under the game's ZhaDai folder. Nothing here touches the game, the save or the
    /// executable; arming only prepares files, and the config is written disabled unless --go is
    /// passed, so planning never starts a run by accident.
    /// </summary>
    private static int RunArm(string[] args)
    {
        Options options = Options.Parse(args);
        if (options.Positional.Count != 1)
        {
            return Fail("用法：zhaodai arm <world.wld> [--game=<泰拉瑞亚目录>] [--go] [--force]");
        }

        string worldPath = options.Positional[0];
        string gameDirectory = ResolveGameDirectory(options.GameDirectory);

        BlastPlanOptions planOptions = new()
        {
            Clearance = options.Clearance,
            MergeLinkDistance = options.MergeLinkDistance,
            MaxSections = options.MaxSections,
            VineReach = options.VineReach,
            ExtraSeedRects = options.ExtraSeedRects,
            Protection = options.Protection,
            ProtectionBuffer = options.ProtectionBuffer,
            PlugVines = options.PlugVines,
            PlugItemId = options.PlugItemId,
            PickPower = options.PickPower,
        };

        LoadedWorld world = WorldFileReader.Read(worldPath);
        BlastPlan plan = BlastPlanner.Plan(world, planOptions);
        Console.Write(PlanWriter.ToTextReport(plan));

        if (!plan.Summary.AllSectionsSealed && !options.Force)
        {
            Console.Error.WriteLine(
                "有隔离段没过泛洪复核，不写出施工文件：照着炸了也可能漏。\n" +
                "先加大 --clearance 或 --vine-reach 重算；确实想照炸就加 --force。");
            return ExitFailure;
        }

        string dataDirectory = Path.Combine(gameDirectory, "ZhaDai");
        Directory.CreateDirectory(dataDirectory);

        const string PlanFileName = "active.zplan";
        string planPath = Path.Combine(dataDirectory, PlanFileName);
        PlanWriter.WriteExecutionFile(plan, planPath, worldPath);

        string configPath = Path.Combine(dataDirectory, "run.cfg");
        string[] config =
        [
            "# 炸带运行时配置。游戏里读到 enabled=1 就开始接管，改成 0 就停，不用重启游戏。",
            "# 本文件由 `zhaodai arm` 或者管理器界面写出。",
            "enabled=" + (options.Go ? "1" : "0"),
            "plan=" + PlanFileName,
            "allowExplosives=" + (options.AllowExplosives ? "1" : "0"),
            "maxDeaths=30",
            "hostileDistance=14",
        ];
        File.WriteAllLines(configPath, config, new UTF8Encoding(false));

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "已写出施工文件：{0}（{1} 发雷管，引信 {2} tick，撤离 {3} 格）",
            planPath,
            plan.Charges.Count,
            plan.Options.DynamiteFuseTicks,
            plan.Options.BlastRadius + plan.Options.RetreatMarginTiles));
        Console.WriteLine("已写出运行配置：" + configPath + "（enabled=" + (options.Go ? "1" : "0") + "）");
        Console.WriteLine();
        Console.WriteLine("接下来：");
        Console.WriteLine("  1. 用 zhaodai-patcher install 把接管插件装进游戏（装之前会自动备份 Terraria.exe）。");
        Console.WriteLine("  2. 进游戏读档；要开始就把 run.cfg 里的 enabled 改成 1（或者现在用 --go 重跑一次）。");
        Console.WriteLine("  3. 进度写在 " + Path.Combine(dataDirectory, "status.txt") + "，细节在 runtime.log。");
        Console.WriteLine("  备份没还原之前，随时把 enabled 改成 0 就能交还操作权。");
        return plan.Summary.AllSectionsSealed ? ExitOk : ExitFailure;
    }

    /// <summary>
    /// Finds the game folder. An explicit path wins, then the environment variable, then the usual
    /// Steam library locations; a candidate only counts if Terraria.exe is actually in it.
    /// </summary>
    private static string ResolveGameDirectory(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return RequireGameDirectory(explicitPath, "--game");
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("ZHAODAI_TERRARIA");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return RequireGameDirectory(fromEnvironment, "环境变量 ZHAODAI_TERRARIA");
        }

        string[] candidates =
        [
            @"D:\Program Files (x86)\Steam\steamapps\common\Terraria",
            @"C:\Program Files (x86)\Steam\steamapps\common\Terraria",
            @"C:\Program Files\Steam\steamapps\common\Terraria",
            @"D:\Steam\steamapps\common\Terraria",
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "Terraria.exe")))
            {
                return candidate;
            }
        }

        throw new ArgumentException(
            "找不到泰拉瑞亚目录。用 --game=<目录> 指定，或用环境变量 ZHAODAI_TERRARIA，目录里要有 Terraria.exe。");
    }

    private static string RequireGameDirectory(string path, string source)
    {
        string directory = File.Exists(path) && Path.GetFileName(path).Equals("Terraria.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(path))!
            : Path.GetFullPath(path);

        if (!File.Exists(Path.Combine(directory, "Terraria.exe")))
        {
            throw new ArgumentException($"{source} 指向的目录里没有 Terraria.exe：{directory}");
        }

        return directory;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            炸带 ZhaDai —— 原版泰拉瑞亚隔离带爆破规划器

            用法：
              zhaodai plan <world.wld> [选项]   读档并算出雷管施工方案
              zhaodai arm  <world.wld> [选项]   算方案并把施工文件与运行配置放进游戏目录
              zhaodai info <world.wld>          只读打印存档与感染源概况

            选项：
              --json=<路径>        写出完整方案 JSON（可重复）
              --html=<路径>        写出自包含交互地图（可重复）
              --zplan=<路径>       写出游戏内施工文件
              --clearance=<格数>   封带厚度，默认 6（不得小于 4）
              --merge-gap=<格数>   多少格以内的感染源并成同一段，默认 3；调大省炸药但封得更多
              --max-sections=<n>   允许的最大段数，默认 4000
              --vine-reach=<格数>  藤蔓能顺着空气向下带多远，默认 13；设 0 只按普通三格扩散封带
              --protect=<级别>     strict（默认，结构物、玩家建材与玩家墙都不炸，改挖）、structures、none
              --protect-buffer=<格> 保护范围外再留几格余量，默认 1（0..3）
              --no-plug            不用封堵块顶掉藤蔓竖井（默认用 1 块木头换掉 13 格竖井）
              --plug-item=<物品id> 封堵用的物品，默认 9（木材）
              --pick=<镐力>        规划时假设的镐力（默认 100，熔岩镐）；影响哪些格子只能靠镐子
                                    （最快最省，但对会长藤蔓的带草前沿不保险）
              --also-rect=x0,y0,x1,y1
                                   额外把一块矩形当作感染源（可重复），
                                   用于把分析器给出的肉前 V 臂预测范围也封进去

            arm 专有选项：
              --game=<目录>        泰拉瑞亚目录（里面有 Terraria.exe）。
                                   不写就先看环境变量 ZHAODAI_TERRARIA，再找常见 Steam 路径。
              --go                 写出配置时直接 enabled=1；默认是 0，需要你手动开
              --allow-explosives   允许爆破范围里有炸弹桶/爆炸物（默认跳过这类雷管）
              --force              即使有段没过泛洪复核也照样写出施工文件（默认拒绝）

            退出码：0 全部封住，1 用法错误，2 有段未封住或读档失败。

            这个命令只读存档，不修改世界、角色或地图文件；arm 只往游戏目录里写
            ZhaDai\active.zplan 和 ZhaDai\run.cfg，不会改 Terraria.exe。
            """);
    }

    private sealed class Options
    {
        public List<string> Positional { get; } = [];

        public List<string> JsonPaths { get; } = [];

        public List<string> HtmlPaths { get; } = [];

        public string? ExecutionPath { get; private set; }

        public string? GameDirectory { get; private set; }

        public bool Go { get; private set; }

        public bool AllowExplosives { get; private set; }

        public bool Force { get; private set; }

        public int Clearance { get; private set; } = 6;

        public int MergeLinkDistance { get; private set; } = InfectionModel.SpreadReach;

        public int MaxSections { get; private set; } = 4000;

        public int VineReach { get; private set; } = InfectionModel.VineDownwardReach;

        /// <summary>How much collateral damage a charge may do; see <see cref="ProtectionLevel"/>.</summary>
        public ProtectionLevel Protection { get; private set; } = ProtectionLevel.Strict;

        /// <summary>Extra margin around protected tiles; see <see cref="BlastPlanOptions.ProtectionBuffer"/>.</summary>
        public int ProtectionBuffer { get; private set; } = 1;

        /// <summary>Replace vine curtains with one inert block per anchor; see BlastPlanOptions.PlugVines.</summary>
        public bool PlugVines { get; private set; } = true;

        /// <summary>Item id used for the plugs, default wood (9).</summary>
        public int PlugItemId { get; private set; } = 9;

        /// <summary>Pick power the plan assumes when a tile has to be dug instead of blasted.</summary>
        public int PickPower { get; private set; } = TileCatalog.PreHardmodePickPower;

        public List<TileRect> ExtraSeedRects { get; } = [];

        public static Options Parse(string[] args)
        {
            Options options = new();
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Positional.Add(arg);
                    continue;
                }

                string[] parts = arg[2..].Split('=', 2);
                string name = parts[0];
                string? value = parts.Length > 1 ? parts[1] : null;

                switch (name)
                {
                    case "json":
                        options.JsonPaths.Add(Require(name, value));
                        break;
                    case "html":
                        options.HtmlPaths.Add(Require(name, value));
                        break;
                    case "zplan":
                        options.ExecutionPath = Require(name, value);
                        break;
                    case "game":
                        options.GameDirectory = Require(name, value);
                        break;
                    case "go":
                        options.Go = true;
                        break;
                    case "allow-explosives":
                        options.AllowExplosives = true;
                        break;
                    case "force":
                        options.Force = true;
                        break;
                    case "clearance":
                        options.Clearance = ParseInt(name, value);
                        break;
                    case "merge-gap":
                        options.MergeLinkDistance = ParseInt(name, value);
                        break;
                    case "max-sections":
                        options.MaxSections = ParseInt(name, value);
                        break;
                    case "vine-reach":
                        options.VineReach = ParseInt(name, value);
                        break;
                    case "protect":
                        options.Protection = ParseProtection(value);
                        break;
                    case "protect-buffer":
                        options.ProtectionBuffer = ParseInt(name, value);
                        break;
                    case "no-plug":
                        options.PlugVines = false;
                        break;
                    case "plug-item":
                        options.PlugItemId = ParseInt(name, value);
                        break;
                    case "pick":
                        options.PickPower = ParseInt(name, value);
                        break;
                    case "also-rect":
                        options.ExtraSeedRects.Add(ParseRect(value));
                        break;
                    default:
                        throw new ArgumentException($"未知选项：--{name}");
                }
            }

            return options;
        }

        private static string Require(string name, string? value) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"--{name} 需要一个路径。") : value;

        private static int ParseInt(string name, string? value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                throw new ArgumentException($"--{name} 需要一个整数。");
            }

            return parsed;
        }

        private static ProtectionLevel ParseProtection(string? value) => value switch
        {
            "strict" or null => ProtectionLevel.Strict,
            "structures" => ProtectionLevel.Structures,
            "none" => ProtectionLevel.None,
            _ => throw new ArgumentException("--protect 只认 strict、structures、none。"),
        };

        private static TileRect ParseRect(string? value)
        {
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 4)
            {
                throw new ArgumentException("--also-rect 需要 x0,y0,x1,y1 四个整数。");
            }

            int[] numbers = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    throw new ArgumentException("--also-rect 需要 x0,y0,x1,y1 四个整数。");
                }
            }

            return TileRect.FromPoints(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
    }
}
