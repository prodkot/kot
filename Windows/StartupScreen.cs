using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kot.Core;

namespace Kot.Windows;

// Visible before WebView2 exists; the first rendered profile snapshot dismisses it.
public sealed class StartupScreen : UserControl
{
    readonly PrivateFontCollection fonts = new();
    readonly Font regular, small;
    Control? failure;
    readonly StartupArtwork artwork;
    internal double AnimationOriginMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - artwork.ElapsedMilliseconds;
    readonly Control titleBar;

    public void ShowFailure(string message, Action retry)
    {
        ClearFailure(); artwork.Hide();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor, Padding = new Padding(48), ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.Controls.Add(new Label { Text = message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, Padding = new Padding(0, 0, 0, 20), AccessibleName = message }, 0, 0);
        var button = new Button { Text = "Повторить", Name = "retryInterface", AutoSize = true, MinimumSize = new Size(160, 38), Anchor = AnchorStyles.None, FlatStyle = FlatStyle.Flat, ForeColor = ForeColor };
        button.Click += (_, _) => retry(); panel.Controls.Add(button, 0, 1);
        failure = panel; Controls.Add(panel); panel.BringToFront(); titleBar.BringToFront(); Show(); button.Focus();
    }
    public void ClearFailure()
    {
        if (failure != null) { Controls.Remove(failure); failure.Dispose(); failure = null; }
        artwork?.Show();
    }

