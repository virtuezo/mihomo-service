using System.Net.NetworkInformation;

namespace MihomoService.Tests;

public class NetworkWaiterTests
{
    [Fact]
    public void Default_routes_point_to_existing_interfaces()
    {
        // Проверяет разбор MIB_IPFORWARD_ROW2: при неверных смещениях вместо индексов были бы случайные числа.
        var known = NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => n.GetIPProperties())
            .SelectMany(p => new[] { TryIndex(() => p.GetIPv4Properties()?.Index), TryIndex(() => p.GetIPv6Properties()?.Index) })
            .OfType<int>()
            .ToHashSet();

        Assert.Subset(known, NetworkWaiter.GetDefaultRouteInterfaceIndexes());
    }

    private static int? TryIndex(Func<int?> read)
    {
        try { return read(); }
        catch (NetworkInformationException) { return null; }
    }
}
