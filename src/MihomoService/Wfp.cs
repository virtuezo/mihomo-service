using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MihomoService;

internal enum WfpLayer { ConnectV4, AcceptV4, ConnectV6, AcceptV6 }

internal abstract record WfpCondition
{
    public sealed record AppId(string Path) : WfpCondition;
    public sealed record Loopback : WfpCondition;
    public sealed record LocalAddress(IPNetwork Network) : WfpCondition;
    public sealed record RemoteAddress(IPNetwork Network) : WfpCondition;
    public sealed record Protocol(ProtocolType Value) : WfpCondition;
    public sealed record LocalPort(ushort Port) : WfpCondition;
    public sealed record RemotePort(ushort Port) : WfpCondition;
}

/// <summary>
/// Описание одного WFP-фильтра. Условия с одинаковым полем объединяются по ИЛИ, с разными — по И
/// (так WFP трактует набор условий фильтра). Внутри подслоя первым срабатывает фильтр с большим весом.
/// </summary>
internal sealed record WfpFilter(string Name, WfpLayer Layer, bool Permit, byte Weight, IReadOnlyList<WfpCondition> Conditions);

internal sealed class WfpException(string operation, uint code)
    : Exception($"WFP {operation}: ошибка 0x{code:X8}{(code == AccessDenied ? " (нужны права администратора)" : "")}")
{
    private const uint AccessDenied = 5;
    public uint Code { get; } = code;
}

/// <summary>
/// Минимальная обёртка над Windows Filtering Platform: постоянный подслой и набор постоянных фильтров
/// с детерминированными ключами, которые можно целиком заменить или удалить.
/// Постоянные фильтры хранятся BFE и действуют после перезагрузки и без запущенной службы.
/// Раскладка структур сверена с fwpmu.h (Windows SDK 10.0.26100), только x64.
/// </summary>
internal static unsafe partial class Wfp
{
    public const int MaxFilters = 64;

    private static readonly Guid SubLayerKey = new("6c1e0d2a-8f3b-4f6e-9a57-3d2b7e4c9a10");
    // Ключ фильтра = база с номером фильтра в последних двух байтах.
    private static readonly Guid FilterKeyBase = new("6c1e0d2a-8f3b-4f6e-9a57-3d2b7e4c0000");

    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    private const uint RpcCAuthnWinnt = 10;
    private const uint ActionBlock = 0x1001;
    private const uint ActionPermit = 0x1002;
    private const uint FilterFlagPersistent = 0x1;
    private const uint SubLayerFlagPersistent = 0x1;
    private const uint ConditionFlagIsLoopback = 0x1;

    private const uint TypeUInt8 = 1, TypeUInt16 = 2, TypeUInt32 = 3, TypeByteBlob = 12, TypeV4AddrMask = 0x100, TypeV6AddrMask = 0x101;
    private const uint MatchEqual = 0, MatchFlagsAllSet = 6;

    private const uint ErrorFilterNotFound = 0x80320003;
    private const uint ErrorSubLayerNotFound = 0x80320007;
    private const uint ErrorAlreadyExists = 0x80320009;

    /// <summary>Атомарно заменяет все фильтры службы на <paramref name="filters"/>.</summary>
    public static void Replace(IReadOnlyList<WfpFilter> filters)
    {
        if (filters.Count > MaxFilters)
            throw new ArgumentException($"Слишком много фильтров: {filters.Count} > {MaxFilters}");

        InTransaction(engine =>
        {
            DeleteFilters(engine);
            AddSubLayer(engine);
            for (var i = 0; i < filters.Count; i++)
                AddFilter(engine, FilterKey(i), filters[i]);
        });
    }

    /// <summary>Удаляет все фильтры и подслой службы. Безопасно вызывать, если их нет.</summary>
    public static void RemoveAll() => InTransaction(engine =>
    {
        DeleteFilters(engine);
        var code = FwpmSubLayerDeleteByKey0(engine, SubLayerKey);
        if (code is not 0 and not ErrorSubLayerNotFound)
            throw new WfpException("удаление подслоя", code);
    });

    /// <returns>Число установленных фильтров службы (0 — kill-switch выключен).</returns>
    public static int CountInstalled()
    {
        var engine = OpenEngine();
        try
        {
            var count = 0;
            for (var i = 0; i < MaxFilters; i++)
            {
                var code = FwpmFilterGetByKey0(engine, FilterKey(i), out var filter);
                if (code == 0)
                {
                    count++;
                    FwpmFreeMemory0(ref filter);
                }
                else if (code != ErrorFilterNotFound)
                {
                    throw new WfpException($"чтение фильтра #{i}", code);
                }
            }
            return count;
        }
        finally
        {
            FwpmEngineClose0(engine);
        }
    }

