namespace Kot.Windows;

// Keep both variants alive while Windows owns their native icon handles.
public sealed class ThemeIcons : IDisposable
{
    readonly Icon dark = new(Path.Combine(AppContext.BaseDirectory, "kot.ico"));
    readonly Icon light = new(Path.Combine(AppContext.BaseDirectory, "kot-light.ico"));
    readonly Icon darkTray, lightTray;
    string? appliedTheme;

    public ThemeIcons()
    {
        darkTray = new Icon(dark, SystemInformation.SmallIconSize);
        lightTray = new Icon(light, SystemInformation.SmallIconSize);
    }

    public Icon Window(string theme) => theme == "light" ? light : dark;

    public void Apply(Form window, NotifyIcon tray, string theme)
    {
        Icon windowIcon = Window(theme), trayIcon = theme == "light" ? lightTray : darkTray;
        // Form.Icon updates both WM_SETICON handles used by the running taskbar button.
        if (!ReferenceEquals(window.Icon, windowIcon)) window.Icon = windowIcon;
        if (!ReferenceEquals(tray.Icon, trayIcon)) tray.Icon = trayIcon;
        string normalized = theme == "light" ? "light" : "dark";
        if (appliedTheme != normalized)
        {
            appliedTheme = normalized;
            // A pinned taskbar group can use its shortcut's icon instead of WM_SETICON.
            try { ShellIdentity.RefreshPinnedIcon(Path.Combine(AppContext.BaseDirectory, normalized == "light" ? "kot-light.ico" : "kot.ico")); }
            catch (Exception ex) { AppLog.Error("taskbar icon refresh", ex); }
        }
    }

    public void Dispose()
    {
        darkTray.Dispose(); lightTray.Dispose(); dark.Dispose(); light.Dispose();
    }
}
