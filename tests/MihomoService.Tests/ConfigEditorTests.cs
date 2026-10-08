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
}