    private static Guid FilterKey(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        FilterKeyBase.TryWriteBytes(bytes);
        bytes[14] = (byte)(index >> 8);
        bytes[15] = (byte)index;
        return new Guid(bytes);
    }

    private static void InTransaction(Action<nint> action)
    {
        var engine = OpenEngine();
        try
        {
            Check(FwpmTransactionBegin0(engine, 0), "начало транзакции");
            try
            {
                action(engine);
                Check(FwpmTransactionCommit0(engine), "фиксация транзакции");
            }
            catch
            {
                FwpmTransactionAbort0(engine);
                throw;
            }
        }
        finally
        {
            FwpmEngineClose0(engine);
        }
    }

    private static nint OpenEngine()
    {
        Check(FwpmEngineOpen0(0, RpcCAuthnWinnt, 0, 0, out var engine), "открытие движка");
        return engine;
    }

    private static void DeleteFilters(nint engine)
    {
        for (var i = 0; i < MaxFilters; i++)
        {
            var code = FwpmFilterDeleteByKey0(engine, FilterKey(i));
            if (code is not 0 and not ErrorFilterNotFound)
                throw new WfpException($"удаление фильтра #{i}", code);
        }
    }

    private static void AddSubLayer(nint engine)
    {
        fixed (char* name = "MihomoService kill-switch")
        fixed (char* description = "Blocks traffic outside the mihomo tunnel")
        {
            var subLayer = new FwpmSubLayer0
            {
                SubLayerKey = SubLayerKey,
                DisplayData = new FwpmDisplayData0 { Name = name, Description = description },
                Flags = SubLayerFlagPersistent,
                Weight = 0xFFFF,
            };
            var code = FwpmSubLayerAdd0(engine, &subLayer, 0);
            if (code is not 0 and not ErrorAlreadyExists)
                throw new WfpException("создание подслоя", code);
        }
    }

    private static void AddFilter(nint engine, Guid key, WfpFilter spec)
    {
        // Всё, на что ссылаются условия, живёт в неуправляемой памяти до вызова FwpmFilterAdd0 (он копирует данные).
        var allocations = new List<nint>();
        var appIds = new List<nint>();
        try
        {
            var conditions = (FwpmFilterCondition0*)Alloc(allocations, sizeof(FwpmFilterCondition0) * Math.Max(1, spec.Conditions.Count));
            for (var i = 0; i < spec.Conditions.Count; i++)
                conditions[i] = BuildCondition(spec.Conditions[i], allocations, appIds);

            fixed (char* name = "MihomoService kill-switch: " + spec.Name)
            {
                var filter = new FwpmFilter0
                {
                    FilterKey = key,
                    DisplayData = new FwpmDisplayData0 { Name = name },
                    Flags = FilterFlagPersistent,
                    LayerKey = LayerKey(spec.Layer),
                    SubLayerKey = SubLayerKey,
                    Weight = new FwpValue0 { Type = TypeUInt8, Value = spec.Weight },
                    NumFilterConditions = (uint)spec.Conditions.Count,
                    FilterCondition = spec.Conditions.Count > 0 ? conditions : null,
                    Action = new FwpmAction0 { Type = spec.Permit ? ActionPermit : ActionBlock },
                };
                Check(FwpmFilterAdd0(engine, &filter, 0, out _), $"добавление фильтра «{spec.Name}»");
            }
        }
        finally
        {
            foreach (var appId in appIds)
            {
                var p = appId;
                FwpmFreeMemory0(ref p);
            }
            foreach (var p in allocations)
                NativeMemory.Free((void*)p);
        }
    }

