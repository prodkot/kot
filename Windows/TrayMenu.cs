using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Kot.Core;

namespace Kot.Windows;

public sealed class TrayMenu : ContextMenuStrip
{
    readonly PrivateFontCollection fonts = new();
    readonly ToolStripMenuItem heading, status, toggle;
    readonly Font regular, bold;
    readonly Action<string> navigate;
    readonly Func<Task> connection, exit;
    bool busy;
    Color background, foreground, muted, line, hover, accent;
    public TrayMenu(Action open, Action<string> navigate, Func<Task> connection, Func<Task> exit)
    {
        this.navigate = navigate; this.connection = connection; this.exit = exit;
        string path = Path.Combine(AppContext.BaseDirectory, "ui", "fonts", "Manrope.ttf");
        if (File.Exists(path)) fonts.AddFontFile(path);
        var family = fonts.Families.FirstOrDefault() ?? SystemFonts.MenuFont!.FontFamily;
        regular = new Font(family, 10); bold = new Font(family, 10, FontStyle.Bold); Font = regular;
        ShowImageMargin = false; ShowCheckMargin = false; Padding = new Padding(7);
        Renderer = new MenuRenderer(this); AccessibleName = "Меню kot.";
        heading = Label("kot. " + ClientIdentity.Version, "heading"); heading.Font = bold;
        status = Label("Не подключено", "status");
        Add("Открыть приложение", "open", open);
        toggle = Add("Подключить", "power", () => _ = Run(this.connection));
        Items.Add(new ToolStripSeparator());
        Add("Серверы", "servers", () => this.navigate("servers"));
        Add("Логи и соединения", "logs", () => this.navigate("logs"));
        Add("Настройки", "settings", () => this.navigate("settings"));
        Items.Add(new ToolStripSeparator());
        Add("Выйти из приложения", "exit", () => _ = Run(this.exit));
        Update("dark", "lime", "idle", "", false);
    }
    ToolStripMenuItem Label(string text, string tag)
    {
        var item = new ToolStripMenuItem(text) { Enabled = false, Tag = tag, AutoSize = false };
        Items.Add(item); return item;
    }
    ToolStripMenuItem Add(string text, string tag, Action action)
    {
        var item = new ToolStripMenuItem(text) { Tag = tag, AutoSize = false };
        item.Click += (_, _) => action(); Items.Add(item); return item;
    }
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; toggle.Enabled = false;
        try { await action(); }
        catch (Exception ex) { AppLog.Error("tray action", ex); MessageBox.Show(Store.Friendly(ex), "kot.", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; }
    }
    public void Update(string theme, string color, string state, string server, bool hasNodes)
    {
        bool light = theme == "light";
        background = ColorTranslator.FromHtml(light ? "#f5f6ef" : "#171b1c");
        foreground = ColorTranslator.FromHtml(light ? "#202822" : "#f1f3ed");
        muted = ColorTranslator.FromHtml(light ? "#69716a" : "#989f9b");
        line = ColorTranslator.FromHtml(light ? "#dce0d5" : "#333b37");
        hover = ColorTranslator.FromHtml(light ? "#e7ebdf" : "#272e2a");
        accent = ColorTranslator.FromHtml(color switch { "gray" => light ? "#53605b" : "#b2beb7", "green" => light ? "#357052" : "#8cdaa7", "purple" => light ? "#7661a8" : "#c8b6f4", _ => light ? "#61772b" : "#d2f58a" });
        BackColor = background; ForeColor = foreground;
        string label = state switch { "connected" => "Подключено", "connecting" => "Подключение", "disconnecting" => "Отключение", "waiting" => "Ожидание сети", _ => "Не подключено" };
        string safe = new(server.Where(c => !char.IsControl(c)).Take(100).ToArray());
        status.Text = label + (safe.Length > 0 ? " · " + safe.Replace("&", "&&") : "");
        status.AccessibleName = status.Text;
        toggle.Text = state switch { "connected" => "Отключить", "connecting" or "waiting" => "Отменить подключение", "disconnecting" => "Отключение…", _ => "Подключить" };
        toggle.Enabled = !busy && state != "disconnecting" && (hasNodes || state != "idle");
        float scale = DeviceDpi / 96f;
        foreach (ToolStripItem item in Items)
            if (item is not ToolStripSeparator) item.Size = new Size((int)(286 * scale), (int)((item == heading ? 29 : item == status ? 38 : 38) * scale));
        Invalidate();
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        using var shape = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10 * DeviceDpi / 96f);
        Region?.Dispose(); Region = new Region(shape);
    }
    internal static GraphicsPath Rounded(Rectangle bounds, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); p.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); p.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { regular.Dispose(); bold.Dispose(); fonts.Dispose(); }
    }
    sealed class MenuRenderer(TrayMenu menu) : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(menu.background);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(new Rectangle(0, 0, menu.Width - 1, menu.Height - 1), 10 * menu.DeviceDpi / 96f);
            using var pen = new Pen(menu.line); e.Graphics.DrawPath(pen, shape);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(new Rectangle(0, 1, e.Item.Width - 1, e.Item.Height - 2), 6 * menu.DeviceDpi / 96f);
            using var brush = new SolidBrush(menu.hover); e.Graphics.FillPath(brush, shape);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            float scale = menu.DeviceDpi / 96f; string tag = e.Item.Tag as string ?? "";
            bool header = tag is "heading" or "status";
            Rectangle bounds = new((int)((header ? 11 : 39) * scale), 0, e.Item.Width - (int)((header ? 22 : 50) * scale), e.Item.Height);
            Color ink = tag == "status" || !header && !e.Item.Enabled ? menu.muted : tag == "heading" ? menu.accent : menu.foreground;
            TextRenderer.DrawText(e.Graphics, e.Item.Text, e.Item.Font, bounds, ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (header) return;
            var g = e.Graphics; var saved = g.Save(); g.TranslateTransform(11 * scale, e.Item.Height / 2f - 8 * scale); g.ScaleTransform(scale, scale); g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item.Enabled ? tag == "power" ? menu.accent : menu.muted : menu.muted, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            switch (tag)
            {
                case "power": g.DrawArc(pen, 1, 1, 14, 14, -50, 280); g.DrawLine(pen, 8, 0, 8, 7); break;
                case "open": g.DrawRectangle(pen, 1, 2, 14, 12); g.DrawLine(pen, 1, 5, 15, 5); break;
                case "servers": g.DrawEllipse(pen, 1, 1, 14, 14); g.DrawEllipse(pen, 5, 1, 6, 14); g.DrawLine(pen, 1, 8, 15, 8); break;
                case "logs": for (int y = 3; y < 16; y += 5) { g.DrawLine(pen, 1, y, 2, y); g.DrawLine(pen, 6, y, 15, y); } break;
                case "settings": for (int y = 3; y < 16; y += 5) { g.DrawLine(pen, 1, y, 15, y); int x = y == 8 ? 4 : 10; g.DrawEllipse(pen, x - 2, y - 2, 4, 4); } break;
                case "exit": g.DrawLines(pen, [new Point(6, 1), new Point(1, 1), new Point(1, 15), new Point(6, 15)]); g.DrawLine(pen, 6, 8, 15, 8); g.DrawLines(pen, [new Point(11, 4), new Point(15, 8), new Point(11, 12)]); break;
            }
            g.Restore(saved);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(menu.line); int inset = (int)(11 * menu.DeviceDpi / 96f); e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    }
}
