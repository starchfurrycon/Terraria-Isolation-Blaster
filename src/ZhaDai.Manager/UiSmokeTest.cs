using System.Globalization;
using System.Text;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Manager;

/// <summary>
/// Headless renderer for the window. It never shows a window: it builds the form, forces a layout
/// pass, and paints each state straight onto a bitmap through the same <c>Render(Graphics)</c>
/// entry point the window uses. <c>DrawToBitmap</c> is deliberately avoided because it goes through
/// the window manager and needs a real window.
/// </summary>
internal static class UiSmokeTest
{
    private const int ExitOk = 0;
    private const int ExitFailure = 2;

    /// <summary>Synthetic world dimensions: big enough that the band is a small part of the map.</summary>
    private const int WorldWidth = 1600;
    private const int WorldHeight = 900;

    private static readonly List<string> Report = [];
    private static int _failures;

    public static int Run(string directory)
    {
        try
        {
            string output = Path.GetFullPath(directory);
            Directory.CreateDirectory(output);
            Console.WriteLine("界面自检：输出目录 " + output);

            LoadedWorld world = BuildSyntheticWorld();
            BlastPlan plan = BlastPlanner.Plan(world);
            bool genuinelySealed = plan.Summary.AllSectionsSealed;
            Note(string.Format(
                CultureInfo.InvariantCulture,
                "合成世界 {0}x{1}：隔离段 {2}、封带 {3} 格、雷管 {4} 发、封住 {5} 格、泛洪复核 {6}",
                world.Metadata.Width,
                world.Metadata.Height,
                plan.Summary.Sections,
                plan.Summary.FenceTiles,
                plan.Summary.Charges,
                plan.Summary.EnclosedSeedTiles,
                genuinelySealed ? "全封住" : "有段未封住"));

            string worldPath = Path.Combine(output, "synthetic-world.wld");

            // Base state: no world chosen, no plan yet.
            using (MainForm form = new())
            {
                form.SetWorldDirectory(Path.Combine(output, "no-worlds-here"));
                Capture(form, output, "ui-smoke-empty", "空状态 · 未选存档");
            }

            // Planning state: the background read is in flight.
            using (MainForm form = new())
            {
                form.SetWorldDirectory(output);
                form.ApplySmokeState(
                    presentation: null,
                    plan: null,
                    worldPath: worldPath,
                    running: true,
                    message: "读档中…");
                Thread.Sleep(120);
                Capture(form, output, "ui-smoke-planning", "规划中 · 读档与规划在后台线程");
            }

            // Both completion verdicts come from one real Plan call on the synthetic world; the
            // unsealed variant is the same plan with the flood verification verdict flipped, which
            // is the only way to paint that state without a genuinely leaking world on disk.
            PlanPresentation sealedPresentation = PlanPresentation.FromPlan(plan) with
            {
                ReadMilliseconds = 812,
                PlanMilliseconds = 3174,
            };

            PlanPresentation unsealedPresentation = sealedPresentation with
            {
                AllSectionsSealed = false,
                ReadMilliseconds = 795,
                PlanMilliseconds = 3288,
            };

            using (MainForm form = new())
            {
                form.SetWorldDirectory(output);
                form.SetExtraRectsText("1000,120,1060,300\n4200,300,4380,520");
                form.ApplySmokeState(
                    sealedPresentation,
                    RebuildSummary(plan, sealedPresentation),
                    worldPath,
                    running: false,
                    message: "规划完成 · 读档 812 ms · 规划 3174 ms · 合成世界");
                Capture(form, output, "ui-smoke-plan-sealed", "规划完成 · 全封住");
            }

            using (MainForm form = new())
            {
                form.SetWorldDirectory(output);
                form.ApplySmokeState(
                    unsealedPresentation,
                    RebuildSummary(plan, unsealedPresentation),
                    worldPath,
                    running: false,
                    message: "规划完成 · 读档 795 ms · 规划 3288 ms · 有段未封住");
                Capture(form, output, "ui-smoke-plan-unsealed", "规划完成 · 有段未封住");
            }

            // Panel state: hazards and the grass-front warning, plus the disabled plugin tooltip.
            PlanPresentation hazardPresentation = PlanPresentation.ForDisplay(
                worldLine: "踽踽独行 · 8400×2400 · 困难模式 · 腐化",
                evilSeeds: 18342,
                hallowSeeds: 4127,
                infectionNodes: 486213,
                sections: 23,
                fenceTiles: 58140,
                charges: 962,
                dynamiteStacks: 10,
                enclosedSeedTiles: 22469,
                allSectionsSealed: true,
                estimatedMinutes: 128.3,
                lavaTiles: 214,
                waterTiles: 1903,
                trapTiles: 37,
                gravestoneTiles: 96,
                explosivesTiles: 4,
                vineAnchorTiles: 318,
                readMilliseconds: 1184,
                planMilliseconds: 4210);

            using (MainForm form = new())
            {
                form.SetWorldDirectory(output);
                form.SetExtraRectsText("1000,120,1060,300");
                form.ApplySmokeState(
                    hazardPresentation,
                    RebuildSummary(plan, hazardPresentation),
                    worldPath,
                    running: false,
                    message: "危险物块与带草前沿清单已展开。");
                form.ShowDisabledPluginTooltip();
                Capture(form, output, "ui-smoke-panel-hazards", "危险物块与带草前沿告警 · 禁用按钮提示");
            }

            Report.Add(_failures == 0 ? "界面自检通过。" : string.Format(CultureInfo.InvariantCulture, "界面自检失败 {0} 项。", _failures));
            File.WriteAllText(
                Path.Combine(output, "ui-smoke.txt"),
                string.Join(Environment.NewLine, Report) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (string line in Report)
            {
                Console.WriteLine(line);
            }

            return _failures == 0 ? ExitOk : ExitFailure;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            Console.Error.WriteLine("界面自检失败：" + ex);
            return ExitFailure;
        }
    }

    private static void Capture(MainForm form, string directory, string tag, string description)
    {
        form.PerformLayout();
        Size size = form.ClientSize;
        using Bitmap bitmap = new(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(UiTheme.Canvas);
            form.Render(graphics);
        }

        string path = Path.Combine(directory, tag + ".png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Inspect(bitmap, tag, description);
        ExpectInk(form, bitmap, tag, description);

        // The placeholder is drawn as geometry, not text, so it gets its own ink check: an empty
        // state that renders nothing but its outline is still a broken empty state.
        if (form.IsEmptyState)
        {
            Rectangle diagram = Rectangle.Intersect(
                form.EmptyDiagramBox(form.CurrentLayout()),
                new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            Require(tag, description, "空状态的示意图里没有任何色块", CountInk(bitmap, diagram) >= 100);
        }

        Note(string.Format(CultureInfo.InvariantCulture, "{0}：{1} · {2}", tag, description, path));
    }

    /// <summary>Counts pixels in the region that are neither white panel nor canvas.</summary>
    private static int CountInk(Bitmap bitmap, Rectangle area)
    {
        int hits = 0;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (!Near(pixel, UiTheme.Surface, 8) && !Near(pixel, UiTheme.Canvas, 8))
                {
                    hits++;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Cheap sanity checks on the rendered pixels. A blank or all-black image is the failure mode
    /// this guards against, so the assertions are about ink actually existing.
    /// </summary>
    private static void Inspect(Bitmap bitmap, string tag, string description)
    {
        int canvas = 0;
        int surface = 0;
        int border = 0;
        int accent = 0;
        int text = 0;
        HashSet<int> colors = [];

        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                Color pixel = bitmap.GetPixel(x, y);
                colors.Add(pixel.ToArgb());
                if (Near(pixel, UiTheme.Canvas, 3))
                {
                    canvas++;
                }
                else if (Near(pixel, UiTheme.Surface, 3))
                {
                    surface++;
                }
                else if (Near(pixel, UiTheme.BorderColor, 24))
                {
                    border++;
                }

                if (Near(pixel, UiTheme.Accent, 40))
                {
                    accent++;
                }

                if (Near(pixel, UiTheme.TextColor, 60))
                {
                    text++;
                }
            }
        }

        int sampled = ((bitmap.Width + 1) / 2) * ((bitmap.Height + 1) / 2);
        double canvasPercent = 100d * canvas / sampled;
        double surfacePercent = 100d * surface / sampled;
        double borderPercent = 100d * border / sampled;

        // Thresholds are deliberately loose: the point is to catch a blank, all-black or
        // single-colour image, not to pin the exact pixel mix. The label ink check below is the
        // assertion that actually proves the layout was painted.
        Require(tag, description, "画布占比过高（面板和文字都没画出来）", canvasPercent < 70d);
        Require(tag, description, "面板底色占比过高（可能只有一个大色块）", surfacePercent < 95d);
        Require(tag, description, "边框占比过高（可能是纯色块误渲染）", borderPercent < 40d);
        Require(tag, description, "缺少强调色（主按钮或强调文字没画出来）", accent >= 60);
        Require(tag, description, "颜色数过少（可能是纯色块误渲染）", colors.Count >= 8);

        // A blank or all-black image has no canvas left at all.
        Require(tag, description, "画布色完全没有出现（图可能是空白或全黑）", canvas > 0);

        Note(string.Format(
            CultureInfo.InvariantCulture,
            "  像素统计 · 画布 {0:0.0}% · 面板 {1:0.0}% · 边框 {2:0.0}% · 强调 {3} 点 · 主文字 {4} 点 · {5} 种颜色",
            canvasPercent,
            surfacePercent,
            borderPercent,
            accent,
            text,
            colors.Count));
    }

    /// <summary>
    /// Requires every short line the window claims to draw to have actually put ink on the bitmap.
    /// The rectangles come from the window itself, so this catches a text path that silently paints
    /// nothing (the classic off-screen <c>DrawString</c> failure) rather than a missing label.
    /// </summary>
    private static void ExpectInk(MainForm form, Bitmap bitmap, string tag, string description)
    {
        List<string> missing = [];
        int checkedLabels = 0;
        foreach (UiLabel label in form.ExpectedLabels())
        {
            if (label.Text.Length == 0)
            {
                continue;
            }

            Rectangle area = Rectangle.Intersect(label.Bounds, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (area.Width <= 0 || area.Height <= 0)
            {
                continue;
            }

            checkedLabels++;
            if (!HasInk(bitmap, area))
            {
                missing.Add(label.Text);
            }
        }

        Require(tag, description, "以下位置没有画出任何内容：" + string.Join("、", missing), missing.Count == 0);
        Require(
            tag,
            description,
            "有内容的短句太少：只画出了 " + (checkedLabels - missing.Count) + "/" + checkedLabels + " 处",
            checkedLabels > 0 && (checkedLabels - missing.Count) * 10 >= checkedLabels * 9);
        Note(string.Format(
            CultureInfo.InvariantCulture,
            "  预期短句 {0} 项 · 有内容的 {1} 项",
            checkedLabels,
            checkedLabels - missing.Count));
    }

    /// <summary>
    /// True when the region carries at least a few pixels that are neither the white panel colour
    /// nor the canvas colour, i.e. something with contrast was actually painted there. Comparing
    /// against the two background colours rather than "differs from its neighbour" is what keeps
    /// anti-aliased glyph edges from being mistaken for background.
    /// </summary>
    private static bool HasInk(Bitmap bitmap, Rectangle area)
    {
        int hits = 0;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                bool background = Near(pixel, UiTheme.Surface, 8) || Near(pixel, UiTheme.Canvas, 8);
                if (!background)
                {
                    hits++;
                    if (hits >= 8)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool Near(Color left, Color right, int tolerance) =>
        Math.Abs(left.R - right.R) <= tolerance &&
        Math.Abs(left.G - right.G) <= tolerance &&
        Math.Abs(left.B - right.B) <= tolerance;

    private static void Require(string tag, string description, string message, bool condition)
    {
        if (condition)
        {
            return;
        }

        _failures++;
        Note("  失败  " + tag + "（" + description + "）：" + message);
    }

    private static void Note(string line)
    {
        Report.Add(line);
        Console.WriteLine(line);
    }

    /// <summary>
    /// A stone world with a diagonal run of Ebonstone, i.e. the shape a vertical fence handles
    /// badly. This is the pattern the core test runner builds by hand, scaled up so the panel has
    /// realistic numbers to display.
    /// </summary>
    private static LoadedWorld BuildSyntheticWorld()
    {
        TileGrid tiles = new(WorldWidth, WorldHeight);
        tiles.FillRect(0, 0, WorldWidth - 1, WorldHeight - 1, TileIds.Stone);

        for (int i = 0; i < 160; i++)
        {
            tiles.Set(300 + i, 300 + i, TileIds.Ebonstone);
        }

        for (int i = 0; i < 40; i++)
        {
            tiles.Set(900 + i, 150 + (i % 7), TileIds.Pearlstone);
        }

        WorldMetadata metadata = new(
            Version: 326,
            Title: "合成世界",
            Seed: "0",
            WorldId: 1,
            Width: WorldWidth,
            Height: WorldHeight,
            SpawnX: WorldWidth / 2,
            SpawnY: 120,
            WorldSurface: WorldHeight * 0.30,
            RockLayer: WorldHeight * 0.60,
            DungeonX: WorldWidth - 120,
            IsCrimson: false,
            HardMode: true,
            DrunkWorld: false,
            GetGoodWorld: false,
            RemixWorld: false,
            SkyblockWorld: false,
            NoTraps: false,
            ZenithWorld: false);
        return new LoadedWorld(metadata, tiles);
    }

    /// <summary>
    /// Rebuilds a <see cref="BlastPlan"/> whose summary matches what the panel is showing, so the
    /// plan kept for exports and the painted lines cannot disagree.
    /// </summary>
    private static BlastPlan RebuildSummary(BlastPlan source, PlanPresentation presentation)
    {
        BlastPlanSummary summary = new(
            InfectionNodes: presentation.InfectionNodes,
            EvilSeeds: presentation.EvilSeeds,
            HallowSeeds: presentation.HallowSeeds,
            Sections: presentation.Sections,
            MixedSections: 0,
            FenceTiles: presentation.FenceTiles,
            Charges: presentation.Charges,
            DynamiteStacks: presentation.DynamiteStacks,
            SeedsDestroyedByBlast: 0,
            EnclosedSeedTiles: presentation.EnclosedSeedTiles,
            BlastImmuneNodesInFence: 0,
            LavaTilesInFence: presentation.LavaTiles,
            WaterTilesInFence: presentation.WaterTiles,
            TrapTilesInFence: presentation.TrapTiles,
            GravestoneTilesInFence: presentation.GravestoneTiles,
            ExplosivesTilesInFence: presentation.ExplosivesTiles,
            BlastDestroyedTiles: 0,
            BlastImmuneTilesInBlast: 0,
            AllSectionsSealed: presentation.AllSectionsSealed,
            VineAnchorTiles: presentation.VineAnchorTiles,
            EstimatedPlayerSeconds: (long)Math.Round(presentation.EstimatedMinutes * 60d));

        return new BlastPlan(
            source.World,
            source.Options,
            summary,
            source.Sections,
            source.Charges,
            source.Notes,
            source.Overview);
    }
}
