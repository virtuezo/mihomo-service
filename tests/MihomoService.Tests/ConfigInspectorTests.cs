namespace MihomoService.Tests;

public sealed class ConfigInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mihomo-inspector-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Mask_hides_secrets_in_any_yaml_style()
    {
        var masked = ConfigInspector.Mask("""
            secret: controller-secret
            authentication: ["user:hunter2"]
            dns:
              nameserver: [https://dns.nextdns.io/abc123]
            proxies:
              - { name: flow, type: ss, server: flow.example, port: 8388, cipher: aes-128-gcm, password: flow-pass }
              - name: block
                type: ssh
                server: block.example
                port: 22
                private-key: |
                  -----BEGIN OPENSSH PRIVATE KEY-----
                  pem-body-line
                private-key-passphrase: phrase-value-xyz
              - name: snell
                type: snell
                server: snell.example
                port: 443
                psk: snell-psk
                reality-opts: { public-key: reality-pub, short-id: abcd }
                ws-opts: { path: /secret-path, headers: { Host: cdn.example } }
            proxy-providers:
              sub:
                type: http
                url: https://user:pw@sub.example/api/subscribe?token=sub-token
                header: { Authorization: [Bearer bearer-token] }
                path: ./sub.yaml
            rules:
              - DOMAIN-SUFFIX,ru,DIRECT
            """);

        foreach (var secret in new[] { "controller-secret", "hunter2", "abc123", "flow-pass", "pem-body-line", "phrase-value-xyz",
                     "snell-psk", "reality-pub", "abcd", "secret-path", "bearer-token", "sub-token", "user:pw", "subscribe" })
            Assert.DoesNotContain(secret, masked);

        foreach (var visible in new[] { "flow.example", "8388", "aes-128-gcm", "block.example", "snell.example",
                     "https://***@sub.example/***", "https://dns.nextdns.io/***", "./sub.yaml", "DOMAIN-SUFFIX,ru,DIRECT" })
            Assert.Contains(visible, masked);
    }

    [Fact]
    public void Mask_applies_proxy_rules_to_merged_templates_and_listener_rules_to_listeners()
    {
        var masked = ConfigInspector.Mask("""
            x-vless: &v { type: vless, uuid: tmpl-uuid, ws-opts: { path: /tmpl-path }, reality-opts: { short-id: tmpl-sid }, grpc-opts: { grpc-service-name: tmpl-grpc } }
            proxies:
              - { <<: *v, name: a, server: a.example, port: 443 }
            listeners:
              - { name: in, type: vless, port: 10000, ws-path: /in-path, grpc-service-name: in-grpc, reality-config: { short-id: [in-sid] } }
            """);

        foreach (var secret in new[] { "tmpl-uuid", "tmpl-path", "tmpl-sid", "tmpl-grpc", "in-path", "in-grpc", "in-sid" })
            Assert.DoesNotContain(secret, masked);
        foreach (var visible in new[] { "a.example", "vless", "10000" })
            Assert.Contains(visible, masked);
    }

    [Fact]
    public void Mask_hides_a_password_containing_at_sign()
    {
        // Go (и mihomo) делят такой адрес по последней «@»: логин user, пароль P@ss.
        var masked = ConfigInspector.Mask("proxy-providers:\n  sub: { type: http, url: 'https://user:P@ss@sub.example/s' }\n");

        Assert.Contains("https://***@sub.example/***", masked);
        Assert.DoesNotContain("ss@", masked);
    }

    [Fact]
    public void Mask_shows_nothing_from_self_referencing_yaml()
    {
        var masked = ConfigInspector.Mask("items: &x [ leaked, *x ]\n");

        Assert.StartsWith("#", masked);
        Assert.DoesNotContain("leaked", masked);
    }

    [Fact]
    public void Mask_shows_nothing_from_unparsable_yaml()
    {
        var masked = ConfigInspector.Mask("proxies: [ { password: leaked\nfoo");

        Assert.StartsWith("#", masked);
        Assert.DoesNotContain("leaked", masked);
    }

    [Fact]
    public void Lists_proxy_servers_of_every_style_and_from_provider_cache()
    {
        var paths = new ServicePaths(_dir);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.ConfigFile, """
            ntp: { enable: true, server: time.apple.com, port: 123 }
            proxies:
              - { name: flow, type: ss, server: flow.example, port: 8388 }
              - server: first-key.example
                name: first-key
                type: socks5
                port: 1080
              - name: hy
                type: hysteria2
                server: hy.example
                port: 443
            proxy-providers:
              sub: { type: http, url: https://sub.example/s, path: ./providers/sub.yaml }
              broken: { type: http, url: https://sub.example/b, path: ./providers/broken.yaml }
            """);
        Directory.CreateDirectory(Path.Combine(paths.DataDir, "providers"));
        File.WriteAllText(Path.Combine(paths.DataDir, "providers", "sub.yaml"),
            "proxies:\n  - { name: p, type: trojan, server: provider.example, port: 2083 }\n");
        File.WriteAllText(Path.Combine(paths.DataDir, "providers", "broken.yaml"), "dm1lc3M6Ly9...");

        var notes = new List<string>();
        var servers = ConfigInspector.ListServers(paths, notes);

        Assert.Equal(
            [("ss", "flow.example", "8388"), ("socks5", "first-key.example", "1080"), ("hysteria2", "hy.example", "443"),
             ("trojan", "provider.example", "2083")],
            servers);
        Assert.Contains(notes, n => n.Contains("broken.yaml"));
    }

    [Fact]
    public void Finds_provider_cache_at_mihomo_default_path_and_through_merges()
    {
        var paths = new ServicePaths(_dir);
        Directory.CreateDirectory(Path.Combine(paths.DataDir, "proxies"));
        File.WriteAllText(paths.ConfigFile, """
            x-provider: &pp { type: file, path: ./merged.yaml }
            proxy-providers:
              nopath: { type: http, url: https://sub.example/s }
              nullpath: { type: http, url: https://sub.example/n, path: ~ }
              viamerge: { <<: *pp }
            """);
        // Без path mihomo хранит провайдера в data\proxies\<md5 от url>.
        File.WriteAllText(Path.Combine(paths.DataDir, "proxies", "2f5eb3564aa8d50b55fad360f450fb14"),
            "proxies:\n  - { name: s, type: ss, server: s.example, port: 1 }\n");
        File.WriteAllText(Path.Combine(paths.DataDir, "proxies", "94948659bbe2a5465b16aa442445013b"),
            "proxies:\n  - { name: n, type: ss, server: n.example, port: 2 }\n");
        File.WriteAllText(Path.Combine(paths.DataDir, "merged.yaml"),
            "proxies:\n  - { name: m, type: ss, server: m.example, port: 3 }\n");

        var notes = new List<string>();
        var servers = ConfigInspector.ListServers(paths, notes);

        Assert.Equal([("ss", "s.example", "1"), ("ss", "n.example", "2"), ("ss", "m.example", "3")], servers);
        Assert.Empty(notes);
    }
}
