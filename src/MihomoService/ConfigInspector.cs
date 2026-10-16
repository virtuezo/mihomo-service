using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MihomoService;

/// <summary>
/// Конфиги для отчёта diag: копия с замаскированными секретами и список прокси-серверов.
/// YAML разбирается целиком, поэтому секреты скрываются и в записях в одну строку (<c>{ password: … }</c>),
/// и в многострочных значениях — построчная замена по шаблону их пропускала.
/// </summary>
internal static partial class ConfigInspector
{
    private const string Hidden = "***";

    // Ключи прокси, которые безопасно показывать. Значения остальных скрываются целиком:
    // у разных протоколов секреты называются по-разному (password, psk, obfs, protocol-param, reserved…).
    private static readonly HashSet<string> ProxyKeys = new(
    [
        "<<", "name", "type", "server", "port", "ports", "network", "udp", "tls", "sni", "servername", "alpn",
        "skip-cert-verify", "fingerprint", "client-fingerprint", "cipher", "flow", "packet-encoding", "xudp",
        "udp-over-tcp", "udp-over-tcp-version", "ip-version", "interface-name", "routing-mark", "dialer-proxy",
        "tfo", "mptcp", "fast-open", "plugin", "plugin-opts", "mode", "host", "version", "alterId", "alter-id",
        "encryption", "congestion-controller", "udp-relay-mode", "reduce-rtt", "disable-sni", "up", "down", "mtu",
        "smux", "enabled", "protocol", "max-connections", "min-streams", "max-streams", "padding", "brutal-opts",
        "ws-opts", "grpc-opts", "h2-opts", "http-opts", "reality-opts", "peers", "allowed-ips",
    ], StringComparer.OrdinalIgnoreCase);

    // Вне прокси скрываются значения ключей, в имени которых есть эти слова (secret, authentication, users…).
    private static readonly string[] SecretWords =
        ["pass", "secret", "token", "key", "auth", "uuid", "psk", "obfs", "param", "user", "header", "cookie", "cert"];

    /// <summary>Копия YAML без секретов: значения скрыты, у URL оставлены только схема и адрес.</summary>
    public static string Mask(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            // Без разбора секреты не найти — ничего из файла не показываем.
            return $"# YAML не разобран, содержимое скрыто: {ex.Message}{Environment.NewLine}";
        }

        var masked = new YamlStream(stream.Documents.Select(d => new YamlDocument(Copy(d.RootNode, proxy: false))));
        var writer = new StringWriter();
        masked.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    /// <summary>Прокси-серверы из config.yaml и из кэша провайдеров (data\…): тип, адрес и порт.</summary>
    /// <param name="notes">Что не удалось прочитать — для отчёта.</param>
    public static List<(string Type, string Server, string Port)> ListServers(ServicePaths paths, List<string> notes)
    {
        var servers = new List<(string, string, string)>();
        var root = LoadMapping(paths.ConfigFile, notes);
        if (root is null)
            return servers;

        AddServers(root, ServicePaths.ConfigFileName, servers);
        if (Get(root, "proxy-providers") is not YamlMappingNode providers)
            return servers;

        foreach (var (key, node) in providers.Children)
        {
            if (node is not YamlMappingNode provider)
                continue;
            var name = (key as YamlScalarNode)?.Value;
            if (Get(provider, "payload") is YamlSequenceNode)
                AddServers(provider, $"provider {name}", servers, "payload");
            if (Get(provider, "path") is YamlScalarNode { Value: { Length: > 0 } path })
            {
                // Путь провайдера — относительно рабочего каталога ядра (-d data).
                var cache = Path.GetFullPath(Path.Combine(paths.DataDir, path));
                if (LoadMapping(cache, notes) is { } cached)
                    AddServers(cached, $"provider {name}", servers);
            }
            else
            {
                notes.Add($"provider {name}: no path, its proxies are not listed");
            }
        }
        return servers;
    }

    private static void AddServers(YamlMappingNode map, string source, List<(string, string, string)> servers, string key = "proxies")
    {
        if (Get(map, key) is not YamlSequenceNode list)
            return;
        foreach (var proxy in list.Children.OfType<YamlMappingNode>())
        {
            if (RuntimeConfigBuilder.GetMerged(proxy, "server") is { Length: > 0 } server)
                servers.Add((RuntimeConfigBuilder.GetMerged(proxy, "type") ?? "?", server,
                    RuntimeConfigBuilder.GetMerged(proxy, "port") ?? "?"));
        }
    }

    private static YamlMappingNode? LoadMapping(string file, List<string> notes)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(file)));
            if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
                return root;
            notes.Add($"{file}: not a YAML mapping (e.g. a base64 subscription), skipped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException or InvalidOperationException)
        {
            notes.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    // node — значение, которое разрешено показывать; ключи внутри него проверяются заново.
    // proxy — внутри описания прокси: действует список разрешённых ключей, а не запрещённых слов.
    private static YamlNode Copy(YamlNode node, bool proxy) => node switch
    {
        YamlScalarNode scalar => new YamlScalarNode(ScrubUrl(scalar.Value)) { Style = scalar.Style, Tag = scalar.Tag },
        YamlSequenceNode list => new YamlSequenceNode(list.Children.Select(c => Copy(c, proxy))) { Style = list.Style },
        YamlMappingNode map => CopyMapping(map, proxy),
        _ => new YamlScalarNode(Hidden) { Style = ScalarStyle.SingleQuoted },
    };

    private static YamlMappingNode CopyMapping(YamlMappingNode map, bool proxy)
    {
        var copy = new YamlMappingNode { Style = map.Style };
        foreach (var (keyNode, value) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            var secret = proxy
                ? !ProxyKeys.Contains(key)
                : SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));
            // Элементы proxies (и payload провайдеров) — описания прокси.
            var childProxy = proxy || key is "proxies" or "payload";
            copy.Add(Copy(keyNode, proxy: false), secret
                ? new YamlScalarNode(Hidden) { Style = ScalarStyle.SingleQuoted }
                : Copy(value, childProxy));
        }
        return copy;
    }

    /// <summary>Убирает из URL логин, путь и параметры: в них бывают токены подписок и идентификаторы DoH.</summary>
    private static string? ScrubUrl(string? value)
    {
        if (value is null || UrlPattern().Match(value) is not { Success: true } match)
            return value;
        var rest = match.Groups["rest"].Value;
        return match.Groups["scheme"].Value
            + (match.Groups["user"].Success ? Hidden + "@" : "")
            + match.Groups["host"].Value
            + (rest is "" or "/" ? rest : "/" + Hidden);
    }

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)(?<user>[^@/?#\s]*@)?(?<host>[^/?#\s]*)(?<rest>[/?#]\S*)?$")]
    private static partial Regex UrlPattern();
}
