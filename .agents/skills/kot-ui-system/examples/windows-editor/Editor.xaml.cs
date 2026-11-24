using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace KotEditor;

public partial class App : Application { }

public partial class Editor : Window
{
    public Editor() => InitializeComponent();

    private async void Start(object sender, RoutedEventArgs args)
    {
        try
        {
            await Canvas.EnsureCoreWebView2Async();
            var view = Canvas.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("editor.example", Path.Combine(AppContext.BaseDirectory, "ui"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            view.WebMessageReceived += (_, message) =>
            {
                if (new Uri(message.Source).Host != "editor.example") return;
                var theme = message.TryGetWebMessageAsString();
                if (theme is not ("light" or "dark")) return;
                var themes = Application.Current.Resources.MergedDictionaries;
                themes[0] = new ResourceDictionary { Source = new Uri($"KotTheme/wpf/{theme}.xaml", UriKind.Relative) };
            };
            view.Navigate("https://editor.example/index.html");
        }
        catch (Exception ex) { Status.Text = $"Не удалось открыть редактор: {ex.Message}"; }
    }
}
