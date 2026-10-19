using System.Drawing.Drawing2D;

namespace Kaptur;

/// Kaptur simgesi: mavi yuvarlatılmış kare, yakalama köşeleri, ortada oynat üçgeni, altında transcript satırları
/// (branding/kaptur.png'deki Codex taslağının vektör hâli). Kodla çizildiği için her boyutta keskin; 24 pikselin
/// altında satırlar okunmayacağı için köşeler ve üçgenden oluşan sade sürüm çizilir.
static class AppIcon
{
    static readonly Color Brand = Color.FromArgb(11, 92, 255);
    static readonly Color Busy = Color.FromArgb(255, 138, 0);   // tepside iş sürerken

    public static Icon Create(bool busy = false)
    {
        using var bmp = Render(32, busy);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// 32 birimlik tasarımı istenen boyuta ölçekleyerek çizer (pencere, tepsi, .ico ve eklenti simgeleri).
    public static Bitmap Render(int size, bool busy = false)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);

        using (var bg = new SolidBrush(busy ? Busy : Brand))
        using (var shape = RoundedRect(new RectangleF(0.5f, 0.5f, 31f, 31f), 7.5f))
            g.FillPath(bg, shape);

        var detailed = size >= 24;
        using var pen = new Pen(Color.White, detailed ? 2.2f : 2.8f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };

        if (detailed)
        {
            // Yakalama köşeleri; alttakiler transcript satırlarına yer açmak için kısa.
            g.DrawLines(pen, [new PointF(5.2f, 10.6f), new PointF(5.2f, 5.2f), new PointF(10.6f, 5.2f)]);
            g.DrawLines(pen, [new PointF(21.4f, 5.2f), new PointF(26.8f, 5.2f), new PointF(26.8f, 10.6f)]);
            g.DrawLines(pen, [new PointF(5.2f, 18.6f), new PointF(5.2f, 22.8f), new PointF(9.4f, 22.8f)]);
            g.DrawLines(pen, [new PointF(26.8f, 18.6f), new PointF(26.8f, 22.8f), new PointF(22.6f, 22.8f)]);
            Play(g, new PointF(12.6f, 9.0f), new PointF(12.6f, 19.2f), new PointF(21.2f, 14.1f));
            using var line = new Pen(Color.White, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(line, 12.2f, 24.8f, 20.6f, 24.8f);
            g.DrawLine(line, 12.2f, 27.6f, 17.4f, 27.6f);
        }
        else
        {
            g.DrawLines(pen, [new PointF(5.5f, 11f), new PointF(5.5f, 5.5f), new PointF(11f, 5.5f)]);
            g.DrawLines(pen, [new PointF(21f, 5.5f), new PointF(26.5f, 5.5f), new PointF(26.5f, 11f)]);
            g.DrawLines(pen, [new PointF(5.5f, 21f), new PointF(5.5f, 26.5f), new PointF(11f, 26.5f)]);
            g.DrawLines(pen, [new PointF(26.5f, 21f), new PointF(26.5f, 26.5f), new PointF(21f, 26.5f)]);
            Play(g, new PointF(12.4f, 10.2f), new PointF(12.4f, 21.8f), new PointF(22.2f, 16f));
        }
        return bmp;
    }

    /// Köşeleri yuvarlatılmış dolu üçgen (aynı renkte kalın kenarla doldurulur).
    static void Play(Graphics g, PointF a, PointF b, PointF c)
    {
        using var path = new GraphicsPath();
        path.AddPolygon([a, b, c]);
        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(Color.White, 1.6f) { LineJoin = LineJoin.Round };
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
