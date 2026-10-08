using System.Net;
using System.Net.Sockets;

namespace MihomoService;

/// <param name="CoreExe">
/// Ядро — единственная программа, которой разрешено ходить в сеть напрямую. null, если файла ядра нет:
/// WFP не выдаёт AppId для несуществующего файла, а сеть всё равно нужно закрыть.
/// </param>
/// <param name="AllowLan">Разрешить обмен с локальными сетями (принтеры, NAS, RDP) мимо туннеля.</param>
/// <param name="TunnelNetworks">Адреса TUN-адаптера: трафик с них уже идёт через туннель.</param>
internal sealed record KillSwitchPolicy(string? CoreExe, bool AllowLan, IReadOnlyList<IPNetwork> TunnelNetworks)
{
    public static readonly IPNetwork DefaultTunnelV4 = IPNetwork.Parse("198.18.0.0/30");
    public static readonly IPNetwork DefaultTunnelV6 = IPNetwork.Parse("fdfe:dcba:9876::/126");
}

/// <summary>
/// Kill-switch на WFP: пока фильтры установлены, в сеть выпускается только трафик туннеля.
/// Фильтры постоянные — они действуют с ранней загрузки Windows, при остановленной или упавшей службе,
/// и снимаются только явно (<c>MihomoService.exe killswitch off</c> или удалением службы).
/// </summary>
internal static class KillSwitch
{
    private static readonly IPNetwork[] LinkLocalV4 =
    [
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("255.255.255.255/32"),
    ];

    private static readonly IPNetwork[] LinkLocalV6 =
    [
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    private static readonly IPNetwork[] PrivateV4 =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
    ];

    private static readonly IPNetwork[] PrivateV6 = [IPNetwork.Parse("fc00::/7")];

    /// <summary>
    /// Набор фильтров для каждого из слоёв (исходящие соединения и входящие, IPv4 и IPv6), по убыванию веса:
    /// <list type="number">
    /// <item>15 — разрешить ядро (соединения с прокси-серверами, его DNS);</item>
    /// <item>14 — разрешить loopback;</item>
    /// <item>13 — разрешить трафик с адресов TUN, т.е. уже идущий через туннель;</item>
    /// <item>12 — запретить DNS (порт 53) всем остальным: иначе запросы утекут к DNS роутера/провайдера;</item>
    /// <item>11 — разрешить DHCP, чтобы адаптер мог получить адрес;</item>
    /// <item>10 — разрешить link-local, multicast и broadcast (служебный трафик сегмента);</item>
    /// <item>9 — разрешить частные сети, если включено <see cref="KillSwitchPolicy.AllowLan"/>;</item>
    /// <item>0 — запретить всё остальное.</item>
    /// </list>
    /// </summary>
    public static List<WfpFilter> BuildFilters(KillSwitchPolicy policy)
    {
        var filters = new List<WfpFilter>();
        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            var v6 = layer is WfpLayer.ConnectV6 or WfpLayer.AcceptV6;
            var connect = layer is WfpLayer.ConnectV4 or WfpLayer.ConnectV6;
            var family = v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            var suffix = $"{(connect ? "out" : "in")} {(v6 ? "IPv6" : "IPv4")}";

            if (policy.CoreExe is not null)
                filters.Add(new WfpFilter($"permit core, {suffix}", layer, Permit: true, 15,
                    [new WfpCondition.AppId(policy.CoreExe)]));

            filters.Add(new WfpFilter($"permit loopback, {suffix}", layer, Permit: true, 14,
                [new WfpCondition.Loopback()]));

            var tunnel = policy.TunnelNetworks.Where(n => n.BaseAddress.AddressFamily == family).ToList();
            if (tunnel.Count > 0)
                filters.Add(new WfpFilter($"permit tunnel, {suffix}", layer, Permit: true, 13,
                    tunnel.Select(n => new WfpCondition.LocalAddress(n)).ToList<WfpCondition>()));

            if (connect)
                filters.Add(new WfpFilter($"block DNS, {suffix}", layer, Permit: false, 12,
                    [new WfpCondition.RemotePort(53)]));

            filters.Add(new WfpFilter($"permit DHCP, {suffix}", layer, Permit: true, 11,
                [new WfpCondition.Protocol(ProtocolType.Udp), new WfpCondition.LocalPort(v6 ? (ushort)546 : (ushort)68)]));

            filters.Add(new WfpFilter($"permit link-local, {suffix}", layer, Permit: true, 10,
                (v6 ? LinkLocalV6 : LinkLocalV4).Select(n => new WfpCondition.RemoteAddress(n)).ToList<WfpCondition>()));

            if (policy.AllowLan)
                filters.Add(new WfpFilter($"permit LAN, {suffix}", layer, Permit: true, 9,
                    (v6 ? PrivateV6 : PrivateV4).Select(n => new WfpCondition.RemoteAddress(n)).ToList<WfpCondition>()));

            filters.Add(new WfpFilter($"block all, {suffix}", layer, Permit: false, 0, []));
        }
        return filters;
    }

    public static void Enable(KillSwitchPolicy policy) => Wfp.Replace(BuildFilters(policy));

    public static void Disable() => Wfp.RemoveAll();

    public static bool IsEnabled() => Wfp.CountInstalled() > 0;
}
