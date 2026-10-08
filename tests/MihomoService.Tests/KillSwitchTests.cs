using System.Net;

namespace MihomoService.Tests;

public class KillSwitchTests
{
    private static readonly KillSwitchPolicy Policy = new(
        @"C:\Program Files\MihomoService\mihomo.exe",
        AllowLan: true,
        [IPNetwork.Parse("198.18.0.0/30"), IPNetwork.Parse("fdfe:dcba:9876::/126")]);

    [Fact]
    public void Native_struct_layout_matches_windows_sdk()
    {
        // Значения из fwpmu.h (Windows SDK 10.0.26100, x64), сняты C-программой с sizeof/offsetof.
        Assert.Equal((200, 40, 72, 16, 20, 8, 17), Wfp.StructSizes());
    }

    [Fact]
    public void Every_layer_ends_with_block_all_and_starts_with_core_permit()
    {
        var filters = KillSwitch.BuildFilters(Policy);

        Assert.True(filters.Count <= Wfp.MaxFilters);
        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            var inLayer = filters.Where(f => f.Layer == layer).OrderByDescending(f => f.Weight).ToList();
            var first = inLayer[0];
            Assert.True(first.Permit);
            Assert.Equal(Policy.CoreExe, Assert.IsType<WfpCondition.AppId>(Assert.Single(first.Conditions)).Path);

            var last = inLayer[^1];
            Assert.False(last.Permit);
            Assert.Equal(0, last.Weight);
            Assert.Empty(last.Conditions);
        }
    }

    [Fact]
    public void Dns_is_blocked_on_outbound_layers_above_lan_permit()
    {
        var filters = KillSwitch.BuildFilters(Policy);

        foreach (var layer in new[] { WfpLayer.ConnectV4, WfpLayer.ConnectV6 })
        {
            var dns = Assert.Single(filters, f => f.Layer == layer && f.Conditions.Any(c => c is WfpCondition.RemotePort { Port: 53 }));
            Assert.False(dns.Permit);
            var lan = Assert.Single(filters, f => f.Layer == layer && f.Name.StartsWith("permit LAN"));
            Assert.True(dns.Weight > lan.Weight);
            var tunnel = Assert.Single(filters, f => f.Layer == layer && f.Name.StartsWith("permit tunnel"));
            Assert.True(tunnel.Weight > dns.Weight); // DNS через туннель (dns-hijack) должен проходить
        }
        Assert.DoesNotContain(filters, f => f.Layer is WfpLayer.AcceptV4 or WfpLayer.AcceptV6 && f.Name.StartsWith("block DNS"));
    }

    [Fact]
    public void Tunnel_permit_uses_only_addresses_of_the_layer_family()
    {
        var filters = KillSwitch.BuildFilters(Policy);

        var v4 = Assert.Single(filters, f => f.Layer == WfpLayer.ConnectV4 && f.Name.StartsWith("permit tunnel"));
        Assert.Equal(IPNetwork.Parse("198.18.0.0/30"), Assert.IsType<WfpCondition.LocalAddress>(Assert.Single(v4.Conditions)).Network);
        var v6 = Assert.Single(filters, f => f.Layer == WfpLayer.ConnectV6 && f.Name.StartsWith("permit tunnel"));
        Assert.Equal(IPNetwork.Parse("fdfe:dcba:9876::/126"), Assert.IsType<WfpCondition.LocalAddress>(Assert.Single(v6.Conditions)).Network);
    }

    [Fact]
    public void Lan_permit_is_absent_when_lan_is_not_allowed()
    {
        var filters = KillSwitch.BuildFilters(Policy with { AllowLan = false });

        Assert.DoesNotContain(filters, f => f.Name.StartsWith("permit LAN"));
        // Служебный трафик сегмента и DHCP нужны всегда, иначе адаптер не получит адрес.
        Assert.Equal(4, filters.Count(f => f.Name.StartsWith("permit DHCP")));
        Assert.Equal(4, filters.Count(f => f.Name.StartsWith("permit link-local")));
    }
}
