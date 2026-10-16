using Kot.Core;
namespace Kot.Windows;

// This window is independent of both the profile and WebView2, so damaged user
// data cannot lock the user out of backup import or network recovery.
internal sealed class ProfileRecovery : Form
{
    readonly ProfilePersistence store;
    readonly Label error = new() { AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Color.FromArgb(240, 165, 155) };
    readonly TextBox password = new() { UseSystemPasswordChar = true, Width = 480, AccessibleName = "Пароль резервной копии", BackColor = Color.FromArgb(40, 45, 46), ForeColor = Color.FromArgb(232, 236, 230), BorderStyle = BorderStyle.FixedSingle };
    Profile? result;
    internal ProfileRecovery(ProfilePersistence store, Exception reason)
    {
        this.store = store;
        Text = "Восстановление kot."; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(560, 460);
        BackColor = Color.FromArgb(21, 23, 24); ForeColor = Color.FromArgb(232, 236, 230);
        Font = new Font("Segoe UI", 10);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        layout.Controls.Add(new Label { Text = "Не удалось открыть профиль", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 0, 3, 12) });
        layout.Controls.Add(new Label { Text = "Подписки и настройки сохранены на диске. Выберите способ восстановления. Старые файлы останутся в папке данных.", AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(3, 0, 3, 14) });
        error.Text = Store.Friendly(reason); layout.Controls.Add(error);
        bool backup = false; try { store.LoadBackup(); backup = true; } catch { }
        Button Add(string text, Func<Task> action)
        {
            var button = new Button { Text = text, Width = 480, Height = 36, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 8, 3, 0), UseVisualStyleBackColor = false, BackColor = Color.FromArgb(40, 45, 46), ForeColor = ForeColor };
            button.Click += async (_, _) =>
            {
                layout.Enabled = false;
                try { await action(); }
                catch (Exception ex) { error.Text = Store.Friendly(ex); }
                finally { if (!IsDisposed) layout.Enabled = true; }
            };
            layout.Controls.Add(button); return button;
        }
        Add("Восстановить предыдущую копию", () => { result = store.RestoreBackup(); DialogResult = DialogResult.OK; return Task.CompletedTask; }).Enabled = backup;
        layout.Controls.Add(new Label { Text = "Пароль резервной копии", AutoSize = true, Margin = new Padding(3, 12, 3, 0) });
        password.Margin = new Padding(3, 4, 3, 0); layout.Controls.Add(password);
        Add("Импортировать .kotbackup", Import);
        Add("Начать с нового профиля", () =>
        {
            if (MessageBox.Show(this, "Открыть приложение без подписок и настроек? Копии прежних файлов останутся в папке данных.", "Новый профиль", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { var fresh = new Profile(); store.Replace(fresh); result = fresh; DialogResult = DialogResult.OK; }
            return Task.CompletedTask;
        });
        Add("Восстановить доступ к интернету", () =>
        {
            KillSwitch.Release(); SystemProxy.Restore(); error.Text = "Блокировка снята, системный прокси восстановлен. VPN сейчас не подключён.";
            return Task.CompletedTask;
        });
        var close = Add("Закрыть", () => { DialogResult = DialogResult.Cancel; return Task.CompletedTask; }); CancelButton = close;
        Controls.Add(layout);
    }
    async Task Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Резервная копия kot. (*.kotbackup)|*.kotbackup" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > 20 * 1024 * 1024 + 52) throw new UserError("Резервная копия слишком большая.");
        byte[] bytes = await File.ReadAllBytesAsync(dialog.FileName); string secret = password.Text;
        var restored = await Task.Run(() => Backup.Import(bytes, secret));
        if (IsDisposed) return;
        store.Replace(restored); result = restored; password.Clear(); DialogResult = DialogResult.OK;
    }
    public static Profile? LoadProfile()
    {
        var store = Store.Persistence();
        try { return store.Load(); }
        catch (Exception ex)
        {
            using var recovery = new ProfileRecovery(store, ex);
            return recovery.ShowDialog() == DialogResult.OK ? recovery.result : null;
        }
    }
}
