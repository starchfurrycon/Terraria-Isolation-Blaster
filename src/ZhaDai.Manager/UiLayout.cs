using ZhaDai.Core.Planning;

namespace ZhaDai.Manager;

/// <summary>
/// One interactive hit target. Paint and hit testing both read the same rectangles out of
/// <see cref="UiLayout"/>, so the visual and the clickable region can never drift apart.
/// </summary>
internal sealed class UiZone
{
    public required Rectangle Bounds { get; init; }

    /// <summary>What a click on this zone does.</summary>
    public required UiAction Action { get; init; }

    /// <summary>Index of the row or knob the action belongs to; <c>-1</c> when unused.</summary>
    public int Index { get; init; } = -1;

    public bool Contains(Point point) => Bounds.Contains(point);
}

/// <summary>
/// A short line the window draws, together with the rectangle it is drawn in. The renderer and the
/// smoke test's "is anything painted here" check both use these, so the two cannot drift.
/// </summary>
internal readonly record struct UiLabel(string Text, Rectangle Bounds);

/// <summary>Every clickable surface of the window.</summary>
internal enum UiAction
{
    None,
    Close,
    Minimise,
    Refresh,
    Browse,
    SelectWorld,
    KnobMinus,
    KnobPlus,
    KnobField,
    ToggleExtra,
    BeginPlan,
    OpenMap,
    ExportJson,
    ExportExecution,
    InstallPlugin,
}

/// <summary>Every rectangle the renderer needs, computed once per layout pass.</summary>
internal sealed class UiLayout
{
    public Rectangle Client { get; init; }

    public Rectangle Header { get; init; }

    public Rectangle WorldCard { get; init; }

    public Rectangle WorldTitleRow { get; init; }

    public Rectangle WorldDirRow { get; init; }

    public Rectangle WorldListBox { get; init; }

    public Rectangle BrowseButton { get; init; }

    public Rectangle RefreshButton { get; init; }

    public Rectangle KnobCard { get; init; }

    public Rectangle KnobTitleRow { get; init; }

    public Rectangle ExtraCard { get; init; }

    public Rectangle ExtraTitleRow { get; init; }

    public Rectangle ExtraField { get; init; }

    public Rectangle ResultCard { get; init; }

    public Rectangle ResultTitleRow { get; init; }

    public Rectangle ResultBody { get; init; }

    public Rectangle ResultWarning { get; init; }

    public Rectangle ActionCard { get; init; }

    public Rectangle ActionTitleRow { get; init; }

    public Rectangle ActionStatusRow { get; init; }

    public Rectangle ProgressBar { get; init; }

    public Rectangle ProgressLabel { get; init; }

    public Rectangle CloseButton { get; init; }

    public Rectangle MinimiseButton { get; init; }

    public Rectangle Grip { get; init; }

    public List<Rectangle> WorldRows { get; } = [];

    public List<Rectangle> KnobRows { get; } = [];

    public List<Rectangle> KnobMinus { get; } = [];

    public List<Rectangle> KnobPlus { get; } = [];

    public List<Rectangle> KnobField { get; } = [];

    public List<Rectangle> ActionButtons { get; } = [];

    public List<UiZone> Zones { get; } = [];

    public bool ExtraExpanded { get; init; }

    public int VisibleWorldRows { get; init; }

    public Rectangle WorldRow(int index) => WorldRows[index];

