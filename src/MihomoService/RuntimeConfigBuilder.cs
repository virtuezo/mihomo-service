using System.Globalization;
using System.Net;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MihomoService;

internal sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record RuntimeConfig(
    string Yaml,
    string GroupName,
    bool GroupGenerated,
    IReadOnlyList<string> Proxies,
    IReadOnlyList<string> Providers,
    bool KillSwitch,
    bool AllowLan,
    IReadOnlyList<IPNetwork> TunnelNetworks,
    IReadOnlyList<string> Warnings);

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
    private const string DefaultTunInet4 = "198.18.0.1/30";
    private const string DefaultTunInet6 = "fdfe:dcba:9876::1/126";

    /// <summary>Сети адресов TUN по умолчанию — их же разрешает kill-switch, пока конфиг не разобран.</summary>
    public static readonly IPNetwork DefaultTunnelV4 = ParsePrefix(DefaultTunInet4, "tun.inet4-address");
    public static readonly IPNetwork DefaultTunnelV6 = ParsePrefix(DefaultTunInet6, "tun.inet6-address");

    // Lan — частная сеть: при kill-switch с allow-lan: false она закрыта (REJECT), иначе идёт напрямую.
    private static readonly (string Match, bool Lan)[] DefaultRules =
    [
        ("IP-CIDR,127.0.0.0/8", false),
        ("IP-CIDR,10.0.0.0/8", true),
        ("IP-CIDR,172.16.0.0/12", true),
        ("IP-CIDR,192.168.0.0/16", true),
        ("IP-CIDR,169.254.0.0/16", false),
        ("IP-CIDR6,::1/128", false),
        ("IP-CIDR6,fc00::/7", true),
        ("IP-CIDR6,fe80::/10", false),
    ];

    // «+.» — домен и все поддомены; «*.» в mihomo совпадает только с одним уровнем.
    private static readonly string[] DefaultFakeIpFilter =
    [
        "+.lan",
        "+.local",
        "+.localdomain",
        "+.home.arpa",
        "+.msftconnecttest.com",
        "+.msftncsi.com",
        "time.windows.com",
        "+.pool.ntp.org",
    ];

    private sealed record ServiceOptions(string GroupName, string TestUrl, int Interval, int Timeout, bool KillSwitch, bool AllowLan)
    {
        public static readonly ServiceOptions Default =
            new(DefaultGroupName, DefaultTestUrl, DefaultInterval, DefaultTimeout, KillSwitch: true, AllowLan: true);
    }

    public static RuntimeConfig Build(string userYaml)
    {
        var warnings = new List<string>();
        var root = Parse(userYaml, out var extraDocuments);
        if (extraDocuments)
            warnings.Add("config.yaml состоит из нескольких YAML-документов (разделитель ---): используется только " +
                "первый, остальные игнорируются.");
        var options = TakeServiceOptions(root);
        var proxies = ReadProxyNames(root);
        var providers = ReadProviderNames(root);

        if (proxies.Count == 0 && providers.Count == 0)
            throw new ConfigException("В config.yaml нет ни одного прокси: заполните раздел proxies (или proxy-providers).");

        var generated = EnsureFallbackGroup(root, options, proxies, providers);
        EnsureRules(root, options);
        if (options.KillSwitch)
            AppendRejectRule(root);
        var tunnelNetworks = ForceTun(root);
        EnsureDns(root);
        root.Children[new YamlScalarNode("mode")] = new YamlScalarNode("rule");
        SetDefault(root, "log-level", new YamlScalarNode("info"));
        // IPv6 в интернет не нужен: kill-switch его и так не выпускает мимо туннеля, а внутри туннеля
        // он лишь даёт приложениям второй путь со своими адресами.
        SetDefault(root, "ipv6", new YamlScalarNode("false"));

        var withoutUdp = ReadProxiesWithoutUdp(root);
        if (withoutUdp.Count > 0)
            warnings.Add($"Прокси без UDP: {string.Join(", ", withoutUdp)} (нет udp: true). Пока такой прокси выбран, " +
                (options.KillSwitch
                    ? "UDP (QUIC, звонки, игры) блокируется. "
                    : "UDP (QUIC, звонки, игры) идёт напрямую, мимо прокси. ") +
                "Если сервер поддерживает UDP, добавьте ему udp: true.");
        if (options.KillSwitch && !generated && GroupContainsDirect(root, options.GroupName))
            warnings.Add($"Группа {options.GroupName} содержит DIRECT: если все прокси недоступны, трафик пойдёт " +
                "напрямую через ядро — kill-switch это не блокирует. Уберите DIRECT из группы, чтобы без туннеля " +
                "интернета не было.");

        return new RuntimeConfig(Serialize(root), options.GroupName, generated, proxies, providers,
            options.KillSwitch, options.AllowLan, tunnelNetworks, warnings);
    }

    private static YamlMappingNode Parse(string yaml) => Parse(yaml, out _);

    /// <param name="extraDocuments">true, если после первого документа есть ещё непустые (их ни служба, ни ядро не читают).</param>
    private static YamlMappingNode Parse(string yaml, out bool extraDocuments)
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

        extraDocuments = stream.Documents.Skip(1).Any(d => !IsNull(d.RootNode));
        return stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new ConfigException("config.yaml: на верхнем уровне должен быть словарь (proxies: ..., и т.д.).");
    }

    private static ServiceOptions TakeServiceOptions(YamlMappingNode root)
    {
        var key = new YamlScalarNode(ServiceSection);
        if (!root.Children.TryGetValue(key, out var node) || IsNull(node))
        {
            root.Children.Remove(key);
            return ServiceOptions.Default;
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
            GetPositiveInt(section, "timeout") ?? DefaultTimeout,
            GetBool(section, "kill-switch") ?? ServiceOptions.Default.KillSwitch,
            GetBool(section, "allow-lan") ?? ServiceOptions.Default.AllowLan);
    }

    /// <summary>
    /// Настройки kill-switch, когда полный разбор config.yaml невозможен (файла нет или он битый):
    /// лучше перекрыть сеть, чем оставить её открытой, поэтому по умолчанию kill-switch включён.
    /// Значения из раздела service берутся, если его удаётся прочитать.
    /// </summary>
    public static (bool KillSwitch, bool AllowLan) ReadKillSwitchFallback(string? userYaml)
    {
        try
        {
            if (userYaml is not null && Parse(userYaml) is var root
                && TryGet(root, ServiceSection, out var node) && node is YamlMappingNode section)
                return (GetBool(section, "kill-switch") ?? ServiceOptions.Default.KillSwitch,
                    GetBool(section, "allow-lan") ?? ServiceOptions.Default.AllowLan);
        }
        catch (ConfigException)
        {
        }
        return (ServiceOptions.Default.KillSwitch, ServiceOptions.Default.AllowLan);
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
            var name = list.Children[i] is YamlMappingNode proxy ? GetMerged(proxy, "name") : null;
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

    private static void EnsureRules(YamlMappingNode root, ServiceOptions options)
    {
        if (TryGet(root, "rules", out var node) && !IsNull(node))
        {
            if (node is not YamlSequenceNode list)
                throw new ConfigException("config.yaml: rules должен быть списком.");
            if (list.Children.Count > 0)
                return;
        }

        // Частные сети вне своей подсети маршрутизируются в TUN, и по DIRECT ядро соединилось бы с ними само —
        // kill-switch ядру выход разрешает. Поэтому при allow-lan: false они закрыты и здесь.
        var lan = options.KillSwitch && !options.AllowLan ? "REJECT" : "DIRECT";
        root.Children[new YamlScalarNode("rules")] = new YamlSequenceNode(DefaultRules
            .Select(r => $"{r.Match},{(r.Lan ? lan : "DIRECT")},no-resolve")
            .Append($"MATCH,{options.GroupName}")
            .Select(r => new YamlScalarNode(r)));
    }

    /// <summary>
    /// mihomo пропускает правило, если его прокси не передаёт UDP (socks5, ss, vless и т.п. без udp: true),
    /// а после последнего правила отправляет соединение в DIRECT. Так UDP (QUIC, STUN, звонки) уходил бы
    /// напрямую от ядра с настоящим IP — kill-switch ядру выход разрешает. Завершающий MATCH,REJECT
    /// превращает такую утечку в блокировку; TCP до него не доходит, если раньше есть MATCH.
    /// </summary>
    private static void AppendRejectRule(YamlMappingNode root)
    {
        var rules = (YamlSequenceNode)root["rules"];
        if (rules.Children.LastOrDefault() is YamlScalarNode { Value: { } last }
            && last.Replace(" ", "").Equals(RejectRule, StringComparison.OrdinalIgnoreCase))
            return;
        rules.Add(RejectRule);
    }

    private const string RejectRule = "MATCH,REJECT";

    // Эти типы передают UDP всегда; остальным нужен udp: true, а http не передаёт его вовсе.
    private static readonly HashSet<string> AlwaysUdpTypes =
        new(["hysteria", "hysteria2", "tuic", "wireguard"], StringComparer.OrdinalIgnoreCase);

    private static List<string> ReadProxiesWithoutUdp(YamlMappingNode root)
    {
        if (!TryGet(root, "proxies", out var node) || node is not YamlSequenceNode list)
            return [];
        return list.Children.OfType<YamlMappingNode>()
            .Where(p => GetMerged(p, "type") is { } type
                && (type.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || (!AlwaysUdpTypes.Contains(type) && !CarriesUdp(GetMerged(p, "udp")))))
            .Select(p => GetMerged(p, "name")!)
            .ToList();
    }

    // mihomo читает udp как bool: true или ненулевое число; yes/on он отвергает при разборе.
    private static bool CarriesUdp(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number != 0));

    /// <returns>Сети адресов TUN-адаптера — их трафик kill-switch считает туннельным.</returns>
    private static List<IPNetwork> ForceTun(YamlMappingNode root)
    {
        var tun = GetOrAddMapping(root, "tun");
        tun.Children[new YamlScalarNode("enable")] = new YamlScalarNode("true");
        tun.Children[new YamlScalarNode("auto-route")] = new YamlScalarNode("true");
        tun.Children[new YamlScalarNode("auto-detect-interface")] = new YamlScalarNode("true");
        SetDefault(tun, "stack", new YamlScalarNode("mixed"));
        SetDefault(tun, "dns-hijack", new YamlSequenceNode(new YamlScalarNode("any:53"), new YamlScalarNode("tcp://any:53")));
        // Адрес задаётся явно, чтобы фильтр kill-switch гарантированно совпадал с адресом адаптера.
        SetDefault(tun, "inet4-address", new YamlSequenceNode(new YamlScalarNode(DefaultTunInet4)));

        var networks = ReadPrefixes(tun, "inet4-address");
        var inet6 = ReadPrefixes(tun, "inet6-address");
        networks.AddRange(inet6.Count > 0 ? inet6 : [DefaultTunnelV6]);
        return networks;
    }

    private static List<IPNetwork> ReadPrefixes(YamlMappingNode tun, string key)
    {
        if (!TryGet(tun, key, out var node) || IsNull(node))
            return [];
        IEnumerable<YamlNode> items = node switch
        {
            YamlSequenceNode list => list.Children,
            YamlScalarNode scalar => [scalar],
            _ => throw new ConfigException($"config.yaml: tun.{key} должен быть списком адресов."),
        };
        return items.Select(i => ParsePrefix((i as YamlScalarNode)?.Value ?? "", $"tun.{key}")).ToList();
    }

    /// <summary>Разбирает «адрес/длина» (адрес может быть адресом хоста, как 198.18.0.1/30) в сеть.</summary>
    internal static IPNetwork ParsePrefix(string text, string what)
    {
        var slash = text.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(text.AsSpan(0, slash), out var address)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            throw new ConfigException($"config.yaml: {what}: \"{text}\" — ожидается адрес с длиной префикса, например 198.18.0.1/30.");

        var bytes = address.GetAddressBytes();
        for (var bit = length; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), length);
    }

    private static bool GroupContainsDirect(YamlMappingNode root, string groupName) =>
        TryGet(root, "proxy-groups", out var node) && node is YamlSequenceNode groups
        && groups.Children.OfType<YamlMappingNode>()
            .Where(g => GetString(g, "name") == groupName)
            .Any(g => TryGet(g, "proxies", out var list) && list is YamlSequenceNode items
                && items.Children.OfType<YamlScalarNode>().Any(p => p.Value == "DIRECT"));

    private static void EnsureDns(YamlMappingNode root)
    {
        // Без DNS-модуля ядра dns-hijack в TUN не работает, поэтому он включается всегда.
        var dns = GetOrAddMapping(root, "dns");
        dns.Children[new YamlScalarNode("enable")] = new YamlScalarNode("true");
        SetDefault(dns, "ipv6", new YamlScalarNode("false"));
        SetDefault(dns, "enhanced-mode", new YamlScalarNode("fake-ip"));
        if (GetString(dns, "enhanced-mode") == "fake-ip")
        {
            SetDefault(dns, "fake-ip-range", new YamlScalarNode("198.18.0.1/16"));
            SetDefault(dns, "fake-ip-filter", new YamlSequenceNode(DefaultFakeIpFilter.Select(Quoted)));
        }
        SetDefault(dns, "default-nameserver", new YamlSequenceNode(new YamlScalarNode("1.1.1.1"), new YamlScalarNode("8.8.8.8")));
        SetDefault(dns, "nameserver", DefaultDoh());

        // Запросы к nameserver идут по правилам, т.е. через туннель, а не напрямую от ядра
        // (такой трафик kill-switch пропустил бы — ядру выход разрешён).
        SetDefault(dns, "respect-rules", new YamlScalarNode("true"));
        // С respect-rules ядру нужен отдельный DNS для имён самих прокси-серверов — он работает напрямую.
        if (ParseBool(GetString(dns, "respect-rules")) == true)
            SetDefault(dns, "proxy-server-nameserver", DefaultDoh());
    }

    private static YamlSequenceNode DefaultDoh() => new(
        new YamlScalarNode("https://1.1.1.1/dns-query"),
        new YamlScalarNode("https://8.8.8.8/dns-query"));

    private static string Serialize(YamlMappingNode root)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("# Сгенерировано MihomoService из config.yaml. Не редактируйте — файл перезаписывается.");
        ClearAnchors(root);
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    /// <summary>
    /// Убирает якоря, чтобы общие узлы сохранялись развёрнутыми: YamlDotNet пишет сохранённые при разборе имена
    /// якорей, и узел, повторно объявивший якорь с тем же именем, превратился бы в ссылку на первый.
    /// </summary>
    private static void ClearAnchors(YamlNode root)
    {
        foreach (var node in root.AllNodes)
            node.Anchor = AnchorName.Empty;
    }

    /// <summary>Значение ключа с учётом слияния «&lt;&lt;: *якорь» (YamlDotNet его не раскрывает); свой ключ важнее.</summary>
    internal static string? GetMerged(YamlMappingNode map, string key)
    {
        if (TryGet(map, key, out _) || !TryGet(map, "<<", out var merge))
            return GetString(map, key);

        IEnumerable<YamlMappingNode> sources = merge switch
        {
            YamlMappingNode single => [single],
            YamlSequenceNode list => list.Children.OfType<YamlMappingNode>(),
            _ => [],
        };
        return sources.Select(s => GetMerged(s, key)).FirstOrDefault(v => v is not null);
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

    private static bool? GetBool(YamlMappingNode map, string key)
    {
        var text = GetString(map, key);
        return text is null
            ? null
            : ParseBool(text) ?? throw new ConfigException($"config.yaml: {ServiceSection}.{key} должен быть true или false, а не \"{text}\".");
    }

    private static bool? ParseBool(string? text) => text?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

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
