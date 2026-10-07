using System.Text.Json.Nodes;
namespace Kot.Core;
public static class NodeSecurity
{
    public static void Validate(Node node)
    {
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
