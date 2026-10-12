using System.Drawing.Text;
using Kot.Core;

namespace Kot.Windows;

// Visible before WebView2 exists; the first rendered profile snapshot dismisses it.
public sealed class StartupScreen : UserControl
{
    readonly PrivateFontCollection fonts = new();
    readonly Font regular, small;
    readonly Bitmap image;

    public StartupScreen(string theme, Icon icon, Action minimize, Action maximize, Action close, Action drag)
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
        var content = new Panel { Dock = DockStyle.Fill, BackColor = background };
        using var large = new Icon(icon, new Size(256, 256));
        image = large.ToBitmap();
        var mark = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, TabStop = false, AccessibleName = "kot." };
        var label = new Label { Text = "Загрузка…", TextAlign = ContentAlignment.MiddleCenter, AutoSize = false, AccessibleRole = AccessibleRole.StaticText };
        content.Controls.Add(mark); content.Controls.Add(label);
        content.Resize += (_, _) =>
        {
            int Px(int value) => (int)Math.Round(value * DeviceDpi / 96f);
            int middle = content.ClientSize.Height / 2;
            mark.SetBounds((content.ClientSize.Width - Px(72)) / 2, middle - Px(56), Px(72), Px(72));
            label.SetBounds(0, middle + Px(36), content.ClientSize.Width, Px(24));
        };
        var title = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = side, Padding = new Padding(0, 0, 0, 1) };
        title.Paint += (_, e) => { using var pen = new Pen(line); e.Graphics.DrawLine(pen, 0, title.Height - 1, title.Width, title.Height - 1); };
        var version = new Label { Text = "kot.  " + ClientIdentity.Version, Font = small, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0) };
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
}
