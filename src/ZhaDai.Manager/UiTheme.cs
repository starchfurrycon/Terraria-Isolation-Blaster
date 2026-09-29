using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ZhaDai.Manager;

/// <summary>
/// The one palette, the one font set, and the one set of drawing primitives. Every pixel the
/// window paints goes through this type, so the visual language cannot drift between panels.
/// </summary>
internal static class UiTheme
{
    // Palette, copied from the project's research report on the TingYu skeleton so that the
    // desktop front end speaks the same visual language as the self-contained HTML map.
    public static readonly Color Canvas = Rgb(0xFA, 0xFA, 0xFB);
    public static readonly Color Surface = Rgb(0xFF, 0xFF, 0xFF);
    public static readonly Color Subtle = Rgb(0xF4, 0xF5, 0xF7);
    public static readonly Color BorderColor = Rgb(0xD6, 0xDA, 0xE0);
    public static readonly Color Accent = Rgb(0x40, 0x78, 0xC8);
    public static readonly Color TextColor = Rgb(0x20, 0x24, 0x2C);
    public static readonly Color Muted = Rgb(0x7A, 0x82, 0x8E);
    public static readonly Color Disabled = Rgb(0xB2, 0xB8, 0xC2);
    public static readonly Color AccentSoft = Rgb(0xE8, 0xF0, 0xFC);
    public static readonly Color Active = Rgb(0x26, 0x96, 0x60);
    public static readonly Color Danger = Rgb(0xC6, 0x40, 0x40);
    public static readonly Color DangerSoft = Rgb(0xFB, 0xEC, 0xEC);
    public static readonly Color Evil = Rgb(0x8F, 0x5A, 0xC8);
    public static readonly Color Hallow = Rgb(0xD4, 0x74, 0xB8);

    // Geometry. One pixel borders only, no gradients, no shadows.
    public const int BorderWidth = 1;
    public const int Margin = 12;
    public const int Padding = 12;
    public const int Gap = 8;
    public const int HeaderHeight = 44;
    public const int RowHeight = 22;
    public const int CardRadius = 3;
    public const int WindowRadius = 6;

    private static readonly FontFamily Family = PickFamily();

    public static readonly Font Body = Create(9f, FontStyle.Regular);
    public static readonly Font BodyBold = Create(9f, FontStyle.Bold);
    public static readonly Font Small = Create(8f, FontStyle.Regular);
    public static readonly Font SmallBold = Create(8f, FontStyle.Bold);
    public static readonly Font Title = Create(11f, FontStyle.Bold);

    /// <summary>An anti-aliased pen at exactly one pixel, aligned so the line lands on the pixel.</summary>
    public static Pen Pen(Color color) => new(color, BorderWidth);

    public static SolidBrush Brush(Color color) => new(color);

