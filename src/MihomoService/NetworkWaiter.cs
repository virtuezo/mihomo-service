using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MihomoService;

/// <summary>
/// Ждёт готовности сети перед запуском ядра.
/// <para>
/// Сеть считается готовой, когда в таблице маршрутов Windows есть маршрут по умолчанию (0.0.0.0/0 или ::/0)
/// через поднятый адаптер с рабочим адресом. Это не зависит от типа подключения: Ethernet, Wi-Fi, PPPoE
/// (у него шлюз 0.0.0.0), модемы, IPv4 и IPv6. Виртуальные Wintun-адаптеры (TUN самого mihomo, WireGuard,
/// sing-box) не учитываются — они сами работают поверх реальной сети.
/// </para>
/// <para>
/// Ожидание ограничено <see cref="MaxWait"/>: если сеть так и не определилась (экзотический адаптер или машина
/// действительно офлайн), ядро всё равно запускается — с auto-detect-interface оно само подхватит сеть позже.
/// </para>
/// </summary>
internal sealed partial class NetworkWaiter(ILogger<NetworkWaiter> logger)
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    // IF_TYPE_PROP_VIRTUAL — так Windows помечает Wintun-адаптеры.
    private const NetworkInterfaceType PropVirtual = (NetworkInterfaceType)53;

    public async Task WaitAsync(CancellationToken ct)
    {
        if (FindUsableAdapter() is { } ready)
        {
            logger.LogInformation("Сеть доступна: {Adapter}", ready);
            return;
        }

        logger.LogWarning("Нет маршрута по умолчанию через сетевой адаптер — жду сеть до {Seconds:F0} с", MaxWait.TotalSeconds);
        var startedAt = Stopwatch.GetTimestamp();

        using var changed = new SemaphoreSlim(0);
        NetworkAddressChangedEventHandler onChange = (_, _) => changed.Release();
        NetworkChange.NetworkAddressChanged += onChange;
        try
        {
            while (Stopwatch.GetElapsedTime(startedAt) < MaxWait)
            {
                // Событие ускоряет реакцию, опрос страхует от пропущенных уведомлений.
                await changed.WaitAsync(PollInterval, ct);

                if (FindUsableAdapter() is { } adapter)
                {
                    logger.LogInformation("Сеть доступна через {Seconds:F0} с: {Adapter}",
                        Stopwatch.GetElapsedTime(startedAt).TotalSeconds, adapter);
                    return;
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onChange;
        }

        logger.LogWarning("Сеть не появилась за {Seconds:F0} с — запускаю ядро без неё, оно подхватит сеть позже",
            MaxWait.TotalSeconds);
    }

    /// <returns>Описание адаптера с маршрутом по умолчанию или null.</returns>
    internal static string? FindUsableAdapter()
    {
        var defaultRouteInterfaces = GetDefaultRouteInterfaceIndexes();
        if (defaultRouteInterfaces.Count == 0)
            return null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or PropVirtual)
                continue;

            var props = nic.GetIPProperties();
            if (!defaultRouteInterfaces.Overlaps(GetIndexes(props)))
                continue;

            var address = props.UnicastAddresses.Select(u => u.Address).Where(IsUsable)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
            if (address is not null)
                return $"{nic.Name}, {nic.Description} ({address})";
        }
        return null;
    }

    private static IEnumerable<int> GetIndexes(IPInterfaceProperties props)
    {
        // У адаптера с отключённым IPv4 или IPv6 соответствующие свойства недоступны.
        int? v4 = null, v6 = null;
        try { v4 = props.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { }
        try { v6 = props.GetIPv6Properties()?.Index; } catch (NetworkInformationException) { }
        if (v4 is { } i4) yield return i4;
        if (v6 is { } i6) yield return i6;
    }

    private static bool IsUsable(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.Any)
            && address.GetAddressBytes() is not [169, 254, ..], // APIPA: DHCP не ответил
        AddressFamily.InterNetworkV6 => !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.IPv6Any)
            && !address.IsIPv6LinkLocal,
        _ => false,
    };

    /// <summary>Индексы интерфейсов, через которые идут маршруты по умолчанию (длина префикса 0).</summary>
    internal static HashSet<int> GetDefaultRouteInterfaceIndexes()
    {
        var result = new HashSet<int>();
        if (GetIpForwardTable2(AfUnspec, out var table) != 0)
            return result;
        try
        {
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + TableHeaderSize + i * RowSize;
                if (Marshal.ReadByte(row, PrefixLengthOffset) == 0)
                    result.Add(Marshal.ReadInt32(row, InterfaceIndexOffset));
            }
        }
        finally
        {
            FreeMibTable(table);
        }
        return result;
    }

    // MIB_IPFORWARD_TABLE2 { ULONG NumEntries; MIB_IPFORWARD_ROW2 Table[]; } — строки выровнены по 8 байт.
    // MIB_IPFORWARD_ROW2 (104 байта): InterfaceLuid @0, InterfaceIndex @8,
    // DestinationPrefix { SOCKADDR_INET Prefix @12 (28 байт); UINT8 PrefixLength @40 }, ...
    private const ushort AfUnspec = 0;
    private const int TableHeaderSize = 8;
    private const int RowSize = 104;
    private const int InterfaceIndexOffset = 8;
    private const int PrefixLengthOffset = 40;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpForwardTable2(ushort family, out nint table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint memory);
}
