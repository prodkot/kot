using Microsoft.Web.WebView2.Core;
namespace Kot.Windows;

public static class WebViewHost
{
    // One browser tree belongs to kot's private profile and its single UI host.
    // This preserves process ownership; Task Manager chooses how to display it.
    public static Task<CoreWebView2Environment> CreateEnvironment(string folder) =>
        CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions { ExclusiveUserDataFolderAccess = true });
}