    public static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);

    /// <summary>Rounds and insets a rectangle by half a pixel so a 1px stroke stays crisp.</summary>
    public static Rectangle Inset(Rectangle bounds) =>
        new(bounds.X, bounds.Y, Math.Max(0, bounds.Width - BorderWidth), Math.Max(0, bounds.Height - BorderWidth));

    /// <summary>The single card primitive: a filled rounded rectangle with a 1px border.</summary>
    public static void Frame(Graphics graphics, Rectangle bounds, Color fill, Color border)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        using (GraphicsPath path = RoundedPath(bounds, CardRadius))
        using (SolidBrush brush = Brush(fill))
        {
            graphics.FillPath(brush, path);
        }

        if (border.A == 0)
        {
            return;
        }

        using GraphicsPath stroke = RoundedPath(Inset(bounds), CardRadius);
        using Pen pen = Pen(border);
        graphics.DrawPath(pen, stroke);
    }

    public static void Border(Graphics graphics, Rectangle bounds, Color border)
    {
        using GraphicsPath path = RoundedPath(Inset(bounds), CardRadius);
        using Pen pen = Pen(border);
        graphics.DrawPath(pen, path);
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Max(2, radius * 2);
        if (bounds.Width <= diameter + 1 || bounds.Height <= diameter + 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        Rectangle arc = new(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);

        arc.X = bounds.Right - diameter - 1;
        path.AddArc(arc, 270, 90);

        arc.Y = bounds.Bottom - diameter - 1;
        path.AddArc(arc, 0, 90);

        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    /// <summary>Rounds the window itself, so a borderless window still reads as a light card.</summary>
    public static Region WindowRegion(int width, int height)
    {
        using GraphicsPath path = RoundedPath(new Rectangle(0, 0, width, height), WindowRadius);
        return new Region(path);
    }

    /// <summary>
    /// One line of text through the GDI+ path with an explicit <see cref="StringFormat"/>.
    /// <c>TextRenderer</c> (the GDI path) and the format-less overload of
    /// <c>Graphics.DrawString</c> both misbehave when the same code paints into an off-screen
    /// bitmap, which is exactly what the UI smoke test does.
    /// </summary>
    public static void Text(Graphics graphics, string text, Font font, Color color, Point origin)
    {
        if (text.Length == 0)
        {
            return;
        }

        using SolidBrush brush = Brush(color);
        graphics.DrawString(text, font, brush, origin.X, origin.Y, StringFormat.GenericTypographic);
    }

    /// <summary>Draws a run of text on one line, vertically centred in the given row.</summary>
    public static void TextLine(Graphics graphics, string text, Font font, Color color, Rectangle row, ContentAlignment align)
    {
        Text(graphics, text, font, color, TextOrigin(graphics, text, font, row, align));
    }

    /// <summary>
    /// Draws wrapped text clipped to <paramref name="row"/>, ending the line with an ellipsis when
    /// it does not fit. This is the only paragraph-ish primitive the window is allowed to use, and
    /// every caller feeds it a single short line.
    /// </summary>
    public static void TextClipped(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        Rectangle row,
        ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        Size measured = Measure(graphics, text, font);
        if (measured.Width <= row.Width)
        {
            TextLine(graphics, text, font, color, row, align);
            return;
        }

        using StringFormat format = new(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using SolidBrush brush = Brush(color);
        graphics.DrawString(text, font, brush, row, format);
    }

    public static Size Measure(Graphics graphics, string text, Font font)
    {
        if (text.Length == 0)
        {
            return Size.Empty;
        }

        SizeF size = graphics.MeasureString(text, font, new PointF(0f, 0f), StringFormat.GenericTypographic);
        return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    public static int MeasureWidth(Graphics graphics, string text, Font font) => Measure(graphics, text, font).Width;

    /// <summary>Top-left origin that places <paramref name="text"/> on one line inside a row.</summary>
    public static Point TextOrigin(Graphics graphics, string text, Font font, Rectangle row, ContentAlignment align)
    {
        if (text.Length == 0)
        {
            return new Point(row.X, row.Y);
        }

        SizeF size = graphics.MeasureString(text, font, new PointF(0f, 0f), StringFormat.GenericTypographic);
        int x = align switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter =>
                row.X + (int)Math.Round((row.Width - size.Width) / 2f),
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight =>
                row.Right - (int)Math.Ceiling(size.Width),
            _ => row.X,
        };

        int y = align switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => row.Y,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight =>
                row.Bottom - (int)Math.Ceiling(size.Height),
            _ => row.Y + (int)Math.Round((row.Height - size.Height) / 2f),
        };

        return new Point(x, y);
    }

    /// <summary>A 8pt section heading in the muted colour.</summary>
    public static void SectionLabel(Graphics graphics, string text, Rectangle row)
    {
        TextLine(graphics, text, SmallBold, Muted, row, ContentAlignment.MiddleLeft);
    }

    /// <summary>A 1px horizontal rule across the given row.</summary>
    public static void Rule(Graphics graphics, Rectangle row, Color color)
    {
        using Pen pen = Pen(color);
        int y = row.Y + (row.Height / 2);
        graphics.DrawLine(pen, row.X, y, row.Right - 1, y);
    }

    public static void PenLine(Graphics graphics, Color color, Point from, Point to)
    {
        using Pen pen = Pen(color);
        graphics.DrawLine(pen, from, to);
    }

    /// <summary>The close affordance: two crossed strokes.</summary>
    public static void Cross(Graphics graphics, Rectangle box, Color color)
    {
        int inset = Math.Max(3, box.Width / 4);
        PenLine(graphics, color, new Point(box.X + inset, box.Y + inset), new Point(box.Right - inset - 1, box.Bottom - inset - 1));
        PenLine(graphics, color, new Point(box.Right - inset - 1, box.Y + inset), new Point(box.X + inset, box.Bottom - inset - 1));
    }

    /// <summary>The minimise affordance: one horizontal stroke.</summary>
    public static void Dash(Graphics graphics, Rectangle box, Color color)
    {
        int inset = Math.Max(3, box.Width / 4);
        int y = box.Y + (box.Height / 2);
        PenLine(graphics, color, new Point(box.X + inset, y), new Point(box.Right - inset - 1, y));
    }

    /// <summary>The resize grip: three diagonal strokes in the corner.</summary>
    public static void Grip(Graphics graphics, Rectangle box, Color color)
    {
        using Pen pen = Pen(color);
        int size = Math.Min(box.Width, box.Height);
        for (int step = 3; step <= size - 2; step += 4)
        {
            graphics.DrawLine(
                pen,
                new Point(box.Right - step - 1, box.Bottom - 2),
                new Point(box.Right - 2, box.Bottom - step - 1));
        }
    }

    /// <summary>A small downward or rightward triangle used by the collapsed sections.</summary>
    public static void Marker(Graphics graphics, Rectangle box, Color color, bool expanded)
    {
        using SolidBrush brush = Brush(color);
        int half = 3;
        int cx = box.X + (box.Width / 2);
        int cy = box.Y + (box.Height / 2);
        Point[] points = expanded
            ?
            [
                new Point(cx - half - 1, cy - 2),
                new Point(cx + half + 1, cy - 2),
                new Point(cx, cy + half - 1),
            ]
            :
            [
                new Point(cx - 2, cy - half - 1),
                new Point(cx + half - 1, cy),
                new Point(cx - 2, cy + half + 1),
            ];
        graphics.FillPolygon(brush, points);
    }

    /// <summary>A 6px status dot.</summary>
    public static void Dot(Graphics graphics, Rectangle box, Color color)
    {
        using SolidBrush brush = Brush(color);
        graphics.FillEllipse(brush, box);
    }

    /// <summary>
    /// Prefers the UI variant of the Chinese system family and degrades to whatever exists. The
    /// family instance is kept for the process lifetime: disposing a family also disposes the fonts
    /// built from it.
    /// </summary>
    private static FontFamily PickFamily()
    {
        foreach (string name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Segoe UI" })
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // Try the next candidate.
            }
        }

        return new FontFamily(FontFamily.GenericSansSerif.Name);
    }

    /// <summary>Creates one font at a point size, the unit the design was measured in.</summary>
    private static Font Create(float size, FontStyle style) => new(Family, size, style, GraphicsUnit.Point);
}
