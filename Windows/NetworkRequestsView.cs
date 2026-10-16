using Kot.Core;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    bool DownloadsAvailable => tunnel.State == "connected" || !KillSwitch.Active;
    int RequestProxyPort
    {
        get
        {
            if (tunnel.State == "connected" && tunnel.ProxyPort > 0) return tunnel.ProxyPort;
            if (KillSwitch.Active) throw new UserError("Kill switch защищает соединение. Подключитесь к VPN, чтобы загрузить данные.");
            return 0;
        }
    }
}
