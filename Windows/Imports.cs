using Kot.Core;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    async Task ApplyLocalImport(string text, string name)
    {
        if (name.Length > 60) throw new UserError("Название слишком длинное.");
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); importAttempt = attempt;
        try
        {
            var result = await Task.Run(() => Subscriptions.Parse(text), attempt.Token);
            result = await ProviderRouting.ResolveLocal(result, attempt.Token, RequestProxyPort);
            await ProviderAssets.Prepare(result.Nodes, attempt.Token, RequestProxyPort);
            await XraySession.Validate(result.Nodes, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            if (profile.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок.");
            await StopTools(); await ping.Stop(clear: true); await tunnel.Stop();
            try
            {
                attempt.Token.ThrowIfCancellationRequested();
                SaveProfile(p =>
                {
                    var saved = new SavedSubscription { Local = true, Name = name.Length == 0 ? "Локальные серверы" : name, Nodes = result.Nodes, Updated = DateTimeOffset.UtcNow, Selected = result.Nodes[0].Id };
                    p.Subscriptions.Add(saved); p.Activate(saved.Id);
                });
                warnings = result.Warnings; AppLog.Register(profile); AppLog.Write("import", "local nodes=" + result.Nodes.Count);
            }
            finally { ResumeDesired(); }
        }
        finally { if (importAttempt == attempt) importAttempt = null; }
    }
    async Task ImportServersFile(string name)
    {
        using var dialog = new OpenFileDialog { Filter = "Серверы / Clash / Xray (*.yaml;*.yml;*.txt;*.json)|*.yaml;*.yml;*.txt;*.json|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > Subscriptions.MaxBytes) throw new UserError("Файл больше 4 МиБ.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true);
        string text = await reader.ReadToEndAsync(lifetime.Token);
        await ApplyLocalImport(text, name);
    }
    async Task<string?> ReadQr(bool clipboard)
    {
        if (clipboard)
        {
            using var image = Clipboard.GetImage() ?? throw new UserError("В буфере нет картинки. Скопируйте скриншот QR.");
            return await Task.Run(() => QrImages.Decode(image), lifetime.Token);
        }
        using var dialog = new OpenFileDialog { Filter = "Изображение QR (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        if (new FileInfo(dialog.FileName).Length > 16 * 1024 * 1024) throw new UserError("Картинка больше 16 МиБ.");
        string path = dialog.FileName;
        return await Task.Run(() => { using var file = File.OpenRead(path); using var image = Image.FromStream(file); return QrImages.Decode(image); }, lifetime.Token);
    }
    Node QrNode(JsonElement data) => profile.Nodes.FirstOrDefault(n => n.Id == data.GetProperty("id").GetString()) ?? throw new UserError("Сервер уже удалён.");
    object ReadShareQr(JsonElement data)
    {
        var node = QrNode(data); string link = NodeLinks.Export(node);
        using var bitmap = QrImages.Encode(link); using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return new { node.Name, link, image = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()) };
    }
    void ExportShareQr(JsonElement data, string action)
    {
        string link = NodeLinks.Export(QrNode(data));
        if (action == "copyNodeLink") { Clipboard.SetText(link); return; }
        using var bitmap = QrImages.Encode(link);
        if (action == "copyNodeQr") { Clipboard.SetImage(bitmap); return; }
        using var dialog = new SaveFileDialog { Filter = "QR PNG (*.png)|*.png", FileName = "kot-server-qr.png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) bitmap.Save(dialog.FileName, ImageFormat.Png);
    }
}
