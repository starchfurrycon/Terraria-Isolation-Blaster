using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZhaDai.Core.Analysis;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Manager;

/// <summary>
/// The whole application surface: a light, borderless, owner drawn window with no child controls
/// for the visuals. Layout, painting and hit testing all read the same <see cref="UiLayout"/>.
/// </summary>
internal sealed partial class MainForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeBorder = 7;

    private static readonly string[] KnobLabels = ["封带厚度", "合并距离", "藤蔓下探"];

    private readonly Stopwatch _watch = new();
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };

    /// <summary>
    /// One invisible helper: the keyboard and IME sink for the numeric fields and the extra seed
    /// rectangle list. It paints nothing; the editing state is drawn by <see cref="Render"/>.
    /// </summary>
    private readonly TextBox _editor = new();

    private UiLayout _layout = null!;
    private List<WorldEntry> _entries = [];
    private PlanPresentation? _presentation;
    private BlastPlan? _plan;
    private string _worldPath = string.Empty;
    private string _worldDirectory = WorldCatalog.DefaultDirectory;

    private int _clearance = 6;
    private int _mergeLinkDistance = 3;
    private int _vineReach = 13;
    private string _extraRectsText = string.Empty;

    private int _selectedWorld = -1;
    private Point _mouse = new(-1, -1);
    private int _editingKnob = -1;
    private bool _editingExtra;
    private bool _extraExpanded;
    private bool _blinkOn = true;
    private bool _running;
    private bool _lastSealed;
    private bool _showEmbeddedTooltip;

    private string _message = string.Empty;
    private string _planError = string.Empty;
    private long _readMilliseconds;
    private long _planMilliseconds;

    public MainForm(bool extraExpanded = false)
    {
        _extraExpanded = extraExpanded;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 700);
        MinimumSize = new Size(940, 620);
        Text = "炸带 · 隔离带爆破规划器";
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.TextColor;
        Font = UiTheme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
            true);

        _editor.Multiline = false;
        _editor.BorderStyle = BorderStyle.None;
        _editor.TabStop = false;
        _editor.Visible = false;
        _editor.Location = new Point(-200, -200);
        _editor.TextChanged += OnEditorTextChanged;
        _editor.KeyDown += OnEditorKeyDown;
        Controls.Add(_editor);

        _blink.Tick += (_, _) =>
        {
            if (_editingKnob < 0 && !_editingExtra)
            {
                _blink.Stop();
                return;
            }

            _blinkOn = !_blinkOn;
            Invalidate();
        };

        _progressTimer.Tick += (_, _) => Invalidate();
        _blink.Start();
        ResumeLayout(performLayout: true);

        ReloadWorlds();
    }

    // --- layout -------------------------------------------------------------------------------

    private void UpdateLayout()
    {
        _layout = UiLayout.Build(ClientSize, _extraExpanded, KnobLabels, _entries.Count);
    }

    private Rectangle[] ButtonBounds(UiLayout layout)
    {
        const int Count = 5;
        int left = layout.ActionCard.X + UiTheme.Padding;
        int right = layout.ActionCard.Right - UiTheme.Padding;
        int top = layout.ActionTitleRow.Bottom + 4;
        int bottom = layout.ActionCard.Bottom - 42;
        int height = Math.Max(26, bottom - top);
        int gap = 6;
        int width = Math.Max(84, (right - left - (gap * (Count - 1))) / Count);
        Rectangle[] result = new Rectangle[Count];
        for (int i = 0; i < Count; i++)
        {
            result[i] = new Rectangle(left + (i * (width + gap)), top, width, height);
        }

        return result;
    }

    // Every string drawn on the window has exactly one place that decides where it goes. The
    // renderer and the smoke test's "is anything painted here" check both read these helpers, so an
    // expectation can never drift away from the drawing.

    private static Rectangle HeaderBrandRow() => new(14, 10, 52, 22);

    private static Rectangle HeaderNameRow() => new(54, 10, 130, 22);

    private static Rectangle HeaderVersionRow() => new(150, 12, 110, 18);

    private static Rectangle HeaderHintRow(UiLayout layout) => new(layout.MinimiseButton.X - 262, 12, 250, 18);

    private string WorldDirectoryText() => _worldDirectory;

    private static Rectangle WorldDirectoryRow(UiLayout layout) => layout.WorldDirRow;

    private static IReadOnlyList<UiLabel> WorldEmptyLabels(UiLayout layout)
    {
        Rectangle row = new(layout.WorldListBox.X + 2, layout.WorldListBox.Y + 12, layout.WorldListBox.Width - 4, 22);
        return [new UiLabel("这个目录里没有 .wld 存档。", row)];
    }

    private static Rectangle WorldFileNameRow(UiLayout layout, int index)
    {
        Rectangle row = layout.WorldRow(index);
        return new Rectangle(row.X + 8, row.Y + 3, Math.Max(60, row.Width - 130), 14);
    }

    private static Rectangle WorldEntryTitleRow(UiLayout layout, int index)
    {
        Rectangle row = layout.WorldRow(index);
        int offset = Math.Clamp(row.Width / 3, 70, 150);
        return new Rectangle(row.X + 12 + offset, row.Y + 4, Math.Max(20, row.Width - offset - 132), 13);
    }

    private static Rectangle WorldSizeRow(UiLayout layout, int index)
    {
        Rectangle row = layout.WorldRow(index);
        return new Rectangle(row.X + 8, row.Y + 17, 90, 13);
    }

    private static Rectangle WorldModifiedRow(UiLayout layout, int index)
    {
        Rectangle row = layout.WorldRow(index);
        return new Rectangle(row.Right - 116, row.Y + 17, 108, 13);
    }

    private static string WorldTitle(WorldEntry entry) =>
        entry.Title is { Length: > 0 } ? entry.Title : "（未解析标题）";

    private static string KnobLabelText(int index) => KnobLabels[index];

    private static string KnobHintText(int index) => index switch
    {
        0 => "最小 4",
        1 => "越大越省",
        2 => "0 = 关",
        _ => string.Empty,
    };

    private string KnobValueText(int index) => KnobValue(index).ToString(CultureInfo.InvariantCulture);

    private static Rectangle KnobLabelRow(UiLayout layout, int index)
    {
        Rectangle row = layout.KnobRows[index];
        return new Rectangle(row.X, row.Y, 72, row.Height);
    }

    private static Rectangle KnobHintRow(UiLayout layout, int index)
    {
        Rectangle row = layout.KnobRows[index];
        return new Rectangle(row.X + 68, row.Y, Math.Max(10, layout.KnobMinus[index].X - row.X - 74), row.Height);
    }

    private static Rectangle ExtraTitleTextRow(UiLayout layout) =>
        new(layout.ExtraTitleRow.X + 14, layout.ExtraTitleRow.Y, layout.ExtraTitleRow.Width - 120, layout.ExtraTitleRow.Height);

    private static Rectangle ExtraHintRow(UiLayout layout) =>
        new(layout.ExtraTitleRow.Right - 140, layout.ExtraTitleRow.Y, 140, layout.ExtraTitleRow.Height);

    private static string ExtraTitleText() => "额外预测区 x0,y0,x1,y1";

    private string ExtraHintText()
    {
        int parsed = CountRectLines(_extraRectsText);
        return parsed > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} 条 · 每行一条", parsed)
            : "每行一条";
    }

    private static Rectangle ActionTitleTextRow(UiLayout layout) =>
        new(layout.ActionTitleRow.X, layout.ActionTitleRow.Y, 90, layout.ActionTitleRow.Height);

    private Rectangle EmptyStateTextRow(UiLayout layout) =>
        new(layout.ResultBody.X, layout.ResultBody.Y + 8, layout.ResultBody.Width, 22);

    private string EmptyStateText() =>
        _planError.Length > 0 ? _planError : "还没有规划。选一个存档，然后点「开始规划」。";

    /// <summary>The placeholder diagram's box, shared with the renderer.</summary>
    internal Rectangle EmptyDiagramBox(UiLayout layout) =>
        new(layout.ResultBody.X, layout.ResultBody.Y + 40, layout.ResultBody.Width, Math.Max(40, layout.ResultBody.Height - 40));

    /// <summary>A layout built from the current state, for the smoke test's geometry.</summary>
    internal UiLayout CurrentLayout() => UiLayout.Build(ClientSize, _extraExpanded, KnobLabels, _entries.Count);

    /// <summary>True while the result panel is showing its placeholder instead of a plan.</summary>
    internal bool IsEmptyState => _presentation is null;

    private static Rectangle ResultHeadingRow(UiLayout layout) => layout.ResultTitleRow;

    private static Rectangle ResultRow(UiLayout layout, int index) =>
        new(layout.ResultBody.X, layout.ResultBody.Y + (index * 20), layout.ResultBody.Width, 20);

    private static Rectangle ProgressTextRow(UiLayout layout) => layout.ProgressLabel;

    private string ProgressText()
    {
        if (_presentation is not null)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "耗时 读档 {0} ms · 规划 {1} ms",
                _readMilliseconds,
                _planMilliseconds);
        }

        return _running ? "读档并规划中…" : "等待开始";
    }

    private static string[] ActionButtonTexts(bool running, bool ready) =>
    [
        running ? "规划中…" : "开始规划",
        "打开地图",
        "导出 JSON",
        "导出施工文件",
        "安装接管插件",
    ];

    /// <summary>
    /// The regions the smoke test requires to carry ink, expressed with the same helpers the
    /// renderer uses. Coordinates are deliberately never duplicated in the test.
    /// </summary>
    internal IReadOnlyList<UiLabel> ExpectedLabels()
    {
        UiLayout layout = UiLayout.Build(ClientSize, _extraExpanded, KnobLabels, _entries.Count);
        List<UiLabel> labels =
        [
            new UiLabel("炸带", HeaderBrandRow()),
            new UiLabel("隔离带爆破规划器", HeaderNameRow()),
            new UiLabel("版本号", HeaderVersionRow()),
            new UiLabel("窗口提示", HeaderHintRow(layout)),
            new UiLabel(WorldDirectoryText(), WorldDirectoryRow(layout)),
            new UiLabel("刷新", layout.RefreshButton),
            new UiLabel("浏览…", layout.BrowseButton),
            new UiLabel("参数标题", layout.KnobTitleRow),
        ];

        labels.Add(new UiLabel(KnobLabelText(0), KnobLabelRow(layout, 0)));
        labels.Add(new UiLabel(KnobLabelText(1), KnobLabelRow(layout, 1)));
        labels.Add(new UiLabel(KnobLabelText(2), KnobLabelRow(layout, 2)));
        labels.Add(new UiLabel(KnobValueText(0), layout.KnobField[0]));
        labels.Add(new UiLabel(KnobValueText(1), layout.KnobField[1]));
        labels.Add(new UiLabel(KnobValueText(2), layout.KnobField[2]));
        labels.Add(new UiLabel(ExtraTitleText(), ExtraTitleTextRow(layout)));
        labels.Add(new UiLabel(ExtraHintText(), ExtraHintRow(layout)));
        labels.Add(new UiLabel("操作标题", ActionTitleTextRow(layout)));
        labels.Add(new UiLabel("规划结果", ResultHeadingRow(layout)));
        labels.Add(new UiLabel(ProgressText(), ProgressTextRow(layout)));

        if (_entries.Count == 0)
        {
            labels.AddRange(WorldEmptyLabels(layout));
        }
        else
        {
            labels.Add(new UiLabel(Path.GetFileNameWithoutExtension(_entries[0].FileName), WorldFileNameRow(layout, 0)));
        }

        if (_presentation is null)
        {
            labels.Add(new UiLabel(EmptyStateText(), EmptyStateTextRow(layout)));
        }
        else
        {
            IReadOnlyList<string> summary = _presentation.SummaryLines();
            labels.Add(new UiLabel(summary[0], ResultRow(layout, 0)));
            labels.Add(new UiLabel(summary[6], ResultRow(layout, 6)));
            labels.Add(new UiLabel(summary[8], ResultRow(layout, 8)));
        }

        string[] actions = ActionButtonTexts(_running, _presentation is not null);
        Rectangle[] bounds = ButtonBounds(layout);
        for (int i = 0; i < bounds.Length; i++)
        {
            labels.Add(new UiLabel(actions[i], bounds[i]));
        }

        return labels;
    }

    // --- painting -----------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.Canvas);
        Render(e.Graphics);
    }

    /// <summary>Paints the window into any target. The smoke test calls this on a bitmap.</summary>
    public void Render(Graphics graphics)
    {
        UpdateLayout();
        UiLayout layout = _layout;
        Point mouse = _mouse;
        if (!Visible)
        {
            // With no window on screen the recorded mouse position is meaningless: drop the hover
            // highlights so an off-screen render is deterministic.
            mouse = new Point(-1, -1);
        }

        SetHighQuality(graphics);

        using (SolidBrush canvas = UiTheme.Brush(UiTheme.Canvas))
        {
            graphics.FillRectangle(canvas, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }

        DrawHeader(graphics, layout, mouse);
        DrawWorldPicker(graphics, layout, mouse);
        DrawKnobs(graphics, layout, mouse);
        DrawResults(graphics, layout);
        DrawActions(graphics, layout, mouse);
        UiTheme.Grip(graphics, layout.Grip, UiTheme.Disabled);
        UiTheme.Border(graphics, layout.Client, UiTheme.BorderColor);
    }

    private static void SetHighQuality(Graphics graphics)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    }

    private void DrawHeader(Graphics graphics, UiLayout layout, Point mouse)
    {
        using (SolidBrush fill = UiTheme.Brush(UiTheme.Surface))
        {
            graphics.FillRectangle(fill, layout.Header);
        }

        UiTheme.Rule(graphics, new Rectangle(0, layout.Header.Bottom - 1, layout.Header.Width, 1), UiTheme.BorderColor);
        UiTheme.TextLine(graphics, "炸带", UiTheme.Title, UiTheme.Accent, HeaderBrandRow(), ContentAlignment.MiddleLeft);
        UiTheme.TextLine(graphics, "隔离带爆破规划器", UiTheme.Body, UiTheme.TextColor, HeaderNameRow(), ContentAlignment.MiddleLeft);
        UiTheme.TextLine(graphics, "v0.1.0-alpha", UiTheme.Small, UiTheme.Muted, HeaderVersionRow(), ContentAlignment.MiddleLeft);
        UiTheme.TextLine(
            graphics,
            "拖动标题栏移动窗口 · 拖动右下角改变大小",
            UiTheme.Small,
            UiTheme.Disabled,
            HeaderHintRow(layout),
            ContentAlignment.MiddleRight);

        bool closeHover = layout.CloseButton.Contains(mouse);
        bool minimiseHover = layout.MinimiseButton.Contains(mouse);
        if (closeHover || minimiseHover)
        {
            using SolidBrush hover = UiTheme.Brush(closeHover ? UiTheme.DangerSoft : UiTheme.Subtle);
            graphics.FillRectangle(hover, closeHover ? layout.CloseButton : layout.MinimiseButton);
        }

        UiTheme.Dash(graphics, layout.MinimiseButton, minimiseHover ? UiTheme.Accent : UiTheme.Muted);
        UiTheme.Cross(graphics, layout.CloseButton, closeHover ? UiTheme.Danger : UiTheme.Muted);
    }

    private void DrawWorldPicker(Graphics graphics, UiLayout layout, Point mouse)
    {
        UiTheme.Frame(graphics, layout.WorldCard, UiTheme.Surface, UiTheme.BorderColor);
        UiTheme.SectionLabel(
            graphics,
            string.Format(CultureInfo.InvariantCulture, "存档 · {0} 个", _entries.Count),
            layout.WorldTitleRow);
        UiTheme.TextClipped(graphics, _worldDirectory, UiTheme.Small, UiTheme.Muted, layout.WorldDirRow);

        DrawButton(graphics, layout.RefreshButton, "刷新", enabled: true, primary: false, mouse);
        DrawButton(graphics, layout.BrowseButton, "浏览…", enabled: true, primary: false, mouse);

        Rectangle list = layout.WorldListBox;
        if (_entries.Count == 0)
        {
            IReadOnlyList<UiLabel> emptyLabels = WorldEmptyLabels(layout);
            UiTheme.TextClipped(graphics, emptyLabels[0].Text, UiTheme.Body, UiTheme.Muted, emptyLabels[0].Bounds);
            return;
        }

        int count = Math.Min(_entries.Count, layout.WorldRows.Count);
        for (int i = 0; i < count; i++)
        {
            WorldEntry entry = _entries[i];
            Rectangle row = layout.WorldRow(i);
            bool selected = i == _selectedWorld;
            bool hover = !selected && row.Contains(mouse);

            if (selected || hover)
            {
                using SolidBrush fill = UiTheme.Brush(selected ? UiTheme.AccentSoft : UiTheme.Subtle);
                graphics.FillRectangle(fill, row);
            }
            else if ((i % 2) == 1)
            {
                using SolidBrush zebra = UiTheme.Brush(UiTheme.Canvas);
                graphics.FillRectangle(zebra, row);
            }

            if (selected)
            {
                using SolidBrush stripe = UiTheme.Brush(UiTheme.Accent);
                graphics.FillRectangle(stripe, new Rectangle(row.X, row.Y, 2, row.Height));
            }

            UiTheme.TextClipped(
                graphics,
                entry.FileName,
                UiTheme.BodyBold,
                selected ? UiTheme.Accent : UiTheme.TextColor,
                WorldFileNameRow(layout, i));
            UiTheme.TextClipped(graphics, WorldTitle(entry), UiTheme.Small, UiTheme.Muted, WorldEntryTitleRow(layout, i));
            UiTheme.TextLine(
                graphics,
                WorldCatalog.FormatSize(entry.Size),
                UiTheme.Small,
                UiTheme.Muted,
                WorldSizeRow(layout, i),
                ContentAlignment.MiddleLeft);
            UiTheme.TextLine(
                graphics,
                WorldCatalog.FormatModified(entry.ModifiedUtc),
                UiTheme.Small,
                UiTheme.Disabled,
                WorldModifiedRow(layout, i),
                ContentAlignment.MiddleRight);
        }

        if (_entries.Count > count)
        {
            UiTheme.TextLine(
                graphics,
                string.Format(CultureInfo.InvariantCulture, "还有 {0} 个存档未显示", _entries.Count - count),
                UiTheme.Small,
                UiTheme.Muted,
                new Rectangle(list.X, list.Bottom - 13, list.Width, 13),
                ContentAlignment.MiddleRight);
        }
    }

    private void DrawKnobs(Graphics graphics, UiLayout layout, Point mouse)
    {
        UiTheme.Frame(graphics, layout.KnobCard, UiTheme.Surface, UiTheme.BorderColor);
        UiTheme.SectionLabel(graphics, "参数（都是可选的）", layout.KnobTitleRow);

        DrawStepper(graphics, layout, 0, mouse);
        DrawStepper(graphics, layout, 1, mouse);
        DrawStepper(graphics, layout, 2, mouse);

        UiTheme.Frame(graphics, layout.ExtraCard, UiTheme.Canvas, UiTheme.BorderColor);
        bool hover = layout.ExtraTitleRow.Contains(mouse);
        UiTheme.Marker(
            graphics,
            new Rectangle(layout.ExtraTitleRow.X, layout.ExtraTitleRow.Y + 4, 10, 10),
            hover ? UiTheme.Accent : UiTheme.Muted,
            _extraExpanded);
        UiTheme.TextLine(
            graphics,
            ExtraTitleText(),
            UiTheme.Body,
            hover ? UiTheme.Accent : UiTheme.TextColor,
            ExtraTitleTextRow(layout),
            ContentAlignment.MiddleLeft);
        UiTheme.TextLine(
            graphics,
            ExtraHintText(),
            UiTheme.Small,
            UiTheme.Muted,
            ExtraHintRow(layout),
            ContentAlignment.MiddleRight);

        if (!_extraExpanded)
        {
            return;
        }

        Rectangle field = layout.ExtraField;
        bool editing = _editingExtra;
        UiTheme.Frame(graphics, field, editing ? UiTheme.AccentSoft : UiTheme.Surface, editing ? UiTheme.Accent : UiTheme.BorderColor);
        bool placeholder = _extraRectsText.Length == 0 && !editing;
        string text = placeholder ? "x0,y0,x1,y1" : _extraRectsText;
        Color color = placeholder ? UiTheme.Disabled : UiTheme.TextColor;
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int y = field.Y + 4;
        foreach (string line in lines)
        {
            if (y + 14 > field.Bottom)
            {
                break;
            }

            UiTheme.TextClipped(graphics, line, UiTheme.Body, color, new Rectangle(field.X + 5, y, field.Width - 10, 14));
            y += 15;
        }

        if (editing && _blinkOn && !placeholder)
        {
            string last = lines[^1];
            int width = UiTheme.MeasureWidth(graphics, last, UiTheme.Body);
            int caretX = Math.Min(field.Right - 5, field.X + 5 + width + 1);
            int caretY = field.Y + 4 + (Math.Max(0, lines.Length - 1) * 15);
            if (caretY + 14 <= field.Bottom)
            {
                UiTheme.PenLine(graphics, UiTheme.TextColor, new Point(caretX, caretY), new Point(caretX, caretY + 13));
            }
        }
    }

    private void DrawStepper(Graphics graphics, UiLayout layout, int index, Point mouse)
    {
        if (index >= layout.KnobRows.Count)
        {
            return;
        }

        UiTheme.TextClipped(graphics, KnobLabelText(index), UiTheme.Body, UiTheme.TextColor, KnobLabelRow(layout, index));
        UiTheme.TextClipped(graphics, KnobHintText(index), UiTheme.Small, UiTheme.Disabled, KnobHintRow(layout, index));

        string value = KnobValueText(index);
        bool fieldEditing = _editingKnob == index;
        DrawStepperPart(graphics, layout.KnobMinus[index], "−", layout.KnobMinus[index].Contains(mouse), active: false);
        DrawStepperPart(graphics, layout.KnobPlus[index], "+", layout.KnobPlus[index].Contains(mouse), active: false);
        DrawStepperPart(graphics, layout.KnobField[index], value, layout.KnobField[index].Contains(mouse), fieldEditing);

        if (fieldEditing && _blinkOn)
        {
            Rectangle field = layout.KnobField[index];
            int width = UiTheme.MeasureWidth(graphics, value, UiTheme.BodyBold);
            int caretX = Math.Min(field.Right - 4, field.X + ((field.Width - width) / 2) + width + 1);
            UiTheme.PenLine(graphics, UiTheme.TextColor, new Point(caretX, field.Y + 4), new Point(caretX, field.Bottom - 5));
        }
    }

    private static void DrawStepperPart(Graphics graphics, Rectangle bounds, string text, bool hover, bool active)
    {
        Color fill = active ? UiTheme.AccentSoft : hover ? UiTheme.Subtle : UiTheme.Surface;
        Color border = active || hover ? UiTheme.Accent : UiTheme.BorderColor;
        Color color = active ? UiTheme.Accent : UiTheme.TextColor;
        UiTheme.Frame(graphics, bounds, fill, border);
        UiTheme.TextLine(graphics, text, UiTheme.BodyBold, color, bounds, ContentAlignment.MiddleCenter);
    }

    private void DrawResults(Graphics graphics, UiLayout layout)
    {
        UiTheme.Frame(graphics, layout.ResultCard, UiTheme.Surface, UiTheme.BorderColor);
        UiTheme.TextLine(graphics, "规划结果", UiTheme.SmallBold, UiTheme.Muted, ResultHeadingRow(layout), ContentAlignment.MiddleLeft);

        if (_presentation is null)
        {
            DrawProgress(graphics, layout);
            UiTheme.TextClipped(
                graphics,
                EmptyStateText(),
                UiTheme.Body,
                _planError.Length > 0 ? UiTheme.Danger : UiTheme.Muted,
                EmptyStateTextRow(layout));
            DrawEmptyDiagram(
                graphics,
                EmptyDiagramBox(layout));
            return;
        }

        PlanPresentation plan = _presentation;
        DrawProgress(graphics, layout);

        IReadOnlyList<string> summary = plan.SummaryLines();
        for (int i = 0; i < summary.Count; i++)
        {
            Rectangle row = ResultRow(layout, i);
            if (i == 6)
            {
                // The flood re-verification verdict is the one line that must never be misread.
                Color color = plan.AllSectionsSealed ? UiTheme.Active : UiTheme.Danger;
                UiTheme.Dot(graphics, new Rectangle(row.X, row.Y + 6, 7, 7), color);
                UiTheme.TextClipped(graphics, summary[i], UiTheme.BodyBold, color, new Rectangle(row.X + 14, row.Y, row.Width - 14, row.Height));
            }
            else
            {
                UiTheme.TextClipped(graphics, summary[i], UiTheme.Body, UiTheme.TextColor, row);
            }
        }

        IReadOnlyList<string> warnings = plan.WarningLines();
        Rectangle warning = layout.ResultWarning;
        if (warnings.Count == 0 || warning.Height < 22)
        {
            return;
        }

        UiTheme.Frame(graphics, warning, UiTheme.DangerSoft, UiTheme.BorderColor);
        using (SolidBrush stripe = UiTheme.Brush(UiTheme.Danger))
        {
            graphics.FillRectangle(stripe, new Rectangle(warning.X, warning.Y, 2, warning.Height));
        }

        int line = warning.Y + 5;
        foreach (string text in warnings)
        {
            if (line + 16 > warning.Bottom)
            {
                break;
            }

            UiTheme.TextClipped(graphics, text, UiTheme.Small, UiTheme.Danger, new Rectangle(warning.X + 10, line, warning.Width - 16, 16));
            line += 16;
        }
    }

    /// <summary>The empty state's placeholder: the diagonal-front idea drawn as plain geometry.</summary>
    private static void DrawEmptyDiagram(Graphics graphics, Rectangle box)
    {
        int size = Math.Min(Math.Min(box.Width - 20, box.Height - 20), 150);
        if (size < 40)
        {
            return;
        }

        Rectangle area = new(box.X + ((box.Width - size) / 2), box.Y + ((box.Height - size) / 2), size, size);
        using (SolidBrush fill = UiTheme.Brush(UiTheme.Canvas))
        {
            graphics.FillRectangle(fill, area);
        }

        UiTheme.Border(graphics, area, UiTheme.BorderColor);

        int step = Math.Max(5, size / 14);
        int offset = Math.Max(8, size / 6);
        using (SolidBrush evil = UiTheme.Brush(UiTheme.Evil))
        using (SolidBrush hallow = UiTheme.Brush(UiTheme.Hallow))
        {
            for (int i = 0; i < 7; i++)
            {
                int x = area.X + offset + (i * step);
                int y = area.Y + offset + (i * step);
                graphics.FillRectangle(evil, new Rectangle(x, y, Math.Max(3, step - 1), Math.Max(3, step - 1)));
                if (i == 4)
                {
                    graphics.FillRectangle(
                        hallow,
                        new Rectangle(x - step, y + step, Math.Max(3, step - 1), Math.Max(3, step - 1)));
                }
            }
        }

        UiTheme.TextLine(
            graphics,
            "感染源 → 封带",
            UiTheme.Small,
            UiTheme.Muted,
            new Rectangle(area.X - 20, area.Bottom + 3, area.Width + 40, 14),
            ContentAlignment.MiddleCenter);
    }

    private void DrawProgress(Graphics graphics, UiLayout layout)
    {
        Rectangle bar = layout.ProgressBar;
        if (bar.Width <= 4)
        {
            return;
        }

        using (SolidBrush track = UiTheme.Brush(UiTheme.Subtle))
        {
            graphics.FillRectangle(track, bar);
        }

        if (_presentation is not null || !_running)
        {
            Color fill = _presentation is null
                ? UiTheme.BorderColor
                : _presentation.AllSectionsSealed ? UiTheme.Active : UiTheme.Danger;
            using SolidBrush brush = UiTheme.Brush(fill);
            graphics.FillRectangle(brush, bar);
        }
        else
        {
            int width = Math.Max(24, bar.Width / 3);
            int travel = Math.Max(1, bar.Width - width);
            int x = (int)((Environment.TickCount64 / 4) % (travel * 2));
            if (x > travel)
            {
                x = (travel * 2) - x;
            }

            using SolidBrush brush = UiTheme.Brush(UiTheme.Accent);
            graphics.FillRectangle(brush, new Rectangle(bar.X + x, bar.Y, width, bar.Height));
        }

        UiTheme.Border(graphics, bar, UiTheme.BorderColor);

        string text;
        if (_presentation is not null)
        {
            text = string.Format(
                CultureInfo.InvariantCulture,
                "耗时 读档 {0} ms · 规划 {1} ms",
                _readMilliseconds,
                _planMilliseconds);
        }
        else if (_running)
        {
            text = "读档并规划中… " + _watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒 · 窗口仍可拖动";
        }
        else
        {
            text = "等待开始";
        }

        UiTheme.TextLine(graphics, text, UiTheme.Small, UiTheme.Muted, layout.ProgressLabel, ContentAlignment.MiddleLeft);
    }

    private void DrawActions(Graphics graphics, UiLayout layout, Point mouse)
    {
        UiTheme.Frame(graphics, layout.ActionCard, UiTheme.Surface, UiTheme.BorderColor);
        UiTheme.SectionLabel(graphics, "操作", layout.ActionTitleRow);

        Rectangle[] bounds = ButtonBounds(layout);
        bool ready = _presentation is not null && !_running;
        DrawButton(graphics, bounds[0], _running ? "规划中…" : "开始规划", !_running, primary: true, mouse);
        DrawButton(graphics, bounds[1], "打开地图", ready, primary: false, mouse);
        DrawButton(graphics, bounds[2], "导出 JSON", ready, primary: false, mouse);
        DrawButton(graphics, bounds[3], "导出施工文件", ready, primary: false, mouse);
        DrawButton(graphics, bounds[4], "安装接管插件", enabled: false, primary: false, mouse);

        if (_showEmbeddedTooltip && bounds[4].Contains(mouse))
        {
            DrawTooltip(graphics, bounds[4], "接管插件尚未随本版本发布");
        }
        string status = _message;
        if (status.Length == 0)
        {
            status = _planError.Length > 0
                ? _planError
                : ready
                    ? "计划已就绪：可以打开地图或导出文件。"
                    : "只读存档，不修改世界、角色或地图文件。";
        }

        Color statusColor = _planError.Length > 0
            ? UiTheme.Danger
            : ready && _lastSealed ? UiTheme.Active : UiTheme.Muted;
        UiTheme.TextClipped(graphics, status, UiTheme.Small, statusColor, layout.ActionStatusRow);
    }

    private static void DrawTooltip(Graphics graphics, Rectangle anchor, string text)
    {
        Size size = UiTheme.Measure(graphics, text, UiTheme.Small);
        Rectangle box = new(anchor.X, anchor.Bottom + 6, size.Width + 16, Math.Max(20, size.Height + 6));
        UiTheme.Frame(graphics, box, UiTheme.TextColor, Color.Empty);
        UiTheme.TextLine(graphics, text, UiTheme.Small, UiTheme.Surface, box, ContentAlignment.MiddleCenter);
    }

    private static void DrawButton(Graphics graphics, Rectangle bounds, string text, bool enabled, bool primary, Point mouse)
    {
        bool hover = enabled && bounds.Contains(mouse);
        Color fill;
        Color border;
        Color color;

        if (!enabled)
        {
            fill = UiTheme.Subtle;
            border = UiTheme.BorderColor;
            color = UiTheme.Disabled;
        }
        else if (primary)
        {
            fill = hover ? UiTheme.Accent : UiTheme.AccentSoft;
            border = UiTheme.Accent;
            color = hover ? UiTheme.Surface : UiTheme.Accent;
        }
        else
        {
            fill = hover ? UiTheme.AccentSoft : UiTheme.Surface;
            border = hover ? UiTheme.Accent : UiTheme.BorderColor;
            color = hover ? UiTheme.Accent : UiTheme.TextColor;
        }

        UiTheme.Frame(graphics, bounds, fill, border);
        UiTheme.TextLine(graphics, text, UiTheme.Body, color, bounds, ContentAlignment.MiddleCenter);
    }

    // --- world list ---------------------------------------------------------------------------

    internal void ReloadWorlds()
    {
        _entries = [.. WorldCatalog.List(_worldDirectory)];
        if (_selectedWorld >= _entries.Count)
        {
            _selectedWorld = -1;
        }

        if (_selectedWorld < 0 && _entries.Count > 0)
        {
            _selectedWorld = 0;
        }

        if (_selectedWorld >= 0)
        {
            _worldPath = _entries[_selectedWorld].Path;
        }

        UpdateLayout();
        Invalidate();
    }

    /// <summary>Points the picker at an explicit directory. Used by the smoke test only.</summary>
    internal void SetWorldDirectory(string directory)
    {
        _worldDirectory = directory;
        ReloadWorlds();
    }

    private void SelectWorld(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        _selectedWorld = index;
        _worldPath = _entries[index].Path;
        _message = "已选择：" + _entries[index].FileName;
        Invalidate();
    }

    // --- knobs --------------------------------------------------------------------------------

    private int KnobValue(int index) => index switch
    {
        0 => _clearance,
        1 => _mergeLinkDistance,
        2 => _vineReach,
        _ => 0,
    };

    private void SetKnobValue(int index, int value)
    {
        switch (index)
        {
            case 0:
                _clearance = Math.Clamp(value, BlastPlanOptions.MinimumClearance, 64);
                break;
            case 1:
                _mergeLinkDistance = Math.Clamp(value, 0, 512);
                break;
            case 2:
                _vineReach = Math.Clamp(value, 0, 64);
                break;
            default:
                break;
        }
    }

    private void BumpKnob(int index, int delta)
    {
        SetKnobValue(index, KnobValue(index) + delta);
        _message = string.Empty;
        Invalidate();
    }

    private void BeginEdit(int knobIndex) =>
        BeginEditText(knobIndex, KnobValue(knobIndex).ToString(CultureInfo.InvariantCulture));

    private void BeginEditExtra()
    {
        _extraExpanded = true;
        UpdateLayout();
        BeginEditText(-1, _extraRectsText);
    }

    private void BeginEditText(int knobIndex, string text)
    {
        _editingKnob = knobIndex;
        _editingExtra = knobIndex < 0;
        _blinkOn = true;
        _blink.Start();
        _editor.Text = text;
        _editor.SelectionStart = _editor.TextLength;
        _editor.Focus();
        Invalidate();
    }

    private void EndEdit(bool commit)
    {
        if (_editingKnob < 0 && !_editingExtra)
        {
            return;
        }

        string text = _editor.Text;
        if (commit)
        {
            if (_editingExtra)
            {
                _extraRectsText = text;
                int parsed = CountRectLines(text);
                _message = parsed > 0
                    ? string.Format(CultureInfo.InvariantCulture, "额外预测区：{0} 条", parsed)
                    : "额外预测区已清空。";
            }
            else if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                SetKnobValue(_editingKnob, value);
            }
        }

        _editingKnob = -1;
        _editingExtra = false;
        _blink.Stop();
        _blinkOn = true;
        Invalidate();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) => Invalidate();

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Tab)
        {
            EndEdit(commit: true);
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            EndEdit(commit: false);
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }

    private static int CountRectLines(string text)
    {
        int count = 0;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Parses the extra seed rectangles exactly like the CLI's repeated <c>--also-rect</c>.</summary>
    private IReadOnlyList<TileRect> ParseExtraRects(out string? error)
    {
        error = null;
        List<TileRect> rects = [];
        foreach (string raw in _extraRectsText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match match = RectPattern().Match(line);
            if (!match.Success)
            {
                error = "额外预测区这一行不是 x0,y0,x1,y1：" + line;
                return [];
            }

            int[] numbers = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    error = "额外预测区这一行不是整数：" + line;
                    return [];
                }
            }

            rects.Add(TileRect.FromPoints(numbers[0], numbers[1], numbers[2], numbers[3]));
        }

        return rects;
    }

    // --- planning -----------------------------------------------------------------------------

    private void BeginPlan()
    {
        if (_running)
        {
            return;
        }

        EndEdit(commit: true);

        if (_worldPath.Length == 0 || !File.Exists(_worldPath))
        {
            _planError = "先选一个 .wld 存档。";
            _presentation = null;
            _plan = null;
            Invalidate();
            return;
        }

        IReadOnlyList<TileRect> extraRects = ParseExtraRects(out string? rectError);
        if (rectError is not null)
        {
            _planError = rectError;
            Invalidate();
            return;
        }

        BlastPlanOptions options = new()
        {
            Clearance = _clearance,
            MergeLinkDistance = _mergeLinkDistance,
            VineReach = _vineReach,
            ExtraSeedRects = extraRects,
        };

        string path = _worldPath;
        TaskScheduler ui = TaskScheduler.FromCurrentSynchronizationContext();
        _running = true;
        _planError = string.Empty;
        _message = "读档中…";
        _presentation = null;
        _plan = null;
        _readMilliseconds = 0;
        _planMilliseconds = 0;
        _watch.Restart();
        _progressTimer.Start();
        Invalidate();

        Task.Run(() =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            LoadedWorld world = WorldFileReader.Read(path);
            long readMs = watch.ElapsedMilliseconds;
            BlastPlan plan = BlastPlanner.Plan(world, options);
            long planMs = watch.ElapsedMilliseconds - readMs;
            return (Plan: plan, ReadMs: readMs, PlanMs: planMs);
        }).ContinueWith(
            task => CompletePlan(task, path),
            CancellationToken.None,
            TaskContinuationOptions.None,
            ui);
    }

    private void CompletePlan(Task<(BlastPlan Plan, long ReadMs, long PlanMs)> task, string path)
    {
        _running = false;
        _progressTimer.Stop();
        _watch.Stop();

        if (task.IsFaulted)
        {
            Exception error = task.Exception?.GetBaseException() ?? new InvalidOperationException("规划失败。");
            _planError = "规划失败：" + error.Message;
            _message = string.Empty;
            _presentation = null;
            _plan = null;
            Invalidate();
            return;
        }

        BlastPlan plan = task.Result.Plan;
        long readMs = task.Result.ReadMs;
        long planMs = task.Result.PlanMs;
        _plan = plan;
        _readMilliseconds = readMs;
        _planMilliseconds = planMs;
        _lastSealed = plan.Summary.AllSectionsSealed;
        _presentation = PlanPresentation.FromPlan(plan) with
        {
            ReadMilliseconds = readMs,
            PlanMilliseconds = planMs,
        };
        _planError = string.Empty;
        _message = string.Format(
            CultureInfo.InvariantCulture,
            "规划完成 · 读档 {0} ms · 规划 {1} ms · {2}",
            readMs,
            planMs,
            Path.GetFileName(path));
        Invalidate();
    }

    // --- actions ------------------------------------------------------------------------------

    private string DefaultOutputName(string extension)
    {
        string title = _plan?.World.Title is { Length: > 0 } value ? value : Path.GetFileNameWithoutExtension(_worldPath);
        string safe = Sanitize(title);
        return (safe.Length == 0 ? "plan" : safe) + extension;
    }

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(name.Length);
        foreach (char c in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = builder.ToString().Trim();
        return result.Length > 60 ? result[..60] : result;
    }

    private void OpenMap()
    {
        if (_plan is null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "zhadai");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DefaultOutputName(".html"));
            PlanHtmlWriter.Write(_plan, path, _worldPath);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _message = "已写出地图并打开：" + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _planError = "打不开地图：" + ex.Message;
        }

        Invalidate();
    }

    private void ExportJson()
    {
        if (_plan is null)
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "方案 JSON|*.json|所有文件|*.*",
            FileName = DefaultOutputName(".json"),
            Title = "导出方案 JSON",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            PlanWriter.WriteJson(_plan, dialog.FileName);
            _message = "已写出 JSON：" + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _planError = "导出 JSON 失败：" + ex.Message;
        }

        Invalidate();
    }

    private void ExportExecutionFile()
    {
        if (_plan is null)
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "施工文件|*.zplan|所有文件|*.*",
            FileName = DefaultOutputName(".zplan"),
            Title = "导出施工文件",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            PlanWriter.WriteExecutionFile(_plan, dialog.FileName, _worldPath);
            _message = "已写出施工文件：" + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _planError = "导出施工文件失败：" + ex.Message;
        }

        Invalidate();
    }

    /// <summary>
    /// The single hook for the in-game takeover plugin. The plugin ships in a later build, so this
    /// deliberately reports that state instead of pretending to install anything.
    /// </summary>
    private void InstallRuntimePlugin()
    {
        _message = "接管插件尚未随本版本发布。方案可以先用「导出施工文件」导出 .zplan，等插件发布后再安装接管。";
        Invalidate();
    }

    // --- input --------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        UiLayout layout = _layout;

        if (_editingKnob >= 0 || _editingExtra)
        {
            EndEdit(commit: true);
        }

        foreach (UiZone zone in layout.Zones)
        {
            if (!zone.Contains(e.Location))
            {
                continue;
            }

            switch (zone.Action)
            {
                case UiAction.Close:
                    Close();
                    return;
                case UiAction.Minimise:
                    WindowState = FormWindowState.Minimized;
                    return;
                case UiAction.Refresh:
                    ReloadWorlds();
                    _message = "已刷新存档列表。";
                    Invalidate();
                    return;
                case UiAction.Browse:
                    BrowseForWorld();
                    return;
                case UiAction.SelectWorld:
                    SelectWorld(zone.Index);
                    return;
                case UiAction.ToggleExtra:
                    _extraExpanded = !_extraExpanded;
                    UpdateLayout();
                    Invalidate();
                    return;
                case UiAction.KnobMinus:
                    BumpKnob(zone.Index, -1);
                    return;
                case UiAction.KnobPlus:
                    BumpKnob(zone.Index, 1);
                    return;
                case UiAction.KnobField:
                    BeginEdit(zone.Index);
                    return;
                case UiAction.BeginPlan:
                    BeginPlan();
                    return;
                case UiAction.OpenMap:
                    OpenMap();
                    return;
                case UiAction.ExportJson:
                    ExportJson();
                    return;
                case UiAction.ExportExecution:
                    ExportExecutionFile();
                    return;
                case UiAction.InstallPlugin:
                    InstallRuntimePlugin();
                    return;
                default:
                    break;
            }
        }

        if (_extraExpanded && layout.ExtraField.Contains(e.Location))
        {
            BeginEditExtra();
        }
    }

    private void BrowseForWorld()
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "泰拉瑞亚存档|*.wld|所有文件|*.*",
            Title = "选择世界存档",
            InitialDirectory = Directory.Exists(_worldDirectory) ? _worldDirectory : string.Empty,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _worldDirectory = Path.GetDirectoryName(dialog.FileName) ?? _worldDirectory;
        ReloadWorlds();
        int index = _entries.FindIndex(entry => string.Equals(entry.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            SelectWorld(index);
        }
        else
        {
            _worldPath = dialog.FileName;
            _selectedWorld = -1;
            _message = "已选择：" + Path.GetFileName(dialog.FileName);
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouse != e.Location)
        {
            _mouse = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_layout.Header.Contains(e.Location) &&
            !_layout.CloseButton.Contains(e.Location) &&
            !_layout.MinimiseButton.Contains(e.Location))
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_editingKnob >= 0 || _editingExtra)
        {
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        if (e.Control && e.KeyCode == Keys.R)
        {
            ReloadWorlds();
            return;
        }

        if (e.Control && e.KeyCode == Keys.Enter)
        {
            BeginPlan();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Handle != IntPtr.Zero && Visible && Width > 0 && Height > 0)
        {
            // A borderless window keeps square corners unless it is handed a region.
            Region? previous = Region;
            Region = UiTheme.WindowRegion(Width, Height);
            previous?.Dispose();
        }

        UpdateLayout();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmNcHitTest || (int)m.Result != HtClient)
        {
            return;
        }

        // Keep the frameless window resizable without a border: report the edges as non-client.
        long packed = m.LParam.ToInt64();
        Point screen = new(unchecked((short)packed), unchecked((short)(packed >> 16)));
        Point client = PointToClient(screen);
        bool left = client.X <= ResizeBorder;
        bool right = client.X >= ClientSize.Width - ResizeBorder;
        bool top = client.Y <= ResizeBorder;
        bool bottom = client.Y >= ClientSize.Height - ResizeBorder;

        m.Result = (IntPtr)((left, right, top, bottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => HtClient,
        });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateLayout();
        _message = _entries.Count > 0
            ? "已找到 " + _entries.Count.ToString(CultureInfo.InvariantCulture) + " 个存档。"
            : "没有找到存档，请点「浏览…」手动选择。";
        Invalidate();
    }

    // --- hooks used only by the UI smoke test -------------------------------------------------

    /// <summary>Installs a display-only state so a panel can be rendered without running a plan.</summary>
    internal void ApplySmokeState(PlanPresentation? presentation, BlastPlan? plan, string worldPath, bool running, string message)
    {
        _presentation = presentation;
        _plan = plan;
        _worldPath = worldPath;
        _running = running;
        _message = message;
        _readMilliseconds = presentation?.ReadMilliseconds ?? 0;
        _planMilliseconds = presentation?.PlanMilliseconds ?? 0;
        _lastSealed = presentation?.AllSectionsSealed ?? false;
        _planError = string.Empty;
        if (running)
        {
            _watch.Restart();
        }

        Invalidate();
    }

    /// <summary>Fills the extra seed rectangle list and expands it. Used by the smoke test.</summary>
    internal void SetExtraRectsText(string text)
    {
        _extraRectsText = text;
        _extraExpanded = true;
        UpdateLayout();
        Invalidate();
    }

    /// <summary>Forces the synthesised hover state used to capture the disabled button's tooltip.</summary>
    internal void ShowDisabledPluginTooltip()
    {
        Rectangle[] bounds = ButtonBounds(UiLayout.Build(ClientSize, _extraExpanded, KnobLabels, _entries.Count));
        _showEmbeddedTooltip = true;
        _mouse = new Point(bounds[4].X + (bounds[4].Width / 2), bounds[4].Y + (bounds[4].Height / 2));
        UpdateLayout();
        Invalidate();
    }

    [GeneratedRegex(@"^([+-]?\d+)\s*,\s*([+-]?\d+)\s*,\s*([+-]?\d+)\s*,\s*([+-]?\d+)$")]
    private static partial Regex RectPattern();
}
