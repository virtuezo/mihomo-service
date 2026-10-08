using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MihomoService;

internal sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record RuntimeConfig(
    string Yaml,
    string GroupName,
    bool GroupGenerated,
    IReadOnlyList<string> Proxies,
    IReadOnlyList<string> Providers);

/// <summary>
/// Превращает пользовательский config.yaml в конфиг, с которым запускается ядро:
/// <list type="bullet">
/// <item>все прокси из <c>proxies</c> (и провайдеры из <c>proxy-providers</c>) собираются в fallback-группу
///   в порядке объявления — первый живой прокси получает трафик;</item>
/// <item>TUN принудительно включён (auto-route, auto-detect-interface), системный прокси ядро не трогает;</item>
/// <item>если не заданы rules/dns — подставляются значения по умолчанию;</item>
/// <item>служебный раздел <c>service:</c> вырезается, ядро его не видит.</item>
/// </list>
/// Любые другие ключи пользователя передаются ядру как есть.
/// </summary>
internal static class RuntimeConfigBuilder
{
    public const string ServiceSection = "service";

    private const string DefaultGroupName = "PROXY";
    private const string DefaultTestUrl = "https://www.gstatic.com/generate_204";
    private const int DefaultInterval = 300;
    private const int DefaultTimeout = 5000;

    private static readonly string[] DefaultRules =
    [
        "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
        "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
        "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve",
        "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
        "IP-CIDR,169.254.0.0/16,DIRECT,no-resolve",
        "IP-CIDR,100.64.0.0/10,DIRECT,no-resolve",
        "IP-CIDR6,::1/128,DIRECT,no-resolve",
        "IP-CIDR6,fc00::/7,DIRECT,no-resolve",
        "IP-CIDR6,fe80::/10,DIRECT,no-resolve",
    ];

    private static readonly string[] DefaultFakeIpFilter =
    [
        "*.lan",
        "*.local",
        "*.localdomain",
        "+.home.arpa",
        "+.msftconnecttest.com",
        "+.msftncsi.com",
        "time.windows.com",
        "+.pool.ntp.org",
    ];

    private sealed record ServiceOptions(string GroupName, string TestUrl, int Interval, int Timeout);

    public static RuntimeConfig Build(string userYaml)
    {
        var root = Parse(userYaml);
        var options = TakeServiceOptions(root);
        var proxies = ReadProxyNames(root);
        var providers = ReadProviderNames(root);

        if (proxies.Count == 0 && providers.Count == 0)
            throw new ConfigException("В config.yaml нет ни одного прокси: заполните раздел proxies (или proxy-providers).");

        var generated = EnsureFallbackGroup(root, options, proxies, providers);
        EnsureRules(root, options.GroupName);
        ForceTun(root);
        EnsureDns(root);
        root.Children[new YamlScalarNode("mode")] = new YamlScalarNode("rule");
        SetDefault(root, "log-level", new YamlScalarNode("info"));

        return new RuntimeConfig(Serialize(root), options.GroupName, generated, proxies, providers);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"config.yaml: ошибка YAML в строке {ex.Start.Line}, столбец {ex.Start.Column}: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // YamlDotNet бросает его на некоторых обрывах документа (незакрытые скобки в конце файла и т.п.).
            throw new ConfigException("config.yaml: ошибка YAML — документ оборван (незакрытая скобка или кавычка?).", ex);
        }

        if (stream.Documents.Count == 0 || IsNull(stream.Documents[0].RootNode))
            throw new ConfigException("config.yaml пуст.");