    /// <summary>
    /// Derives every rectangle from the client size. The left column is a fixed width so the world
    /// list keeps its shape; the result panel takes the rest.
    /// </summary>
    public static UiLayout Build(Size client, bool extraExpanded, IReadOnlyList<string> knobLabels, int worldCount)
    {
        Rectangle box = new(0, 0, client.Width, client.Height);
        Rectangle header = new(0, 0, client.Width, UiTheme.HeaderHeight);
        int closeWidth = 34;
        int closeHeight = 24;
        Rectangle close = new(
            client.Width - UiTheme.Margin - closeWidth,
            (UiTheme.HeaderHeight - closeHeight) / 2,
            closeWidth,
            closeHeight);
        Rectangle minimise = new(close.X - closeWidth - 2, close.Y, closeWidth, closeHeight);

        int contentTop = UiTheme.HeaderHeight + UiTheme.Margin;
        int contentBottom = client.Height - UiTheme.Margin;
        int sidebarWidth = Math.Clamp(client.Width / 3, 360, 420);
        int left = UiTheme.Margin;
        int sidebarHeight = Math.Max(120, contentBottom - contentTop);
        int extraHeight = extraExpanded ? 132 : 44;
        int knobHeight = 214 + (extraExpanded ? 88 : 0);
        knobHeight = Math.Min(knobHeight, Math.Max(120, sidebarHeight - 90));
        int worldHeight = Math.Max(120, sidebarHeight - knobHeight - UiTheme.Gap);

        Rectangle worldCard = new(left, contentTop, sidebarWidth, worldHeight);
        Rectangle worldTitleRow = new(worldCard.X + UiTheme.Padding, worldCard.Y + 8, worldCard.Width - (UiTheme.Padding * 2), 18);
        Rectangle worldDirRow = new(worldTitleRow.X, worldTitleRow.Bottom + 1, worldTitleRow.Width, 15);

        int browseWidth = 62;
        int refreshWidth = 46;
        Rectangle browse = new(worldTitleRow.Right - browseWidth, worldTitleRow.Y - 1, browseWidth, 20);
        Rectangle refresh = new(browse.X - refreshWidth - 6, browse.Y, refreshWidth, 20);

        int listTop = worldDirRow.Bottom + 6;
        Rectangle worldList = new(
            worldCard.X + UiTheme.Padding,
            listTop,
            worldCard.Width - (UiTheme.Padding * 2),
            Math.Max(24, worldCard.Bottom - UiTheme.Padding - listTop));

        Rectangle knobCard = new(left, worldCard.Bottom + UiTheme.Gap, sidebarWidth, knobHeight);
        Rectangle knobTitleRow = new(knobCard.X + UiTheme.Padding, knobCard.Y + 7, knobCard.Width - (UiTheme.Padding * 2), 16);
        Rectangle extraCard = new(
            knobCard.X + UiTheme.Padding,
            knobCard.Bottom - extraHeight - UiTheme.Padding,
            knobCard.Width - (UiTheme.Padding * 2),
            extraHeight);
        Rectangle extraTitleRow = new(extraCard.X + 6, extraCard.Y + 2, extraCard.Width - 12, 18);
        Rectangle extraField = new(extraCard.X + 6, extraTitleRow.Bottom + 2, extraCard.Width - 12, Math.Max(0, extraCard.Height - 26));

        int rightX = left + sidebarWidth + UiTheme.Gap + 4;
        int rightWidth = Math.Max(280, client.Width - UiTheme.Margin - rightX);
        int actionHeight = 96;
        Rectangle actionCard = new(rightX, contentTop, rightWidth, actionHeight);
        Rectangle actionTitleRow = new(actionCard.X + UiTheme.Padding, actionCard.Y + 7, actionCard.Width - (UiTheme.Padding * 2), 18);
        Rectangle resultCard = new(
            rightX,
            actionCard.Bottom + UiTheme.Gap,
            rightWidth,
            Math.Max(120, sidebarHeight - actionHeight - UiTheme.Gap));
        Rectangle resultTitleRow = new(resultCard.X + UiTheme.Padding, resultCard.Y + 8, resultCard.Width - (UiTheme.Padding * 2), 18);
        Rectangle progressBar = new(resultTitleRow.X, resultTitleRow.Bottom + 3, Math.Min(210, resultTitleRow.Width / 2), 5);
        Rectangle progressLabel = new(progressBar.Right + 8, resultTitleRow.Y, resultTitleRow.Right - progressBar.Right - 8, 18);
        Rectangle resultBody = new(
            resultTitleRow.X,
            resultTitleRow.Bottom + 14,
            resultTitleRow.Width,
            Math.Max(40, resultCard.Height - 40 - 94));
        Rectangle resultWarning = new(
            resultTitleRow.X - 4,
            resultBody.Bottom + 6,
            resultTitleRow.Width + 8,
            Math.Max(0, resultCard.Bottom - UiTheme.Padding - resultBody.Bottom - 6));

        // World rows: 34px each, as many as fit in the list box.
        int visibleWorldRows = Math.Min(worldCount, Math.Max(1, (worldList.Height - 4) / 34));

        UiLayout layout = new()
        {
            Client = box,
            Header = header,
            WorldCard = worldCard,
            WorldTitleRow = worldTitleRow,
            WorldDirRow = worldDirRow,
            WorldListBox = worldList,
            BrowseButton = browse,
            RefreshButton = refresh,
            KnobCard = knobCard,
            KnobTitleRow = knobTitleRow,
            ExtraCard = extraCard,
            ExtraTitleRow = extraTitleRow,
            ExtraField = extraField,
            ResultCard = resultCard,
            ResultTitleRow = resultTitleRow,
            ResultBody = resultBody,
            ResultWarning = resultWarning,
            ActionCard = actionCard,
            ActionTitleRow = actionTitleRow,
            ActionStatusRow = new Rectangle(actionCard.X + UiTheme.Padding, actionCard.Y + 46, actionCard.Width - (UiTheme.Padding * 2), 40),
            ProgressBar = progressBar,
            ProgressLabel = progressLabel,
            CloseButton = close,
            MinimiseButton = minimise,
            Grip = new Rectangle(client.Width - 18, client.Height - 18, 14, 14),
            ExtraExpanded = extraExpanded,
            VisibleWorldRows = visibleWorldRows,
        };

        // Knob rows: a compact label plus a three part stepper, one row per knob.
        int rowHeight = extraExpanded ? 26 : 30;
        int rowTop = knobTitleRow.Bottom + 4;
        for (int i = 0; i < knobLabels.Count; i++)
        {
            Rectangle row = new(knobCard.X + UiTheme.Padding, rowTop + (i * rowHeight), knobCard.Width - (UiTheme.Padding * 2), rowHeight - 4);
            int fieldWidth = 74;
            int stepWidth = 22;
            layout.KnobRows.Add(row);
            layout.KnobPlus.Add(new Rectangle(row.Right - stepWidth, row.Y + 2, stepWidth, row.Height - 4));
            layout.KnobField.Add(new Rectangle(row.Right - stepWidth - fieldWidth - 3, row.Y + 2, fieldWidth, row.Height - 4));
            layout.KnobMinus.Add(new Rectangle(row.Right - stepWidth - fieldWidth - 3 - stepWidth - 3, row.Y + 2, stepWidth, row.Height - 4));
        }

        // Action buttons across the top of the action card.
        int buttonTop = actionTitleRow.Bottom + 4;
        int buttonWidth = Math.Clamp((actionCard.Width - (UiTheme.Padding * 2) - (5 * 6)) / 5, 96, 190);
        for (int i = 0; i < 5; i++)
        {
            layout.ActionButtons.Add(new Rectangle(
                actionCard.X + UiTheme.Padding + (i * (buttonWidth + 6)),
                buttonTop,
                buttonWidth,
                Math.Max(24, actionCard.Height - (buttonTop - actionCard.Y) - 42)));
        }

        // World rows: one entry per visible file.
        for (int i = 0; i < visibleWorldRows; i++)
        {
            layout.WorldRows.Add(new Rectangle(worldList.X, worldList.Y + 2 + (i * 34), worldList.Width, 32));
        }

        layout.BuildZones();
        return layout;
    }

