using System.Net;
using YamlDotNet.RepresentationModel;

namespace MihomoService.Tests;

public class RuntimeConfigBuilderTests
{
    private const string TwoProxies = """
        proxies:
          - { name: first, type: socks5, server: 127.0.0.1, port: 1080 }
          - { name: "second #2", type: socks5, server: 127.0.0.1, port: 1081 }
        """;

    [Fact]
    public void Builds_fallback_group_from_proxies_in_declared_order()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies);
        var root = Load(config.Yaml);

        var group = Assert.IsType<YamlMappingNode>(Seq(root, "proxy-groups").Children[0]);
        Assert.Equal("PROXY", Str(group, "name"));
        Assert.Equal("fallback", Str(group, "type"));
        Assert.Equal(["first", "second #2"], Strings(Seq(group, "proxies")));
        Assert.Equal("https://www.gstatic.com/generate_204", Str(group, "url"));
        Assert.Equal("300", Str(group, "interval"));
        Assert.True(config.GroupGenerated);
    }

    [Fact]
    public void Forces_tun_and_rule_mode_but_keeps_user_tun_options()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies + """

            mode: direct
            tun:
              enable: false
              auto-route: false
              stack: gvisor
            """);
        var tun = Map(Load(config.Yaml), "tun");

        Assert.Equal("true", Str(tun, "enable"));
        Assert.Equal("true", Str(tun, "auto-route"));
        Assert.Equal("true", Str(tun, "auto-detect-interface"));
        Assert.Equal("gvisor", Str(tun, "stack"));
        Assert.Equal(["any:53", "tcp://any:53"], Strings(Seq(tun, "dns-hijack")));
        Assert.Equal("rule", Str(Load(config.Yaml), "mode"));
    }

    [Fact]
    public void Default_rules_send_private_ranges_direct_and_everything_else_to_group()
    {
        var rules = Strings(Seq(Load(RuntimeConfigBuilder.Build(TwoProxies).Yaml), "rules"));

        Assert.Contains("IP-CIDR,192.168.0.0/16,DIRECT,no-resolve", rules);
        Assert.Equal("MATCH,PROXY", rules[^1]);
    }

    [Fact]
    public void Default_dns_goes_through_the_tunnel()
    {
        var dns = Map(Load(RuntimeConfigBuilder.Build(TwoProxies).Yaml), "dns");

        Assert.Equal("true", Str(dns, "respect-rules"));
        Assert.NotEmpty(Seq(dns, "proxy-server-nameserver").Children); // обязателен при respect-rules
        Assert.Contains("+.lan", Strings(Seq(dns, "fake-ip-filter")));
    }

    [Fact]
    public void User_can_opt_out_of_respect_rules()
    {
        var dns = Map(Load(RuntimeConfigBuilder.Build(TwoProxies + "\ndns: { respect-rules: false }").Yaml), "dns");

        Assert.Equal("false", Str(dns, "respect-rules"));
        Assert.False(dns.Children.ContainsKey(new YamlScalarNode("proxy-server-nameserver")));
    }

    [Fact]
    public void Default_rules_do_not_send_cgnat_direct()
    {
        var rules = Strings(Seq(Load(RuntimeConfigBuilder.Build(TwoProxies).Yaml), "rules"));

        Assert.DoesNotContain(rules, r => r!.Contains("100.64.0.0/10"));
    }

    [Fact]
    public void Keeps_user_rules_and_dns_but_enables_dns()
    {
        var root = Load(RuntimeConfigBuilder.Build(TwoProxies + """

            rules:
              - DOMAIN-SUFFIX,ru,DIRECT
              - MATCH,PROXY
            dns:
              enable: false
              enhanced-mode: redir-host
              nameserver: [https://dns.example/dns-query]
            """).Yaml);

        Assert.Equal(["DOMAIN-SUFFIX,ru,DIRECT", "MATCH,PROXY"], Strings(Seq(root, "rules")));
        var dns = Map(root, "dns");
        Assert.Equal("true", Str(dns, "enable"));
        Assert.Equal("redir-host", Str(dns, "enhanced-mode"));
        Assert.Equal(["https://dns.example/dns-query"], Strings(Seq(dns, "nameserver")));
        Assert.False(dns.Children.ContainsKey(new YamlScalarNode("fake-ip-range")));
    }

    [Fact]
    public void Applies_and_strips_service_section()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies + """

            service:
              group-name: Auto
              test-url: http://cp.cloudflare.com
              interval: 60
              timeout: 2000
            """);
        var root = Load(config.Yaml);
        var group = Assert.IsType<YamlMappingNode>(Seq(root, "proxy-groups").Children[0]);

        Assert.False(root.Children.ContainsKey(new YamlScalarNode(RuntimeConfigBuilder.ServiceSection)));
        Assert.Equal("Auto", Str(group, "name"));
        Assert.Equal("http://cp.cloudflare.com", Str(group, "url"));
        Assert.Equal("60", Str(group, "interval"));
        Assert.Equal("2000", Str(group, "timeout"));
        Assert.Equal("MATCH,Auto", Strings(Seq(root, "rules"))[^1]);
    }

    [Fact]
    public void Providers_go_to_use_list()
    {
        var root = Load(RuntimeConfigBuilder.Build("""
            proxy-providers:
              sub: { type: http, url: https://example.com/sub, path: ./sub.yaml }
            """).Yaml);
        var group = Assert.IsType<YamlMappingNode>(Seq(root, "proxy-groups").Children[0]);

        Assert.Equal(["sub"], Strings(Seq(group, "use")));
        Assert.False(group.Children.ContainsKey(new YamlScalarNode("proxies")));
    }

    [Fact]
    public void User_group_with_same_name_is_left_untouched()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies + """

            proxy-groups:
              - { name: PROXY, type: select, proxies: ["second #2", first] }
            """);
        var groups = Seq(Load(config.Yaml), "proxy-groups");

        Assert.False(config.GroupGenerated);
        var group = Assert.Single(groups.Children.Cast<YamlMappingNode>());
        Assert.Equal("select", Str(group, "type"));
    }

    [Theory]
    [InlineData("", "пуст")]
    [InlineData("proxies: []", "нет ни одного прокси")]
    [InlineData("proxies:\n  - { type: ss }", "нет поля name")]
    [InlineData("proxies:\n  - { name: a }\n  - { name: a }", "несколько раз")]
    [InlineData("proxies: [ {name: a}\nfoo", "ошибка YAML")]
    [InlineData("proxies:\n  - { name: a }\nservice: { interval: soon }", "положительным целым")]
    [InlineData("proxies:\n  - { name: a }\ntun: yes", "tun должен быть словарём")]
    [InlineData("proxies:\n  - { name: a }\nservice: { kill-switch: maybe }", "true или false")]
    [InlineData("proxies:\n  - { name: a }\ntun: { inet4-address: [198.18.0.1] }", "длиной префикса")]
    public void Rejects_invalid_config_with_readable_message(string yaml, string expected)
    {
        var ex = Assert.Throws<ConfigException>(() => RuntimeConfigBuilder.Build(yaml));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Kill_switch_is_on_with_lan_allowed_by_default_and_tun_address_is_pinned()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies);

        Assert.True(config.KillSwitch);
        Assert.True(config.AllowLan);
        Assert.Equal(["198.18.0.1/30"], Strings(Seq(Map(Load(config.Yaml), "tun"), "inet4-address")));
        Assert.Equal([IPNetwork.Parse("198.18.0.0/30"), IPNetwork.Parse("fdfe:dcba:9876::/126")], config.TunnelNetworks);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Kill_switch_options_and_custom_tun_addresses_are_read()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies + """

            service: { kill-switch: false, allow-lan: no }
            tun:
              inet4-address: [172.19.0.1/30]
              inet6-address: fdfe:1::1/126
            """);

        Assert.False(config.KillSwitch);
        Assert.False(config.AllowLan);
        Assert.Equal([IPNetwork.Parse("172.19.0.0/30"), IPNetwork.Parse("fdfe:1::/126")], config.TunnelNetworks);
    }

    [Fact]
    public void Warns_when_kill_switch_group_falls_back_to_direct()
    {
        var config = RuntimeConfigBuilder.Build(TwoProxies + """

            proxy-groups:
              - { name: PROXY, type: fallback, proxies: [first, DIRECT] }
            """);

        Assert.Contains("DIRECT", Assert.Single(config.Warnings));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("proxies: [ {name: a}\nfoo", true, true)]
    [InlineData("service: { kill-switch: false }", false, true)]
    [InlineData("service: { allow-lan: false }\nproxies: []", true, false)]
    public void Kill_switch_fallback_fails_closed(string? yaml, bool killSwitch, bool allowLan)
    {
        Assert.Equal((killSwitch, allowLan), RuntimeConfigBuilder.ReadKillSwitchFallback(yaml));
    }

    [Theory]
    [InlineData("198.18.0.1/30", "198.18.0.0/30")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("fdfe:dcba:9876::1/126", "fdfe:dcba:9876::/126")]
    public void Prefix_host_bits_are_cleared(string text, string expected)
    {
        Assert.Equal(IPNetwork.Parse(expected), RuntimeConfigBuilder.ParsePrefix(text, "test"));
    }

    private static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node[key];

    private static YamlSequenceNode Seq(YamlMappingNode node, string key) => (YamlSequenceNode)node[key];

    private static string? Str(YamlMappingNode node, string key) => ((YamlScalarNode)node[key]).Value;

    private static List<string?> Strings(YamlSequenceNode seq) => seq.Children.Cast<YamlScalarNode>().Select(s => s.Value).ToList();
}
