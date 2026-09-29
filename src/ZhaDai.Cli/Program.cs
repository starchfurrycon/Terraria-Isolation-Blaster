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

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            炸带 ZhaDai —— 原版泰拉瑞亚隔离带爆破规划器

            用法：
              zhaodai plan <world.wld> [选项]   读档并算出雷管施工方案
              zhaodai info <world.wld>          只读打印存档与感染源概况

            选项：
              --json=<路径>        写出完整方案 JSON（可重复）
              --html=<路径>        写出自包含交互地图（可重复）
              --zplan=<路径>       写出游戏内施工文件
              --clearance=<格数>   封带厚度，默认 6（不得小于 4）
              --merge-gap=<格数>   多少格以内的感染源并成同一段，默认 3；调大省炸药但封得更多
              --max-sections=<n>   允许的最大段数，默认 4000
              --vine-reach=<格数>  藤蔓能顺着空气向下带多远，默认 13；设 0 只按普通三格扩散封带
                                    （最快最省，但对会长藤蔓的带草前沿不保险）
              --also-rect=x0,y0,x1,y1
                                   额外把一块矩形当作感染源（可重复），
                                   用于把分析器给出的肉前 V 臂预测范围也封进去

            退出码：0 全部封住，1 用法错误，2 有段未封住或读档失败。

            这个命令只读存档，不修改世界、角色或地图文件。
            """);
    }

    private sealed class Options
    {
        public List<string> Positional { get; } = [];

        public List<string> JsonPaths { get; } = [];

        public List<string> HtmlPaths { get; } = [];

        public string? ExecutionPath { get; private set; }

        public int Clearance { get; private set; } = 6;

        public int MergeLinkDistance { get; private set; } = InfectionModel.SpreadReach;

        public int MaxSections { get; private set; } = 4000;

        public int VineReach { get; private set; } = InfectionModel.VineDownwardReach;

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
