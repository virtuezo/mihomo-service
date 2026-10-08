namespace MihomoService;

/// <summary>Приводит WFP-фильтры kill-switch в соответствие с настройками и помнит, что уже применено.</summary>
internal sealed class KillSwitchManager(ServicePaths paths, ILogger<KillSwitchManager> logger)
{
    private bool? _enabled;
    private KillSwitchPolicy? _policy;

    public bool Enabled => _enabled == true;

    /// <summary>
    /// Применяется при старте службы, до ожидания сети и разбора конфига: сеть закрывается сразу,
    /// даже если config.yaml отсутствует или битый.
    /// </summary>
    public void ApplyStartup()
    {
        string? yaml = null;
        try
        {
            yaml = File.ReadAllText(paths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var (enabled, allowLan) = RuntimeConfigBuilder.ReadKillSwitchFallback(yaml);
        Apply(enabled, DefaultPolicy(paths, allowLan));
    }

    public void Apply(RuntimeConfig config) =>
        Apply(config.KillSwitch, new KillSwitchPolicy(paths.CoreExe, config.AllowLan, config.TunnelNetworks));

    public static KillSwitchPolicy DefaultPolicy(ServicePaths paths, bool allowLan) =>
        new(paths.CoreExe, allowLan, [KillSwitchPolicy.DefaultTunnelV4, KillSwitchPolicy.DefaultTunnelV6]);

    private void Apply(bool enabled, KillSwitchPolicy policy)
    {
        if (_enabled == enabled && (!enabled || SamePolicy(_policy!, policy)))
            return;

        try
        {
            if (enabled)
            {
                KillSwitch.Enable(policy);
                logger.LogInformation("Kill-switch включён: в сеть выходит только туннель (ядро {Core}, адреса TUN {Tunnel}); локальная сеть {Lan}",
                    policy.CoreExe, string.Join(", ", policy.TunnelNetworks), policy.AllowLan ? "разрешена" : "закрыта");
            }
            else
            {
                KillSwitch.Disable();
                logger.LogWarning("Kill-switch выключен (service.kill-switch: false): без туннеля трафик пойдёт напрямую");
            }
            _enabled = enabled;
            _policy = policy;
        }
        catch (WfpException ex)
        {
            logger.LogError(ex, "Не удалось {Action} kill-switch", enabled ? "включить" : "выключить");
        }
    }

    private static bool SamePolicy(KillSwitchPolicy a, KillSwitchPolicy b) =>
        string.Equals(a.CoreExe, b.CoreExe, StringComparison.OrdinalIgnoreCase)
        && a.AllowLan == b.AllowLan
        && a.TunnelNetworks.SequenceEqual(b.TunnelNetworks);
}