        return stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new ConfigException("config.yaml: на верхнем уровне должен быть словарь (proxies: ..., и т.д.).");
    }

    private static ServiceOptions TakeServiceOptions(YamlMappingNode root)
    {
        var key = new YamlScalarNode(ServiceSection);
        if (!root.Children.TryGetValue(key, out var node) || IsNull(node))
        {
            root.Children.Remove(key);
            return new ServiceOptions(DefaultGroupName, DefaultTestUrl, DefaultInterval, DefaultTimeout);
        }

        root.Children.Remove(key);
        if (node is not YamlMappingNode section)
            throw new ConfigException($"config.yaml: раздел {ServiceSection} должен быть словарём.");

        var groupName = GetString(section, "group-name") ?? DefaultGroupName;
        if (groupName.Length == 0 || groupName.Contains(','))
            throw new ConfigException($"config.yaml: {ServiceSection}.group-name не может быть пустым или содержать запятую.");

        return new ServiceOptions(
            groupName,
            GetString(section, "test-url") ?? DefaultTestUrl,
            GetPositiveInt(section, "interval") ?? DefaultInterval,
            GetPositiveInt(section, "timeout") ?? DefaultTimeout);
    }

    private static List<string> ReadProxyNames(YamlMappingNode root)
    {
        if (!TryGet(root, "proxies", out var node) || IsNull(node))
            return [];
        if (node is not YamlSequenceNode list)
            throw new ConfigException("config.yaml: proxies должен быть списком.");

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Children.Count; i++)
        {
            var name = list.Children[i] is YamlMappingNode proxy ? GetString(proxy, "name") : null;
            if (string.IsNullOrEmpty(name))
                throw new ConfigException($"config.yaml: у прокси №{i + 1} в proxies нет поля name.");
            if (!seen.Add(name))
                throw new ConfigException($"config.yaml: имя прокси \"{name}\" встречается в proxies несколько раз.");
            names.Add(name);
        }
        return names;
    }

    private static List<string> ReadProviderNames(YamlMappingNode root)
    {
        if (!TryGet(root, "proxy-providers", out var node) || IsNull(node))
            return [];
        if (node is not YamlMappingNode providers)
            throw new ConfigException("config.yaml: proxy-providers должен быть словарём.");

        return providers.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToList();
    }

    /// <returns>false, если пользователь сам описал группу с таким именем — тогда она остаётся как есть.</returns>
    private static bool EnsureFallbackGroup(
        YamlMappingNode root, ServiceOptions options, List<string> proxies, List<string> providers)
    {
        var groups = GetOrAddSequence(root, "proxy-groups");
        if (groups.Children.OfType<YamlMappingNode>().Any(g => GetString(g, "name") == options.GroupName))
            return false;

        var group = new YamlMappingNode
        {
            { "name", Quoted(options.GroupName) },
            { "type", "fallback" },
        };
        if (proxies.Count > 0)
            group.Add("proxies", new YamlSequenceNode(proxies.Select(Quoted)));
        if (providers.Count > 0)
            group.Add("use", new YamlSequenceNode(providers.Select(Quoted)));
        group.Add("url", options.TestUrl);
        group.Add("interval", options.Interval.ToString(CultureInfo.InvariantCulture));
        group.Add("timeout", options.Timeout.ToString(CultureInfo.InvariantCulture));
        group.Add("lazy", "false");

        groups.Children.Insert(0, group);
        return true;
    }

    private static void EnsureRules(YamlMappingNode root, string groupName)
    {
        if (TryGet(root, "rules", out var node) && node is YamlSequenceNode { Children.Count: > 0 })
            return;

        root.Children[new YamlScalarNode("rules")] =
            new YamlSequenceNode(DefaultRules.Append($"MATCH,{groupName}").Select(r => new YamlScalarNode(r)));
    }

    private static void ForceTun(YamlMappingNode root)
    {
        var tun = GetOrAddMapping(root, "tun");
        tun.Children[new YamlScalarNode("enable")] = new YamlScalarNode("true");
        tun.Children[new YamlScalarNode("auto-route")] = new YamlScalarNode("true");
        tun.Children[new YamlScalarNode("auto-detect-interface")] = new YamlScalarNode("true");
        SetDefault(tun, "stack", new YamlScalarNode("mixed"));
        SetDefault(tun, "dns-hijack", new YamlSequenceNode(new YamlScalarNode("any:53"), new YamlScalarNode("tcp://any:53")));
    }

    private static void EnsureDns(YamlMappingNode root)
    {
        // Без DNS-модуля ядра dns-hijack в TUN не работает, поэтому он включается всегда.
        var dns = GetOrAddMapping(root, "dns");
        dns.Children[new YamlScalarNode("enable")] = new YamlScalarNode("true");
        SetDefault(dns, "enhanced-mode", new YamlScalarNode("fake-ip"));
        if (GetString(dns, "enhanced-mode") == "fake-ip")
        {
            SetDefault(dns, "fake-ip-range", new YamlScalarNode("198.18.0.1/16"));
            SetDefault(dns, "fake-ip-filter", new YamlSequenceNode(DefaultFakeIpFilter.Select(Quoted)));
        }
        SetDefault(dns, "default-nameserver", new YamlSequenceNode(new YamlScalarNode("1.1.1.1"), new YamlScalarNode("8.8.8.8")));
        SetDefault(dns, "nameserver", new YamlSequenceNode(
            new YamlScalarNode("https://1.1.1.1/dns-query"),
            new YamlScalarNode("https://8.8.8.8/dns-query")));
    }

    private static string Serialize(YamlMappingNode root)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("# Сгенерировано MihomoService из config.yaml. Не редактируйте — файл перезаписывается.");
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode node) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out node!);

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: null or "" or "~" or "null" or "Null" or "NULL" };

    private static void SetDefault(YamlMappingNode map, string key, YamlNode value)
    {
        if (!TryGet(map, key, out var node) || IsNull(node))
            map.Children[new YamlScalarNode(key)] = value;
    }

    private static YamlMappingNode GetOrAddMapping(YamlMappingNode map, string key)
    {
        if (TryGet(map, key, out var node) && !IsNull(node))
            return node as YamlMappingNode ?? throw new ConfigException($"config.yaml: {key} должен быть словарём.");

        var created = new YamlMappingNode();
        map.Children[new YamlScalarNode(key)] = created;
        return created;
    }

    private static YamlSequenceNode GetOrAddSequence(YamlMappingNode map, string key)
    {
        if (TryGet(map, key, out var node) && !IsNull(node))
            return node as YamlSequenceNode ?? throw new ConfigException($"config.yaml: {key} должен быть списком.");

        var created = new YamlSequenceNode();
        map.Children[new YamlScalarNode(key)] = created;
        return created;
    }

    private static string? GetString(YamlMappingNode map, string key) =>
        TryGet(map, key, out var node) && !IsNull(node) && node is YamlScalarNode scalar ? scalar.Value : null;

    private static int? GetPositiveInt(YamlMappingNode map, string key)
    {
        var text = GetString(map, key);
        if (text is null)
            return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
            return value;
        throw new ConfigException($"config.yaml: {ServiceSection}.{key} должен быть положительным целым числом, а не \"{text}\".");
    }

    private static YamlScalarNode Quoted(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
}
