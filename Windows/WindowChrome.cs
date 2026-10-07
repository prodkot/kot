namespace Kot.Windows;

// A borderless window still needs the normal shell commands and taskbar styles.
public class WindowChrome : Form
{
    FormWindowState restoredState = FormWindowState.Normal;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00080000 | 0x00020000 | 0x00010000; // SYSMENU, MINIMIZEBOX, MAXIMIZEBOX
            return cp;
        }
    }
    protected override void OnResize(EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) restoredState = WindowState;
        base.OnResize(e);
    }
    public void MinimizeWindow() => WindowState = FormWindowState.Minimized;
    public void RestoreWindow()
    {
        if (IsDisposed) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = restoredState;
        Activate();
    }
    public void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x112)
        {
            long command = m.WParam.ToInt64() & 0xfff0;
            if (command == 0xf020) { MinimizeWindow(); m.Result = IntPtr.Zero; return; }
            if (command == 0xf120 && WindowState == FormWindowState.Minimized) { RestoreWindow(); m.Result = IntPtr.Zero; return; }
        }
        base.WndProc(ref m);
    }
}
