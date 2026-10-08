using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using MihomoService;

const string ServiceName = "MihomoService";

var paths = ServicePaths.FromAppBase();

switch (args)
{
    case ["check"]:
        return await CheckAsync(paths);
    case ["killswitch", "on" or "off" or "remove" or "status"]:
        return KillSwitchCommand(paths, args[1]);
    case []:
        break;
    default:
        Console.WriteLine($"""
            {ServiceName} — служба-обёртка для ядра mihomo (TUN, fallback).

            Использование:
              MihomoService.exe          запуск (службой — через SCM, вручную — в консоли от администратора)
              MihomoService.exe check    проверить {ServicePaths.ConfigFileName} без запуска ядра
              MihomoService.exe killswitch on|off|status|remove
                                         on/off — записать service.kill-switch в {ServicePaths.ConfigFileName} и сразу
                                         применить (от администратора); remove — снять фильтры, не меняя конфиг
                                         (удаление службы, аварийное восстановление сети)
            """);
        return args is ["help"] or ["-h"] or ["--help"] or ["/?"] ? 0 : 2;
}

if (!WindowsServiceHelpers.IsWindowsService() && !IsAdministrator())
{
    Console.Error.WriteLine("Для TUN нужны права администратора: запустите консоль от имени администратора.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
builder.Logging.AddProvider(new FileLoggerProvider(new RotatingLogWriter(paths.ServiceLogFile)));
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<CoreRunner>();
builder.Services.AddSingleton<ConfigValidator>();
builder.Services.AddSingleton<NetworkWaiter>();
builder.Services.AddSingleton<KillSwitchManager>();
builder.Services.AddHostedService<Supervisor>();

await builder.Build().RunAsync();
return Environment.ExitCode;

static async Task<int> CheckAsync(ServicePaths paths)
{
    using var core = new CoreRunner(paths, NullLogger<CoreRunner>.Instance);
    if (!core.CoreExists)
    {
        Console.Error.WriteLine($"Не найдено ядро {paths.CoreExe}");
        return 1;
    }

    // Во временную папку: check работает без прав администратора и не мешает запущенной службе.
    var candidate = Path.Combine(Path.GetTempPath(), $"mihomo-service-check-{Environment.ProcessId}.yaml");
    ValidationResult result;
    try
    {
        result = await new ConfigValidator(paths, core, candidate).ValidateAsync(CancellationToken.None);
    }
    finally
    {
        File.Delete(candidate);
    }
    if (!result.Ok)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }

    var config = result.Config!;
    Console.WriteLine($"OK: {paths.ConfigFile}");
    Console.WriteLine(config.GroupGenerated
        ? $"Fallback-группа {config.GroupName}, порядок: {string.Join(" → ", config.Proxies)}"
        : $"Используется группа {config.GroupName} из config.yaml");
    if (config.Providers.Count > 0)
        Console.WriteLine($"Провайдеры: {string.Join(", ", config.Providers)}");
    Console.WriteLine(config.KillSwitch
        ? $"Kill-switch: включён, локальная сеть {(config.AllowLan ? "разрешена" : "закрыта")}, адреса TUN {string.Join(", ", config.TunnelNetworks)}"
        : "Kill-switch: выключен (service.kill-switch: false)");
    foreach (var warning in config.Warnings)
        Console.WriteLine($"ВНИМАНИЕ: {warning}");
    Console.WriteLine($"Сеть: {NetworkWaiter.FindUsableAdapter() ?? $"нет маршрута по умолчанию — служба подождёт до {NetworkWaiter.MaxWait.TotalSeconds:F0} с"}");
    return 0;
}

static int KillSwitchCommand(ServicePaths paths, string action)
{
    try
    {
        switch (action)
        {
            // Сначала фильтры (это требует прав администратора и может не получиться), потом конфиг.
            case "on":
                var policy = CurrentPolicy(paths);
                KillSwitch.Enable(policy);
                ConfigEditor.SetKillSwitch(paths.ConfigFile, enabled: true);
                Console.WriteLine($"Kill-switch включён (service.kill-switch: true в {ServicePaths.ConfigFileName}): " +
                    $"разрешены ядро и туннель ({string.Join(", ", policy.TunnelNetworks)})" + (policy.AllowLan ? ", локальная сеть" : ""));
                return 0;

            case "off":
                KillSwitch.Disable();
                ConfigEditor.SetKillSwitch(paths.ConfigFile, enabled: false);
                Console.WriteLine($"Kill-switch выключен (service.kill-switch: false в {ServicePaths.ConfigFileName}): без туннеля трафик идёт напрямую.");
                return 0;

            case "remove":
                KillSwitch.Disable();
                Console.WriteLine($"Фильтры kill-switch сняты, {ServicePaths.ConfigFileName} не изменён — служба при запуске включит их снова, если kill-switch включён в конфиге.");
                return 0;

            default:
                var count = Wfp.CountInstalled();
                var configured = RuntimeConfigBuilder.ReadKillSwitchFallback(File.Exists(paths.ConfigFile) ? File.ReadAllText(paths.ConfigFile) : null).KillSwitch;
                Console.WriteLine(count > 0 ? $"Kill-switch активен ({count} фильтров WFP)" : "Kill-switch не активен (фильтров WFP нет)");
                Console.WriteLine($"В {ServicePaths.ConfigFileName}: kill-switch {(configured ? "включён" : "выключен")}");
                return 0;
        }
    }
    catch (Exception ex) when (ex is WfpException or ConfigException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static KillSwitchPolicy CurrentPolicy(ServicePaths paths)
{
    var yaml = File.Exists(paths.ConfigFile) ? File.ReadAllText(paths.ConfigFile) : null;
    try
    {
        return KillSwitchManager.PolicyFor(paths, RuntimeConfigBuilder.Build(yaml ?? ""));
    }
    catch (ConfigException)
    {
        return KillSwitchManager.DefaultPolicy(paths, RuntimeConfigBuilder.ReadKillSwitchFallback(yaml).AllowLan);
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
