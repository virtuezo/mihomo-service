using System.Security.Cryptography;
using System.Text;
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

    // То же для входящих listeners: у них секретами бывают и пути (ws-path), и short-id, и имена gRPC-сервисов.
    private static readonly HashSet<string> ListenerKeys = new(
    [
        "<<", "name", "type", "port", "listen", "udp", "network", "rule", "proxy", "target", "stack", "mtu",
        "auto-route", "auto-detect-interface", "dns-hijack", "inet4-address", "inet6-address", "alpn",
        "congestion-controller", "cipher",
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
            return Unparsed(ex.Message);
        }
        if (stream.Documents.Select(d => RuntimeConfigBuilder.FindCycle(d.RootNode)).FirstOrDefault(c => c is not null) is { } cycle)
            return Unparsed(RuntimeConfigBuilder.DescribeCycle(cycle));

        var masked = new YamlStream(stream.Documents.Select(d =>
            new YamlDocument(Copy(d.RootNode, allowed: null, ProxyTemplates(d.RootNode)))));
        var writer = new StringWriter();
        masked.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static string Unparsed(string reason) => $"# YAML не разобран, содержимое скрыто: {reason}{Environment.NewLine}";

    /// <summary>Прокси-серверы из config.yaml и из кэша провайдеров (data\…): тип, адрес и порт.</summary>
    /// <param name="notes">Что не удалось прочитать — для отчёта.</param>
    public static List<(string Type, string Server, string Port)> ListServers(ServicePaths paths, List<string> notes)
    {
        var servers = new List<(string, string, string)>();
        var root = LoadMapping(paths.ConfigFile, notes);
        if (root is null)
            return servers;

        AddServers(root, ServicePaths.ConfigFileName, servers);
        if (!RuntimeConfigBuilder.TryGet(root, "proxy-providers", out var node) || node is not YamlMappingNode providers)
            return servers;

        foreach (var (key, value) in providers.Children)
        {
            if (value is not YamlMappingNode provider)
                continue;
            var name = (key as YamlScalarNode)?.Value;
            var payload = RuntimeConfigBuilder.TryGet(provider, "payload", out var list) && list is YamlSequenceNode;
            if (payload)
                AddServers(provider, $"provider {name}", servers, "payload");

            // Путь провайдера — относительно рабочего каталога ядра (-d data). Без path mihomo хранит
            // скачанный список в proxies\<md5 от url> (adapter/provider/parser.go, GetPathByHash).
            var cache = RuntimeConfigBuilder.GetMerged(provider, "path") is { Length: > 0 } path
                ? Path.GetFullPath(Path.Combine(paths.DataDir, path))
                : RuntimeConfigBuilder.GetMerged(provider, "url") is { Length: > 0 } url
                    ? Path.Combine(paths.DataDir, "proxies", Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(url))))
                    : null;
            if (cache is not null)
            {
                if (LoadMapping(cache, notes) is { } cached)
                    AddServers(cached, $"provider {name}", servers);
            }
            else if (!payload)
            {
                notes.Add($"provider {name}: neither path, url nor payload, its proxies are not listed");
            }
        }
        return servers;
    }

    private static void AddServers(YamlMappingNode map, string source, List<(string, string, string)> servers, string key = "proxies")
    {
        if (!RuntimeConfigBuilder.TryGet(map, key, out var node) || node is not YamlSequenceNode list)
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
            {
                if (RuntimeConfigBuilder.FindCycle(root) is not { } cycle)
                    return root;
                notes.Add($"{file}: {RuntimeConfigBuilder.DescribeCycle(cycle)}, skipped");
                return null;
            }
            notes.Add($"{file}: not a YAML mapping (e.g. a base64 subscription), skipped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException or InvalidOperationException)
        {
            notes.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Описания, которые прокси подмешивают через «&lt;&lt;: *якорь». Сам якорь может быть объявлен вне proxies
    /// (например, под x-common:) — его копия маскируется так же строго, как прокси.
    /// </summary>
    private static HashSet<YamlNode> ProxyTemplates(YamlNode root)
    {
        var templates = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        foreach (var map in root.AllNodes.OfType<YamlMappingNode>())
        {
            foreach (var (key, value) in map.Children)
            {
                if (key is YamlScalarNode { Value: "proxies" or "payload" } && value is YamlSequenceNode list)
                {
                    foreach (var proxy in list.Children.OfType<YamlMappingNode>())
                        AddMergeSources(proxy, templates);
                }
            }
        }
        return templates;
    }

    private static void AddMergeSources(YamlMappingNode map, HashSet<YamlNode> found)
    {
        if (!RuntimeConfigBuilder.TryGet(map, "<<", out var merge))
            return;
        IEnumerable<YamlMappingNode> sources = merge switch
        {
            YamlMappingNode single => [single],
            YamlSequenceNode list => list.Children.OfType<YamlMappingNode>(),
            _ => [],
        };
        foreach (var source in sources)
        {
            if (found.Add(source))
                AddMergeSources(source, found);
        }
    }

    // node — значение, которое разрешено показывать; ключи внутри него проверяются заново.
    // allowed — внутри описания прокси или listener: действует список разрешённых ключей, а не запрещённых слов.
    private static YamlNode Copy(YamlNode node, HashSet<string>? allowed, HashSet<YamlNode> templates) => node switch
    {
        YamlScalarNode scalar => new YamlScalarNode(ScrubUrl(scalar.Value)) { Style = scalar.Style, Tag = scalar.Tag },
        YamlSequenceNode list => new YamlSequenceNode(list.Children.Select(c => Copy(c, allowed, templates))) { Style = list.Style },
        YamlMappingNode map => CopyMapping(map, allowed ?? (templates.Contains(map) ? ProxyKeys : null), templates),
        _ => new YamlScalarNode(Hidden) { Style = ScalarStyle.SingleQuoted },
    };

    private static YamlMappingNode CopyMapping(YamlMappingNode map, HashSet<string>? allowed, HashSet<YamlNode> templates)
    {
        var copy = new YamlMappingNode { Style = map.Style };
        foreach (var (keyNode, value) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            var secret = allowed is not null
                ? !allowed.Contains(key)
                : SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));
            // Элементы proxies (и payload провайдеров) — описания прокси, элементы listeners — входящих.
            var childAllowed = allowed ?? key switch
            {
                "proxies" or "payload" => ProxyKeys,
                "listeners" => ListenerKeys,
                _ => null,
            };
            copy.Add(Copy(keyNode, allowed: null, templates), secret
                ? new YamlScalarNode(Hidden) { Style = ScalarStyle.SingleQuoted }
                : Copy(value, childAllowed, templates));
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

    // Логин и пароль — всё до последней «@» перед путём (так делит адрес Go, а значит, и mihomo):
    // в пароле «@» встречается и без экранирования.
    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)(?<user>[^/?#\s]*@)?(?<host>[^/?#\s]*)(?<rest>[/?#]\S*)?$")]
    private static partial Regex UrlPattern();
}
