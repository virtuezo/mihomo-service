namespace MihomoService.Tests;

public class ConfigEditorTests
{
    [Fact]
    public void Replaces_existing_value_keeping_indent_and_comment()
    {
        var text = "service:\n    group-name: PROXY\n    kill-switch: true   # см. README\nproxies: []\n";

        var result = ConfigEditor.SetServiceValue(text, "kill-switch", "false");

        Assert.Equal("service:\n    group-name: PROXY\n    kill-switch: false   # см. README\nproxies: []\n", result);
    }

    [Fact]
    public void Adds_key_to_existing_section_with_its_indent()
    {
        var text = "proxies: []\nservice:   # настройки\n    interval: 60\n\n# хвост\nrules: []\n";

        var result = ConfigEditor.SetServiceValue(text, "kill-switch", "false");

        Assert.Equal("proxies: []\nservice:   # настройки\n    kill-switch: false\n    interval: 60\n\n# хвост\nrules: []\n", result);
        Assert.False(RuntimeConfigBuilder.ReadKillSwitchFallback(result).KillSwitch);
    }

    [Fact]
    public void Appends_section_and_preserves_crlf()
    {
        var result = ConfigEditor.SetServiceValue("mode: rule\r\nproxies: []\r\n", "kill-switch", "false");

        Assert.Equal("mode: rule\r\nproxies: []\r\n\r\nservice:\r\n  kill-switch: false\r\n", result);
        Assert.False(RuntimeConfigBuilder.ReadKillSwitchFallback(result).KillSwitch);
    }

    [Fact]
    public void Creates_section_in_empty_file()
    {
        Assert.Equal("service:\n  kill-switch: true\n", ConfigEditor.SetServiceValue("", "kill-switch", "true"));
    }

    [Fact]
    public void Does_not_touch_same_key_in_other_sections()
    {
        var text = "tun:\n  kill-switch: whatever\nservice:\n  interval: 60\n";

        var result = ConfigEditor.SetServiceValue(text, "kill-switch", "false");

        Assert.Equal("tun:\n  kill-switch: whatever\nservice:\n  kill-switch: false\n  interval: 60\n", result);
    }

    [Fact]
    public void Refuses_flow_style_section()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigEditor.SetServiceValue("service: { interval: 60 }\n", "kill-switch", "false"));
        Assert.Contains("вручную", ex.Message);
    }

    [Theory]
    [InlineData("mode: rule\nservice:\n  kill-switch: true\r\nproxies: []\r\n", "mode: rule\nservice:\n  kill-switch: false\r\nproxies: []\r\n")]
    [InlineData("service:\r\n  kill-switch: true\n  allow-lan: false\r\n", "service:\r\n  kill-switch: false\n  allow-lan: false\r\n")]
    [InlineData("service:\r\n  kill-switch: true\ntun:\n  stack: mixed\r\n", "service:\r\n  kill-switch: false\ntun:\n  stack: mixed\r\n")]
    public void Mixed_line_endings_change_only_the_value(string text, string expected)
    {
        Assert.Equal(expected, ConfigEditor.SetServiceValue(text, "kill-switch", "false"));
    }

    [Fact]
    public void Inserts_key_after_header_without_trailing_newline()
    {
        Assert.Equal("proxies: []\nservice:\n  kill-switch: false\n", ConfigEditor.SetServiceValue("proxies: []\nservice:", "kill-switch", "false"));
    }

    [Theory]
    [InlineData("service:\n  group-name: X\n  extra:\n    foo: 1\n")]
    [InlineData("service:\n  test-url: >-\n    https://example.com/generate_204\n")]
    public void Inserted_key_takes_the_indent_of_the_section_not_of_nested_lines(string text)
    {
        var result = ConfigEditor.SetServiceValue(text, "kill-switch", "false");

        Assert.StartsWith("service:\n  kill-switch: false\n", result);
        Assert.False(RuntimeConfigBuilder.ReadKillSwitchFallback(result).KillSwitch);
    }

    [Fact]
    public void Nested_key_with_the_same_name_is_not_the_section_key()
    {
        var text = "service:\n  extra:\n    kill-switch: whatever\n";

        var result = ConfigEditor.SetServiceValue(text, "kill-switch", "false");

        Assert.Equal("service:\n  kill-switch: false\n  extra:\n    kill-switch: whatever\n", result);
    }
}

public sealed class ConfigEditorFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mihomo-editor-test").FullName;
    private string ConfigFile => Path.Combine(_dir, "config.yaml");

    public void Dispose()
    {
        if (File.Exists(ConfigFile))
            File.SetAttributes(ConfigFile, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Keeps_bom_when_the_file_had_it()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(ConfigFile, [.. bom, .. "# конфиг\nservice:\n  kill-switch: true\n"u8.ToArray()]);

        ConfigEditor.SetKillSwitch(ConfigFile, enabled: false);

        byte[] expected = [.. bom, .. "# конфиг\nservice:\n  kill-switch: false\n"u8.ToArray()];
        Assert.Equal(expected, File.ReadAllBytes(ConfigFile));
    }

    [Fact]
    public void Refuses_non_utf8_file_without_touching_it()
    {
        byte[] cp1251 = [(byte)'#', (byte)' ', 0xEA, 0xEE, 0xED, 0xF4, (byte)'\n', .. "service:\n  kill-switch: true\n"u8.ToArray()];
        File.WriteAllBytes(ConfigFile, cp1251);

        var ex = Assert.Throws<ConfigException>(() => ConfigEditor.SetKillSwitch(ConfigFile, enabled: false));

        Assert.Contains("UTF-8", ex.Message);
        Assert.Equal(cp1251, File.ReadAllBytes(ConfigFile));
    }

    [Fact]
    public void Keeps_file_permissions()
    {
        // install-service закрывает config.yaml от обычных пользователей — правка не должна это сбрасывать.
        File.WriteAllText(ConfigFile, "service:\n  kill-switch: true\n");
        var file = new FileInfo(ConfigFile);
        var security = file.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        file.SetAccessControl(security);

        ConfigEditor.SetKillSwitch(ConfigFile, enabled: false);

        Assert.True(new FileInfo(ConfigFile).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal("service:\n  kill-switch: false\n", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void Failed_write_names_the_file_and_leaves_no_temp_file()
    {
        File.WriteAllText(ConfigFile, "service:\n  kill-switch: true\n");
        File.SetAttributes(ConfigFile, FileAttributes.ReadOnly);

        var ex = Assert.Throws<IOException>(() => ConfigEditor.SetKillSwitch(ConfigFile, enabled: false));

        Assert.Contains(ConfigFile, ex.Message);
        Assert.False(File.Exists(ConfigFile + ".tmp"));
        Assert.Equal("service:\n  kill-switch: true\n", File.ReadAllText(ConfigFile));
    }
}