    private void BuildZones()
    {
        Zones.Add(new UiZone { Bounds = CloseButton, Action = UiAction.Close });
        Zones.Add(new UiZone { Bounds = MinimiseButton, Action = UiAction.Minimise });
        Zones.Add(new UiZone { Bounds = BrowseButton, Action = UiAction.Browse });
        Zones.Add(new UiZone { Bounds = RefreshButton, Action = UiAction.Refresh });
        Zones.Add(new UiZone { Bounds = ExtraTitleRow, Action = UiAction.ToggleExtra });

        for (int i = 0; i < WorldRows.Count; i++)
        {
            Zones.Add(new UiZone { Bounds = WorldRows[i], Action = UiAction.SelectWorld, Index = i });
        }

        for (int i = 0; i < KnobRows.Count; i++)
        {
            Zones.Add(new UiZone { Bounds = KnobMinus[i], Action = UiAction.KnobMinus, Index = i });
            Zones.Add(new UiZone { Bounds = KnobPlus[i], Action = UiAction.KnobPlus, Index = i });
            Zones.Add(new UiZone { Bounds = KnobField[i], Action = UiAction.KnobField, Index = i });
        }

        UiAction[] actions =
        [
            UiAction.BeginPlan,
            UiAction.OpenMap,
            UiAction.ExportJson,
            UiAction.ExportExecution,
            UiAction.InstallPlugin,
        ];
        for (int i = 0; i < ActionButtons.Count && i < actions.Length; i++)
        {
            Zones.Add(new UiZone { Bounds = ActionButtons[i], Action = actions[i], Index = i });
        }
    }
}

