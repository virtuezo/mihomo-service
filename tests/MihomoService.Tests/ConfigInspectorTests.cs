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
}