    public StartupScreen(string theme, Icon icon, Action minimize, Action maximize, Action close, Action drag, string[]? sequence = null)
    {
        bool light = theme == "light";
        Color background = ColorTranslator.FromHtml(light ? "#f6f6f2" : "#151718");
        Color side = ColorTranslator.FromHtml(light ? "#efefeb" : "#191c1d");
        Color muted = ColorTranslator.FromHtml(light ? "#757d73" : "#969e9f");
        Color line = ColorTranslator.FromHtml(light ? "#dadcd5" : "#303536");
        Color raised = ColorTranslator.FromHtml(light ? "#e6e8e0" : "#282d2e");
        string path = Path.Combine(AppContext.BaseDirectory, "ui", "fonts", "Manrope.ttf");
        if (File.Exists(path)) fonts.AddFontFile(path);
        var family = fonts.Families.FirstOrDefault() ?? SystemFonts.MessageBoxFont!.FontFamily;
        regular = new Font(family, 10); small = new Font(family, 8.25f);
        Name = "startupScreen"; AccessibleName = "Загрузка kot."; Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = background; ForeColor = muted; Font = regular;
        DoubleBuffered = true;
        var content = new StartupArtwork(sequence ?? LoadingMotion.Sequence(), light) { Dock = DockStyle.Fill, BackColor = background, ForeColor = muted, Font = regular };
        artwork = content;
        var title = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = side, Padding = new Padding(0, 0, 0, 1) };
        titleBar = title;
        title.Paint += (_, e) => { using var pen = new Pen(line); e.Graphics.DrawLine(pen, 0, title.Height - 1, title.Width, title.Height - 1); };
        var version = new Label { Text = "", Font = small, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), AccessibleName = "kot. " + ClientIdentity.DisplayVersion };
        version.Paint += (_, e) =>
        {
            // A separate square follows the word's visual centre, not the font baseline.
            float dpi = DeviceDpi / 96f;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Size word = TextRenderer.MeasureText(e.Graphics, "kot", small, Size.Empty, flags);
            int x = (int)Math.Round(16 * dpi), y = (version.Height - word.Height) / 2;
            TextRenderer.DrawText(e.Graphics, "kot", small, new Point(x, y), muted, flags);
            int dot = Math.Max(2, (int)Math.Round(2 * dpi));
            using var brush = new SolidBrush(muted);
            e.Graphics.FillRectangle(brush, x + word.Width + (int)Math.Round(2 * dpi), (version.Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(e.Graphics, ClientIdentity.DisplayVersion, small,
                new Point(x + word.Width + dot + (int)Math.Round(12 * dpi), y), muted, flags);
        };
        title.Controls.Add(version);
        void Drag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) drag(); }
        title.MouseDown += Drag; version.MouseDown += Drag;
        foreach (var (name, action) in new[] { ("close", close), ("maximize", maximize), ("minimize", minimize) })
        {
            var button = new Button { Name = name, Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat,
                BackColor = side, ForeColor = muted, AccessibleName = name == "close" ? "Закрыть" : name == "maximize" ? "Развернуть" : "Свернуть" };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = name == "close" ? Color.FromArgb(189, 62, 62) : raised;
            button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
            button.Click += (_, _) => action();
            button.Paint += (_, e) =>
            {
                float scale = DeviceDpi / 96f; var g = e.Graphics; var saved = g.Save();
                g.TranslateTransform(button.Width / 2f - 6 * scale, button.Height / 2f - 6 * scale); g.ScaleTransform(scale, scale);
                using var pen = new Pen(muted, 1.4f);
                if (name == "close") { g.DrawLine(pen, 2, 2, 10, 10); g.DrawLine(pen, 10, 2, 2, 10); }
                else if (name == "maximize") g.DrawRectangle(pen, 2, 2, 8, 8);
                else g.DrawLine(pen, 2, 6, 10, 6);
                g.Restore(saved);
            };
            title.Controls.Add(button); button.BringToFront();
        }
        // Dock the fill label last so it cannot cover the window controls.
        version.BringToFront();
        Controls.Add(content); Controls.Add(title);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { regular.Dispose(); small.Dispose(); fonts.Dispose(); }
    }

    // Paint only the small loading artwork; window controls remain live and unanimated.
    sealed class StartupArtwork : Control
    {
        readonly string[] sequence;
        readonly Color ink, detail;
        Form? owner;
        readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
        readonly Stopwatch clock = new();
        public double ElapsedMilliseconds => clock.Elapsed.TotalMilliseconds;
        bool reduced;

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfoW(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);

        public StartupArtwork(string[] sequence, bool light)
        {
            this.sequence = sequence;
            ink = ColorTranslator.FromHtml(light ? "#252a23" : "#f2f4ed");
            detail = ColorTranslator.FromHtml(light ? "#55752a" : "#d4f58a");
            DoubleBuffered = true; ResizeRedraw = true; TabStop = false;
            AccessibleName = "kot. Загрузка…"; AccessibleRole = AccessibleRole.StaticText;
            VisibleChanged += (_, _) => UpdateAnimation();
            timer.Tick += (_, _) =>
            {
                if (FindForm()?.WindowState == FormWindowState.Minimized) { UpdateAnimation(); return; }
                // One region contains every pose of the moving mark and the animated caption.
                var dpi = DeviceDpi / 96f;
                int radius = (int)Math.Ceiling(76 * dpi);
                Invalidate(new Rectangle(ClientSize.Width / 2 - radius, ClientSize.Height / 2 - radius,
                    2 * radius + 2, 2 * radius + 2));
            };
        }

        void OwnerChanged(object? sender, EventArgs e) => UpdateAnimation();

        void UpdateAnimation()
        {
            timer.Stop(); clock.Stop();
            if (!Visible || !IsHandleCreated || FindForm()?.WindowState == FormWindowState.Minimized) return;
            reduced = SystemParametersInfoW(0x1042, 0, out bool enabled, 0) && !enabled;
            if (!reduced) { clock.Start(); timer.Start(); }
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            owner = FindForm();
            if (owner != null) { owner.Resize += OwnerChanged; owner.VisibleChanged += OwnerChanged; }
            UpdateAnimation();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            timer.Stop(); clock.Stop();
            if (owner != null) { owner.Resize -= OwnerChanged; owner.VisibleChanged -= OwnerChanged; owner = null; }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x001A) UpdateAnimation(); // WM_SETTINGCHANGE
        }

        // Match startup-mark-in's cubic-bezier(.22,1,.36,1), including early handoffs.
        static float EntranceEase(float elapsed)
        {
            if (elapsed <= 0) return 0;
            if (elapsed >= 1) return 1;
            float t = elapsed;
            for (int i = 0; i < 4; i++)
            {
                float x = t * (.66f + t * (-.24f + .58f * t));
                float slope = .66f + t * (-.48f + 1.74f * t);
                t -= (x - elapsed) / slope;
            }
            return 1 - MathF.Pow(1 - t, 3);
        }

        void DrawMark(Graphics target, float x, float y, float size, float opacity, double[] pose)
        {
            var state = target.Save();
            target.SmoothingMode = SmoothingMode.AntiAlias;
            target.TranslateTransform(x - size * 1560 / 1800 / 2, y - size / 2);
            target.ScaleTransform(size / 1800, size / 1800);
            using var letterBrush = new SolidBrush(Color.FromArgb((int)(255 * opacity), ink));
            using var dotBrush = new SolidBrush(Color.FromArgb((int)(255 * opacity), detail));
            var letter = target.Save(); target.TranslateTransform((float)pose[3] * 272, 1600); target.ScaleTransform(1, -1);
            PointF[] contour = [new(138,0),new(140,1440),new(416,1440),new(416,560),new(782,1080),new(1118,1080),new(728,540),new(1144,0),new(790,0),new(416,520),new(416,0)];
            target.FillPolygon(letterBrush, contour); target.Restore(letter);
            target.TranslateTransform(1277.17647f + (float)pose[0]*272, 1464 + (float)pose[1]*272);
            target.RotateTransform((float)pose[2]);
            target.FillRectangle(dotBrush, -136, -136, 272, 272);
            target.Restore(state);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float dpi = DeviceDpi / 96f, seconds = (float)clock.Elapsed.TotalSeconds;
            float entered = reduced ? 1 : EntranceEase(seconds / .42f);
            float size = 72 * dpi * (.88f + .12f * entered);
            float centerY = ClientSize.Height / 2f - 20 * dpi + 6 * dpi * (1 - entered);
            var g = e.Graphics;
            DrawMark(g, ClientSize.Width / 2f, centerY, size, entered,
                reduced ? [0,0,0,0] : LoadingMotion.Sample(sequence, clock.Elapsed.TotalMilliseconds));
            float labelProgress = reduced ? 1 : Math.Clamp((seconds - .08f) / .36f, 0, 1);
            int Mix(int background, int foreground) => (int)Math.Round(background + (foreground - background) * labelProgress);
            Color textColor = Color.FromArgb(Mix(BackColor.R, ForeColor.R), Mix(BackColor.G, ForeColor.G), Mix(BackColor.B, ForeColor.B));
            var textBounds = new Rectangle(0, (int)(ClientSize.Height / 2f + 36 * dpi), ClientSize.Width, (int)(24 * dpi));
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (labelProgress >= 1)
            {
                TextRenderer.DrawText(g, "Загрузка…", Font, textBounds, textColor, flags);
            }
            else if (labelProgress > 0 && textBounds.Width > 0 && textBounds.Height > 0)
            {
                // GDI takes integer baselines. Translate the whole raster line during its short entrance.
                using var line = new Bitmap(textBounds.Width, textBounds.Height);
                line.SetResolution(g.DpiX, g.DpiY);
                using (var raster = Graphics.FromImage(line))
                {
                    raster.Clear(BackColor);
                    TextRenderer.DrawText(raster, "Загрузка…", Font, new Rectangle(Point.Empty, line.Size), textColor, flags);
                }
                float top = textBounds.Y + 4 * dpi * (1 - labelProgress);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(line, new RectangleF(0, top, line.Width, line.Height),
                    new RectangleF(0, 0, line.Width, line.Height), GraphicsUnit.Pixel);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); clock.Stop(); }
            base.Dispose(disposing);
        }
    }
}
