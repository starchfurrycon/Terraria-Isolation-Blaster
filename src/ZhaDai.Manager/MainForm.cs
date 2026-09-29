using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
    private const int WmSetRedraw = 0x000B;

    private static readonly string[] KnobLabels = ["封带厚度", "合并距离", "藤蔓下探"];

    private static readonly Regex RectPattern = RectRegex();

    private readonly Stopwatch _watch = new();
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };

    /// <summary>
    /// One invisible helper: the keyboard and IME sink for the numeric fields and the extra seed
    /// rectangles box. It paints nothing; editing state is drawn by <see cref="Render"/>.
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
    private bool _installing;
    private bool _running;
    private bool _lastSealed;

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
        ForeColor = UiTheme.Text;
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

    // --- painting -----------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        e.Graphics.Clear(UiTheme.Canvas);
        Render(e.Graphics);
    }

    /// <summary>Paints the window into any target. The smoke test calls this on a bitmap.</summary>
    public void Render(Graphics graphics)
    {
        UpdateLayout();
        UiLayout layout = _layout;
        Point mouse = _mouse;
        bool onScreen = graphics.ClipBounds.X > -1000f;
        if (!onScreen && mouse.X >= 0)
        {
            // Off screen the mouse position is meaningless: drop the hover highlight.
            mouse = new Point(-1, -1);
        }

        SetHighQuality(graphics);

        using (Region window = UiTheme.WindowRegion(ClientSize.Width, ClientSize.Height))
        {
            using SolidBrush canvas = UiTheme.Brush(UiTheme.Canvas);
            graphics.FillRectangle(canvas, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

            DrawHeader(graphics, layout, mouse);

            using (Region content = new(layout.ResultCard))
            {
                using Region card = new(layout.WorldCard);
                content.Union(card);
                DrawWorldPicker(graphics, layout, mouse);
            }

            DrawKnobs(graphics, layout, mouse);
            DrawResults(graphics, layout);
            DrawActions(graphics, layout, mouse);
            DrawGrip(graphics, layout);
            UiTheme.Border(graphics, layout.Client, UiTheme.Border);
        }
    }

    private static void SetHighQuality(Graphics graphics)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    }

    private void DrawHeader(Graphics graphics, UiLayout layout, Point mouse)
    {
        using (SolidBrush fill = UiTheme.Brush(UiTheme.Surface))
        {
            graphics.FillRectangle(fill, layout.Header);
        }

        UiTheme.PenLine(graphics, UiTheme.Border, new Point(0, layout.Header.Bottom - 1), new Point(layout.Header.Right, layout.Header.Bottom - 1));
        UiTheme.TextLine(graphics, "炸带", UiTheme.Title, UiTheme.Accent, new Rectangle(14, 10, 56, 22), ContentAlignment.MiddleLeft);
        UiTheme.TextLine(graphics, "隔离带爆破规划器", UiTheme.Body, UiTheme.Text, new Rectangle(56, 10, 180, 22), ContentAlignment.MiddleLeft);
        UiTheme.TextLine(graphics, "v0.1.0-alpha", UiTheme.Small, UiTheme.Muted, new Rectangle(150, 12, 120, 18), ContentAlignment.MiddleLeft);

        int hintRight = layout.MinimiseButton.X - 12;
        UiTheme.TextLine(
            graphics,
            "拖动标题栏移动窗口 · 拖动右下角改变大小",
            UiTheme.Small,
            UiTheme.Disabled,
            new Rectangle(hintRight - 250, 12, 250, 18),
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
        UiTheme.Frame(graphics, layout.WorldCard, UiTheme.Surface, UiTheme.Border);
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
            UiTheme.TextClipped(
                graphics,
                Directory.Exists(_worldDirectory) ? "这个目录里没有 .wld 存档。" : "找不到存档目录：" + _worldDirectory,
                UiTheme.Body,
                UiTheme.Muted,
                new Rectangle(list.X + 2, list.Y + 12, list.Width - 4, 22));
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

            string name = entry.Title is { Length: > 0 } ? entry.Title : Path.GetFileNameWithoutExtension(entry.FileName);
            Color nameColor = selected ? UiTheme.Accent : UiTheme.Text;
            Size nameSize = UiTheme.Measure(graphics, name, UiTheme.BodyBold);
            int nameWidth = Math.Min(nameSize.Width, row.Width - 150);
            UiTheme.TextClipped(
                graphics,
                name,
                UiTheme.BodyBold,
                nameColor,
                new Rectangle(row.X + 8, row.Y + 3, Math.Max(40, nameWidth), 14));
            UiTheme.TextClipped(
                graphics,
                entry.FileName,
                UiTheme.Small,
                UiTheme.Muted,
                new Rectangle(row.X + 10 + Math.Max(40, nameWidth), row.Y + 4, Math.Max(20, row.Width - nameWidth - 150), 13));
            UiTheme.TextLine(
                graphics,
                WorldCatalog.FormatSize(entry.Size),
                UiTheme.Small,
                UiTheme.Muted,
                new Rectangle(row.X + 8, row.Y + 17, 90, 13),
                ContentAlignment.MiddleLeft);
            UiTheme.TextLine(
                graphics,
                WorldCatalog.FormatModified(entry.ModifiedUtc),
                UiTheme.Small,
                UiTheme.Disabled,
                new Rectangle(row.Right - 116, row.Y + 17, 108, 13),
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
        UiTheme.Frame(graphics, layout.KnobCard, UiTheme.Surface, UiTheme.Border);
        UiTheme.SectionLabel(graphics, "参数（留空用默认值）", layout.KnobTitleRow);

        DrawStepper(graphics, layout, 0, "封带厚度", _clearance.ToString(CultureInfo.InvariantCulture), "格 · 最小 4", mouse);
        DrawStepper(graphics, layout, 1, "合并距离", _mergeLinkDistance.ToString(CultureInfo.InvariantCulture), "格 · 越大越省炸药", mouse);
        DrawStepper(graphics, layout, 2, "藤蔓下探", _vineReach.ToString(CultureInfo.InvariantCulture), "格 · 0 = 不考虑藤蔓", mouse);

        UiTheme.Frame(graphics, layout.ExtraCard, UiTheme.Canvas, UiTheme.Border);
        bool hover = layout.ExtraTitleRow.Contains(mouse);
        UiTheme.Marker(graphics, new Rectangle(layout.ExtraTitleRow.X, layout.ExtraTitleRow.Y + 4, 10, 10), hover ? UiTheme.Accent : UiTheme.Muted, _extraExpanded);
        UiTheme.TextLine(
            graphics,
            "额外预测区 x0,y0,x1,y1",
            UiTheme.Body,
            hover ? UiTheme.Accent : UiTheme.Text,
            new Rectangle(layout.ExtraTitleRow.X + 14, layout.ExtraTitleRow.Y, layout.ExtraTitleRow.Width - 120, layout.ExtraTitleRow.Height),
            ContentAlignment.MiddleLeft);
        int parsedCount = CountRectLines(_extraRectsText);
        UiTheme.TextLine(
            graphics,
            parsedCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} 条 · 每行一条", parsedCount)
                : "每行一条 · 可折叠",
            UiTheme.Small,
            UiTheme.Muted,
            new Rectangle(layout.ExtraTitleRow.Right - 140, layout.ExtraTitleRow.Y, 140, layout.ExtraTitleRow.Height),
            ContentAlignment.MiddleRight);

        if (!_extraExpanded)
        {
            return;
        }

        Rectangle field = layout.ExtraField;
        bool editing = _editingExtra;
        UiTheme.Frame(graphics, field, editing ? UiTheme.AccentSoft : UiTheme.Surface, editing ? UiTheme.Accent : UiTheme.Border);
        string text = _extraRectsText.Length == 0 && !editing ? "x0,y0,x1,y1" : _extraRectsText;
        Color color = _extraRectsText.Length == 0 && !editing ? UiTheme.Disabled : UiTheme.Text;
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

        if (editing && _blinkOn)
        {
            string last = lines.Length == 0 ? string.Empty : lines[^1];
            int width = UiTheme.MeasureWidth(graphics, last, UiTheme.Body);
            int caretX = Math.Min(field.Right - 5, field.X + 5 + width + 1);
            int caretY = field.Y + 4 + (Math.Max(0, lines.Length - 1) * 15);
            if (caretY + 14 <= field.Bottom)
            {
                UiTheme.PenLine(graphics, UiTheme.Text, new Point(caretX, caretY), new Point(caretX, caretY + 13));
            }
        }
    }

    private void DrawStepper(Graphics graphics, UiLayout layout, int index, string label, string value, string hint, Point mouse)
    {
        if (index >= layout.KnobRows.Count)
        {
            return;
        }

        Rectangle row = layout.KnobRows[index];
        UiTheme.TextClipped(graphics, label, UiTheme.Body, UiTheme.Text, new Rectangle(row.X, row.Y, 84, row.Height));
        UiTheme.TextClipped(
            graphics,
            hint,
            UiTheme.Small,
            UiTheme.Disabled,
            new Rectangle(row.X + 78, row.Y, Math.Max(10, layout.KnobMinus[index].X - row.X - 84), row.Height));

        bool fieldEditing = _editingKnob == index;
        DrawStepperPart(graphics, layout.KnobMinus[index], "−", layout.KnobMinus[index].Contains(mouse), false);
        DrawStepperPart(graphics, layout.KnobPlus[index], "+", layout.KnobPlus[index].Contains(mouse), false);
        DrawStepperPart(graphics, layout.KnobField[index], value, layout.KnobField[index].Contains(mouse), fieldEditing);

        if (fieldEditing && _blinkOn)
        {
            Rectangle field = layout.KnobField[index];
            int width = UiTheme.MeasureWidth(graphics, value, UiTheme.BodyBold);
            int caretX = field.X + ((field.Width - width) / 2) + width + 1;
            if (caretX > field.Right - 4)
            {
                caretX = field.Right - 4;
            }

            UiTheme.PenLine(
                graphics,
                UiTheme.Text,
                new Point(caretX, field.Y + 4),
                new Point(caretX, field.Bottom - 5));
        }
    }

    private static void DrawStepperPart(Graphics graphics, Rectangle bounds, string text, bool hover, bool active)
    {
        Color fill = active ? UiTheme.AccentSoft : hover ? UiTheme.Subtle : UiTheme.Surface;
        Color border = active ? UiTheme.Accent : hover ? UiTheme.Accent : UiTheme.Border;
        UiTheme.Frame(graphics, bounds, fill, border);
        Font font = text is "−" or "+" ? UiTheme.BodyBold : UiTheme.BodyBold;
        UiTheme.TextLine(graphics, text, font, active ? UiTheme.Accent : UiTheme.Text, bounds, ContentAlignment.MiddleCenter);
    }

    private void DrawResults(Graphics graphics, UiLayout layout)
    {
        UiTheme.Frame(graphics, layout.ResultCard, UiTheme.Surface, UiTheme.Border);
        bool hasPlan = _presentation is not null;
        UiTheme.TextLine(graphics, "规划结果", UiTheme.SmallBold, UiTheme.Muted, layout.ResultTitleRow, ContentAlignment.MiddleLeft);

        if (!hasPlan)
        {
            UiTheme.TextClipped(
                graphics,
                _planError.Length > 0 ? _planError : "还没有规划。选一个存档，然后点「开始规划」。",
                UiTheme.Body,
                _planError.Length > 0 ? UiTheme.Danger : UiTheme.Muted,
                new Rectangle(layout.ResultBody.X, layout.ResultBody.Y + 8, layout.ResultBody.Width, 22));
            DrawEmptyDiagram(graphics, new Rectangle(layout.ResultBody.X, layout.ResultBody.Y + 40, layout.ResultBody.Width, Math.Max(40, layout.ResultBody.Height - 40)));
            return;
        }

        PlanPresentation plan = _presentation!;
        DrawProgress(graphics, layout, complete: true);

        int y = layout.ResultBody.Y;
        IReadOnlyList<string> summary = plan.SummaryLines();
        for (int i = 0; i < summary.Count; i++)
        {
            Rectangle row = new(layout.ResultBody.X, y, layout.ResultBody.Width, 20);
            bool sealRow = i == 6;
            Color color = UiTheme.Text;
            if (sealRow)
            {
                color = plan.AllSectionsSealed ? UiTheme.Active : UiTheme.Danger;
                Rectangle dot = new(row.X, row.Y + 6, 7, 7);
                UiTheme.Dot(graphics, dot, color);
                UiTheme.TextClipped(graphics, summary[i], UiTheme.BodyBold, color, new Rectangle(row.X + 14, row.Y, row.Width - 14, row.Height));
            }
            else
            {
                UiTheme.TextClipped(graphics, summary[i], UiTheme.Body, color, row);
            }

            y += 20;
        }

        IReadOnlyList<string> warnings = plan.WarningLines();
        Rectangle warning = layout.ResultWarning;
        if (warnings.Count == 0 || warning.Height < 22)
        {
            return;
        }

        UiTheme.Frame(graphics, warning, UiTheme.DangerSoft, UiTheme.Border);
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

    /// <summary>The empty state's placeholder: the band-hugging idea drawn as geometry, no chrome.</summary>
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

        UiTheme.Border(graphics, area, UiTheme.Border);

        int step = Math.Max(5, size / 14);
        int offset = Math.Max(10, size / 5);
        for (int i = 0; i < 8; i++)
        {
            int x = area.X + offset + (i * step);
            int y = area.Y + offset + (i * step);
            using SolidBrush evil = UiTheme.Brush(UiTheme.Evil);
            graphics.FillRectangle(evil, new Rectangle(x, y, Math.Max(3, step - 1), Math.Max(3, step - 1)));
        }

        UiTheme.TextLine(
            graphics,
            "感染源",
            UiTheme.Small,
            UiTheme.Muted,
            new Rectangle(area.X, area.Bottom + 4, area.Width, 14),
            ContentAlignment.MiddleCenter);
    }

    private void DrawProgress(Graphics graphics, UiLayout layout, bool complete)
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

        if (complete || !_running)
        {
            using SolidBrush fill = UiTheme.Brush(_presentation is null ? UiTheme.Border : _presentation.AllSectionsSealed ? UiTheme.Active : UiTheme.Danger);
            graphics.FillRectangle(fill, bar);
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

            using SolidBrush fill = UiTheme.Brush(UiTheme.Accent);
            graphics.FillRectangle(fill, new Rectangle(bar.X + x, bar.Y, width, bar.Height));
        }

        UiTheme.Border(graphics, bar, UiTheme.Border);

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
            text = string.Format(
                CultureInfo.InvariantCulture,
                "读档并规划中… {0} 秒",
                _watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        }
        else
        {
            text = "等待开始";
        }

        UiTheme.TextLine(graphics, text, UiTheme.Small, UiTheme.Muted, layout.ProgressLabel, ContentAlignment.MiddleLeft);
    }

    private void DrawActions(Graphics graphics, UiLayout layout, Point mouse)
    {
        UiTheme.Frame(graphics, layout.ActionCard, UiTheme.Surface, UiTheme.Border);
        UiTheme.SectionLabel(graphics, "操作", layout.ActionTitleRow);
        if (_running)
        {
            UiTheme.TextLine(
                graphics,
                "规划中 · 窗口仍可拖动",
                UiTheme.Small,
                UiTheme.Accent,
                new Rectangle(layout.ActionTitleRow.Right - 160, layout.ActionTitleRow.Y, 160, layout.ActionTitleRow.Height),
                ContentAlignment.MiddleRight);
        }

        Rectangle[] bounds = ButtonBounds(layout);
        bool ready = _presentation is not null && !_running;
        DrawButton(graphics, bounds[0], _running ? "规划中…" : "开始规划", !_running, primary: true, mouse);
        DrawButton(graphics, bounds[1], "打开地图", ready, primary: false, mouse);
        DrawButton(graphics, bounds[2], "导出 JSON", ready, primary: false, mouse);
        DrawButton(graphics, bounds[3], "导出施工文件", ready, primary: false, mouse);
        DrawButton(graphics, bounds[4], "安装接管插件", _installing, primary: false, mouse);

        if (bounds[4].Contains(mouse) && !_installing)
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
            : _lastSealed && ready ? UiTheme.Active : UiTheme.Muted;
        UiTheme.TextClipped(graphics, status, UiTheme.Small, statusColor, layout.ActionStatusRow);
    }

    private static void DrawTooltip(Graphics graphics, Rectangle anchor, string text)
    {
        Size size = UiTheme.Measure(graphics, text, UiTheme.Small);
        int width = size.Width + 16;
        int height = Math.Max(20, size.Height + 6);
        Rectangle box = new(anchor.X, anchor.Bottom + 6, width, height);
        UiTheme.Frame(graphics, box, UiTheme.Text, Color.Empty);
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
            border = UiTheme.Border;
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
            border = hover ? UiTheme.Accent : UiTheme.Border;
            color = hover ? UiTheme.Accent : UiTheme.Text;
        }

        UiTheme.Frame(graphics, bounds, fill, border);
        UiTheme.TextLine(graphics, text, UiTheme.Body, color, bounds, ContentAlignment.MiddleCenter);
    }

    private static void DrawGrip(Graphics graphics, UiLayout layout)
    {
        UiTheme.Grip(graphics, layout.Grip, UiTheme.Disabled);
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
                _clearance = Math.Max(BlastPlanOptions.MinimumClearance, Math.Min(64, value));
                break;
            case 1:
                _mergeLinkDistance = Math.Max(0, Math.Min(512, value));
                break;
            case 2:
                _vineReach = Math.Max(0, Math.Min(64, value));
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

    private void BeginEdit(int knobIndex)
    {
        BeginEditText(knobIndex, KnobValue(knobIndex).ToString(CultureInfo.InvariantCulture));
    }

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

            Match match = RectPattern.Match(line);
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
            TaskScheduler.FromCurrentSynchronizationContext());
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

        (BlastPlan plan, long readMs, long planMs) = task.Result;
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
            "规划完成 · 读档 {0} ms · 规划 {1} ms",
            readMs,
            planMs);
        Invalidate();
    }

    // --- actions ------------------------------------------------------------------------------

    private string DefaultOutputName(string extension)
    {
        string title = _plan?.World.Title is { Length: > 0 } value ? value : Path.GetFileNameWithoutExtension(_worldPath);
        string safe = Sanitize(title);
        if (safe.Length == 0)
        {
            safe = "plan";
        }

        return safe + extension;
    }

    private static string Sanitize(string name)
    {
        StringBuilder builder = new(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
        _installing = false;
        _message = "接管插件尚未随本版本发布。计划文件（.zplan）已经可以导出，等插件发布后再安装。";
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

        if (layout.ExtraField.Contains(e.Location) && _extraExpanded)
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
        if (_layout.Header.Contains(e.Location) && !_layout.CloseButton.Contains(e.Location) && !_layout.MinimiseButton.Contains(e.Location))
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
        if (Handle != IntPtr.Zero && Width > 0 && Height > 0)
        {
            // A borderless window keeps square corners unless it is given a region.
            Region? previous = Region;
            Region = UiTheme.WindowRegion(Width, Height);
            previous?.Dispose();
        }

        UpdateLayout();
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmSetRedraw)
        {
            UpdateLayout();
        }

        if (m.Msg != WmNcHitTest || (int)m.Result != HtClient)
        {
            return;
        }

        // Keep the frameless window resizable without a border: report the edges as non-client.
        Point screen = new(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
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

    /// <summary>Drives the smoke test's non-empty states without touching the window handle.</summary>
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

    /// <summary>Shows the extra seed rectangle editor without a click. Used by the smoke test.</summary>
    internal void SetExtraRectsText(string text)
    {
        _extraRectsText = text;
        _extraExpanded = true;
        UpdateLayout();
        Invalidate();
    }

    [GeneratedRegex(@"^([+-]?\d+)\s*,\s*([+-]?\d+)\s*,\s*([+-]?\d+)\s*,\s*([+-]?\d+)$")]
    private static partial Regex RectRegex();
}