    private static FwpmFilterCondition0 BuildCondition(WfpCondition condition, List<nint> allocations, List<nint> appIds)
    {
        switch (condition)
        {
            case WfpCondition.AppId(var path):
                Check(FwpmGetAppIdFromFileName0(path, out var blob), $"получение AppId для {path}");
                appIds.Add(blob);
                return Condition(ConditionAppId, MatchEqual, TypeByteBlob, (ulong)blob);

            case WfpCondition.Loopback:
                return Condition(ConditionFlags, MatchFlagsAllSet, TypeUInt32, ConditionFlagIsLoopback);

            case WfpCondition.LocalAddress(var network):
                return AddressCondition(ConditionLocalAddress, network, allocations);

            case WfpCondition.RemoteAddress(var network):
                return AddressCondition(ConditionRemoteAddress, network, allocations);

            case WfpCondition.Protocol(var protocol):
                return Condition(ConditionProtocol, MatchEqual, TypeUInt8, (byte)protocol);

            case WfpCondition.LocalPort(var port):
                return Condition(ConditionLocalPort, MatchEqual, TypeUInt16, port);

            case WfpCondition.RemotePort(var port):
                return Condition(ConditionRemotePort, MatchEqual, TypeUInt16, port);

            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition, null);
        }
    }

    private static FwpmFilterCondition0 AddressCondition(Guid field, IPNetwork network, List<nint> allocations)
    {
        var address = network.BaseAddress;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var mask = (FwpV4AddrAndMask*)Alloc(allocations, sizeof(FwpV4AddrAndMask));
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            mask->Addr = BinaryPrimitives.ReadUInt32BigEndian(bytes); // WFP ждёт адрес в порядке байт хоста
            mask->Mask = network.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - network.PrefixLength);
            return Condition(field, MatchEqual, TypeV4AddrMask, (ulong)mask);
        }
        else
        {
            var mask = (FwpV6AddrAndMask*)Alloc(allocations, sizeof(FwpV6AddrAndMask));
            address.TryWriteBytes(new Span<byte>(mask->Addr, 16), out _);
            mask->PrefixLength = (byte)network.PrefixLength;
            return Condition(field, MatchEqual, TypeV6AddrMask, (ulong)mask);
        }
    }

    private static FwpmFilterCondition0 Condition(Guid field, uint match, uint type, ulong value) => new()
    {
        FieldKey = field,
        MatchType = match,
        ConditionValue = new FwpValue0 { Type = type, Value = value },
    };

    private static nint Alloc(List<nint> allocations, int size)
    {
        var p = (nint)NativeMemory.AllocZeroed((nuint)size);
        allocations.Add(p);
        return p;
    }

    private static Guid LayerKey(WfpLayer layer) => layer switch
    {
        WfpLayer.ConnectV4 => LayerConnectV4,
        WfpLayer.AcceptV4 => LayerAcceptV4,
        WfpLayer.ConnectV6 => LayerConnectV6,
        WfpLayer.AcceptV6 => LayerAcceptV6,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    private static void Check(uint code, string operation)
    {
        if (code != 0)
            throw new WfpException(operation, code);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public char* Name;
        public char* Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint Size;
        public byte* Data;
    }

    /// <summary>FWP_VALUE0 и FWP_CONDITION_VALUE0: тип + объединение (число или указатель), 16 байт.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpValue0
    {
        public uint Type;
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddrAndMask
    {
        public uint Addr;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV6AddrAndMask
    {
        public fixed byte Addr[16];
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition0
    {
        public Guid FieldKey;
        public uint MatchType;
        public FwpValue0 ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmAction0
    {
        public uint Type;
        public Guid FilterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid SubLayerKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public Guid* ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct FwpmFilter0
    {
        [FieldOffset(0)] public Guid FilterKey;
        [FieldOffset(16)] public FwpmDisplayData0 DisplayData;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public Guid* ProviderKey;
        [FieldOffset(48)] public FwpByteBlob ProviderData;
        [FieldOffset(64)] public Guid LayerKey;
        [FieldOffset(80)] public Guid SubLayerKey;
        [FieldOffset(96)] public FwpValue0 Weight;
        [FieldOffset(112)] public uint NumFilterConditions;
        [FieldOffset(120)] public FwpmFilterCondition0* FilterCondition;
        [FieldOffset(128)] public FwpmAction0 Action;
        [FieldOffset(152)] public ulong RawContext;
        [FieldOffset(168)] public Guid* Reserved;
        [FieldOffset(176)] public ulong FilterId;
        [FieldOffset(184)] public FwpValue0 EffectiveWeight;
    }

    // Проверяется тестом: размеры и смещения должны совпадать с fwpmu.h.
    internal static (int Filter, int Condition, int SubLayer, int Value, int Action, int V4, int V6) StructSizes() =>
        (sizeof(FwpmFilter0), sizeof(FwpmFilterCondition0), sizeof(FwpmSubLayer0), sizeof(FwpValue0),
         sizeof(FwpmAction0), sizeof(FwpV4AddrAndMask), sizeof(FwpV6AddrAndMask));

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineOpen0(nint serverName, uint authnService, nint authIdentity, nint session, out nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineClose0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionBegin0(nint engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionCommit0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionAbort0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerAdd0(nint engineHandle, FwpmSubLayer0* subLayer, nint sd);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerDeleteByKey0(nint engineHandle, in Guid key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterAdd0(nint engineHandle, FwpmFilter0* filter, nint sd, out ulong id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterDeleteByKey0(nint engineHandle, in Guid key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterGetByKey0(nint engineHandle, in Guid key, out nint filter);

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string fileName, out nint appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial void FwpmFreeMemory0(ref nint p);
}