/// <summary>
/// The displayed slice of a plan. The window keeps the real <see cref="BlastPlan"/> for exports;
/// this carries exactly the short lines the panel shows, so the smoke test can render every state
/// without a world file.
/// </summary>
internal sealed record PlanPresentation(
    string WorldLine,
    int EvilSeeds,
    int HallowSeeds,
    int InfectionNodes,
    int Sections,
    int FenceTiles,
    int Charges,
    int DynamiteStacks,
    int EnclosedSeedTiles,
    bool AllSectionsSealed,
    double EstimatedMinutes,
    int LavaTiles,
    int WaterTiles,
    int TrapTiles,
    int GravestoneTiles,
    int ExplosivesTiles,
    int VineAnchorTiles,
    long ReadMilliseconds,
    long PlanMilliseconds)
{
    /// <summary>Projects a real plan onto the panel's short line format.</summary>
    public static PlanPresentation FromPlan(BlastPlan plan)
    {
        BlastPlanSummary s = plan.Summary;
        return new PlanPresentation(
            WorldLine: string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} · {1}×{2} · {3} · {4}",
                plan.World.Title,
                plan.World.Width,
                plan.World.Height,
                plan.World.HardMode ? "困难模式" : "肉前",
                plan.World.IsCrimson ? "猩红" : "腐化"),
            EvilSeeds: s.EvilSeeds,
            HallowSeeds: s.HallowSeeds,
            InfectionNodes: s.InfectionNodes,
            Sections: s.Sections,
            FenceTiles: s.FenceTiles,
            Charges: s.Charges,
            DynamiteStacks: s.DynamiteStacks,
            EnclosedSeedTiles: s.EnclosedSeedTiles,
            AllSectionsSealed: s.AllSectionsSealed,
            EstimatedMinutes: Math.Round(s.EstimatedPlayerSeconds / 60d, 1),
            LavaTiles: s.LavaTilesInFence,
            WaterTiles: s.WaterTilesInFence,
            TrapTiles: s.TrapTilesInFence,
            GravestoneTiles: s.GravestoneTilesInFence,
            ExplosivesTiles: s.ExplosivesTilesInFence,
            VineAnchorTiles: s.VineAnchorTiles,
            ReadMilliseconds: 0,
            PlanMilliseconds: 0);
    }

    /// <summary>
    /// Builds a presentation directly from summary numbers. Only the UI smoke test uses this, and
    /// only to paint a state (the hazard warnings) that a ten second synthetic world does not
    /// produce on its own.
    /// </summary>
    public static PlanPresentation ForDisplay(
        string worldLine,
        int evilSeeds,
        int hallowSeeds,
        int infectionNodes,
        int sections,
        int fenceTiles,
        int charges,
        int dynamiteStacks,
        int enclosedSeedTiles,
        bool allSectionsSealed,
        double estimatedMinutes,
        int lavaTiles,
        int waterTiles,
        int trapTiles,
        int gravestoneTiles,
        int explosivesTiles,
        int vineAnchorTiles,
        long readMilliseconds,
        long planMilliseconds) =>
        new(
            worldLine,
            evilSeeds,
            hallowSeeds,
            infectionNodes,
            sections,
            fenceTiles,
            charges,
            dynamiteStacks,
            enclosedSeedTiles,
            allSectionsSealed,
            estimatedMinutes,
            lavaTiles,
            waterTiles,
            trapTiles,
            gravestoneTiles,
            explosivesTiles,
            vineAnchorTiles,
            readMilliseconds,
            planMilliseconds);

    public double EnclosedPercent => InfectionNodes <= 0 ? 0d : 100d * EnclosedSeedTiles / InfectionNodes;

    /// <summary>The summary block, one short line per item, in the order the window shows them.</summary>
    public IReadOnlyList<string> SummaryLines() =>
    [
        "世界 · " + WorldLine,
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "感染源 · 邪恶 {0} 格 · 神圣 {1} 格",
            EvilSeeds,
            HallowSeeds),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "隔离段 · {0} 段",
            Sections),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "封带 · {0} 格",
            FenceTiles),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "雷管 · {0} 发 · {1} 组",
            Charges,
            DynamiteStacks),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "封住感染源 · {0} 格 · 占可感染物块 {1:0.###}%",
            EnclosedSeedTiles,
            EnclosedPercent),
        "泛洪复核 · " + (AllSectionsSealed ? "全封住" : "有段未封住"),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "预计施工 · 约 {0} 分钟",
            EstimatedMinutes),
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "危险物块 · 岩浆 {0} · 水 {1} · 陷阱 {2} · 墓碑 {3} · 爆炸物 {4}",
            LavaTiles,
            WaterTiles,
            TrapTiles,
            GravestoneTiles,
            ExplosivesTiles),
    ];

    /// <summary>One short warning line per non-zero hazard, plus the vine anchored front tiles.</summary>
    public IReadOnlyList<string> WarningLines()
    {
        List<string> lines = [];
        if (LavaTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "封带穿过岩浆 {0} 格：先处理下游液体再炸。", LavaTiles));
        }

        if (WaterTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "封带穿过水 {0} 格：炸开会放液，注意下游。", WaterTiles));
        }

        if (TrapTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "封带覆盖陷阱 {0} 格：施工前拆掉或断线。", TrapTiles));
        }

        if (GravestoneTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "封带覆盖墓碑 {0} 格：雷管可直接炸掉。", GravestoneTiles));
        }

        if (ExplosivesTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "封带覆盖爆炸物 {0} 格：会被雷管连锁引爆。", ExplosivesTiles));
        }

        if (VineAnchorTiles > 0)
        {
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "带草前沿物块 {0} 格：会长藤蔓，建议直接炸掉。", VineAnchorTiles));
        }

        return lines;
    }
}
