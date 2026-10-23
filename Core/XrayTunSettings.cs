using System.Text.Json.Nodes;
namespace Kot.Core;

public enum XrayPlatform { Windows, Linux }

public static class XrayTunSettings
{
    public static void Apply(JsonObject settings, XrayPlatform platform)
    {
        if (platform == XrayPlatform.Linux)
        {
            // A privileged, restricted helper owns routes, addresses and resolved.
            // Xray receives its already-open TUN FD and has no network capabilities.
            return;
        }
        settings["desc"] = "kot. Xray";
        settings["gateway"] = new JsonArray("172.28.231.1/30", "fdfe:dcba:231::1/126");
        settings["dns"] = new JsonArray("172.28.231.2", "fdfe:dcba:231::2");
        settings["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0");
        settings["autoOutboundsInterface"] = "auto";
        settings["autoSystemWfpBlockLeak"] = new JsonArray("dns", "misconfigtun");
    }
}
