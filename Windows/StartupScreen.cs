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
    readonly Bitmap image;

    public StartupScreen(string theme, Icon icon, Action minimize, Action maximize, Action close, Action drag, bool spin = false)
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
        if (light)
        {
            using var source = new Bitmap(Path.Combine(AppContext.BaseDirectory, "ui", "brand-icon-light.png"));
            image = new Bitmap(source);
        }
        else
        {
            using var large = new Icon(icon, new Size(256, 256));
            image = large.ToBitmap();
        }
        var content = new StartupArtwork(image, spin) { Dock = DockStyle.Fill, BackColor = background, ForeColor = muted, Font = regular };
        var title = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = side, Padding = new Padding(0, 0, 0, 1) };
        title.Paint += (_, e) => { using var pen = new Pen(line); e.Graphics.DrawLine(pen, 0, title.Height - 1, title.Width, title.Height - 1); };
        var version = new Label { Text = "", Font = small, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), AccessibleName = "kot. " + ClientIdentity.Version };
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
            TextRenderer.DrawText(e.Graphics, ClientIdentity.Version, small,
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
        if (disposing) { image.Dispose(); regular.Dispose(); small.Dispose(); fonts.Dispose(); }
    }

    // Paint only the small loading artwork; window controls remain live and unanimated.
    sealed class StartupArtwork : Control
    {
        readonly Bitmap image;
        readonly bool spin;
        readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
        readonly Stopwatch clock = new();
        bool reduced;

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfoW(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);

        public StartupArtwork(Bitmap image, bool spin)
        {
            this.image = image; this.spin = spin;
            DoubleBuffered = true; ResizeRedraw = true; TabStop = false;
            AccessibleName = "kot. Загрузка…"; AccessibleRole = AccessibleRole.StaticText;
            VisibleChanged += (_, _) => UpdateAnimation();
            timer.Tick += (_, _) =>
            {
                if (FindForm()?.WindowState != FormWindowState.Minimized) Invalidate();
            };
        }

        void UpdateAnimation()
        {
            timer.Stop(); clock.Stop();
            if (!Visible || !IsHandleCreated) return;
            reduced = SystemParametersInfoW(0x1042, 0, out bool enabled, 0) && !enabled;
            if (!reduced) { clock.Start(); timer.Start(); }
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); UpdateAnimation();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            timer.Stop(); clock.Stop(); base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x001A) UpdateAnimation(); // WM_SETTINGCHANGE
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float dpi = DeviceDpi / 96f, seconds = (float)clock.Elapsed.TotalSeconds;
            float progress = reduced ? 1 : Math.Clamp(seconds / .42f, 0, 1);
            float entered = 1 - MathF.Pow(1 - progress, 3);
            float breath = reduced ? 0 : .009f * (1 - MathF.Cos(Math.Max(0, seconds - .42f) * MathF.PI / 1.1f));
            float size = 72 * dpi * (.88f + .12f * entered + breath);
            float centerY = ClientSize.Height / 2f - 20 * dpi + 6 * dpi * (1 - entered);
            var g = e.Graphics; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = entered });
            var saved = g.Save();
            g.TranslateTransform(ClientSize.Width / 2f, centerY);
            if (spin && !reduced) g.RotateTransform(Math.Max(0, seconds - .42f) * 225 % 360);
            var bounds = Rectangle.Round(new RectangleF(-size / 2, -size / 2, size, size));
            g.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            g.Restore(saved);
            float labelProgress = reduced ? 1 : Math.Clamp((seconds - .08f) / .36f, 0, 1);
            int Mix(int background, int foreground) => (int)Math.Round(background + (foreground - background) * labelProgress);
            Color textColor = Color.FromArgb(Mix(BackColor.R, ForeColor.R), Mix(BackColor.G, ForeColor.G), Mix(BackColor.B, ForeColor.B));
            var textBounds = new Rectangle(0, (int)(ClientSize.Height / 2f + (36 + 4 * (1 - labelProgress)) * dpi), ClientSize.Width, (int)(24 * dpi));
            TextRenderer.DrawText(g, "Загрузка…", Font, textBounds, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); clock.Stop(); }
            base.Dispose(disposing);
        }
    }
}
