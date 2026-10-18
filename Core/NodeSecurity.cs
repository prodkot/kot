using System.Text.Json.Nodes;
namespace Kot.Core;
public static class NodeSecurity
{
    public static void Validate(Node node)
    {
        if (node.FullConfig != null) XrayConfig.Validate(node.FullConfig);
        else if (node.Protocol == "xray") throw new UserError("Отсутствует клиентская конфигурация Xray.");
        if (node.GeoIpUrl == null || node.GeoSiteUrl == null) throw new UserError("Неверные адреса геобаз подписки.");
        if (node.GeoIpUrl.Length > 0) RoutingProfiles.Source(node.GeoIpUrl);
        if (node.GeoSiteUrl.Length > 0) RoutingProfiles.Source(node.GeoSiteUrl);
        if (node.GeoRevision < 0) throw new UserError("Неверная версия геобаз подписки.");
        Xhttp.Validate(node);
        if (node.Outbound["tls"] is JsonObject tls && tls["insecure"] is JsonValue value
            && value.TryGetValue<bool>(out bool insecure) && insecure)
            throw new UserError("Сервер отключает проверку TLS-сертификата. Обновите подписку или запросите безопасную конфигурацию у провайдера.");
    }
    public static void RejectInsecureFlag(params string[] flags)
    {
        if (flags.Any(v => v.Trim() == "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            throw new UserError("Ссылка отключает проверку TLS-сертификата. Такой сервер не добавлен.");
    }
}
