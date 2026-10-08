using System.Diagnostics;
using System.Threading.Channels;

namespace MihomoService;

/// <summary>
/// Основной цикл службы: держит ядро запущенным, перезапускает его при падении (с нарастающей паузой)
/// и при изменении config.yaml — но только если новый конфиг прошёл проверку.
/// </summary>
internal sealed class Supervisor(
    ServicePaths paths,
    CoreRunner core,
    ConfigValidator validator,
    NetworkWaiter network,
    KillSwitchManager killSwitch,
    ILogger<Supervisor> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StableRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    // Конфиг мог быть отклонён из-за временной причины (не скачалась geo-база), поэтому проверка повторяется.
    private static readonly TimeSpan InvalidConfigRetry = TimeSpan.FromMinutes(2);

    private readonly Channel<bool> _configChanged =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private enum CoreExit { Crashed, Restart, Stopping }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "Служба остановлена из-за ошибки");
            // Ненулевой код — сигнал SCM применить действия восстановления (перезапуск службы).
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            if (killSwitch.Enabled)
                logger.LogInformation("Kill-switch остаётся включённым: пока туннель не поднят, интернет закрыт");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        paths.EnsureDirectories();
        logger.LogInformation("Каталог службы: {Dir}", paths.BaseDir);
        if (!core.CoreExists)
            throw new FileNotFoundException($"Не найдено ядро {paths.CoreExe}");
        logger.LogInformation("Ядро: {Version}", await core.GetVersionAsync(stoppingToken));

        // До всего остального: пока туннеля нет, в сеть выходит только ядро.
        killSwitch.ApplyStartup();

        using var watcher = WatchConfig();
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Сначала сеть: проверка конфига может скачивать geo-базы, а без сети она бы провалилась.
            await network.WaitAsync(stoppingToken);

            var config = await TryApplyConfigAsync(stoppingToken);
            if (config is null)
            {
                logger.LogWarning("Ядро не запущено: исправьте {File}, служба подхватит изменения автоматически " +
                    "(повторная проверка — через {Minutes:F0} мин)", paths.ConfigFile, InvalidConfigRetry.TotalMinutes);
                await WaitForConfigChangeAsync(stoppingToken, InvalidConfigRetry);
                continue;
            }

            var startedAt = Stopwatch.GetTimestamp();
            using var process = core.Start(paths.RuntimeConfig);
            var exit = await SuperviseAsync(process, config.Yaml, stoppingToken);

            if (exit == CoreExit.Stopping)
                break;
            if (exit == CoreExit.Restart)
            {
                backoff = MinBackoff;
                continue;
            }

            if (Stopwatch.GetElapsedTime(startedAt) > StableRun)
                backoff = MinBackoff;
            logger.LogError("mihomo завершился с кодом {Code} (подробности в {Log}), перезапуск через {Delay} с",
                process.ExitCode, paths.CoreLogFile, backoff.TotalSeconds);
            await Task.Delay(backoff, stoppingToken);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task<CoreExit> SuperviseAsync(Process process, string runningYaml, CancellationToken stoppingToken)
    {
        var exited = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (true)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var changed = WaitForConfigChangeAsync(waitCts.Token);
                if (await Task.WhenAny(exited, changed) == exited)
                {
                    // Отменяем ожидание, чтобы оно не «съело» следующее уведомление об изменении конфига.
                    await waitCts.CancelAsync();
                    await changed.ContinueWith(static _ => { }, TaskScheduler.Default);
                    return CoreExit.Crashed;
                }

                try
                {
                    await changed;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return CoreExit.Stopping;
                }

                logger.LogInformation("{File} изменён, проверяю новую конфигурацию", ServicePaths.ConfigFileName);
                var applied = await TryApplyConfigAsync(stoppingToken);
                if (applied is null)
                {
                    logger.LogWarning("Новая конфигурация отклонена, ядро продолжает работать на прежней");
                    continue;
                }
                // Изменились только настройки службы (например, kill-switch) — ядро перезапускать незачем.
                if (applied.Yaml == runningYaml)
                {
                    logger.LogInformation("Конфигурация ядра не изменилась, перезапуск не нужен");
                    continue;
                }
                return CoreExit.Restart;
            }
        }
        finally
        {
            await core.StopAsync(process);
        }
    }

    /// <returns>Применённая конфигурация или null, если config.yaml отклонён.</returns>
    private async Task<RuntimeConfig?> TryApplyConfigAsync(CancellationToken ct)
    {
        var result = await validator.ValidateAsync(ct);
        if (!result.Ok)
        {
            logger.LogError("{Error}", result.Error);
            return null;
        }

        validator.Commit();
        killSwitch.Apply(result.Config!);
        foreach (var warning in result.Config!.Warnings)
            logger.LogWarning("{Warning}", warning);
        var config = result.Config!;
        logger.LogInformation(
            config.GroupGenerated
                ? "Конфигурация применена: fallback-группа {Group}, прокси: {Proxies}; провайдеры: {Providers}"
                : "Конфигурация применена: используется группа {Group} из config.yaml; прокси: {Proxies}; провайдеры: {Providers}",
            config.GroupName,
            config.Proxies.Count > 0 ? string.Join(" → ", config.Proxies) : "—",
            config.Providers.Count > 0 ? string.Join(", ", config.Providers) : "—");
        return config;
    }

    private async Task WaitForConfigChangeAsync(CancellationToken ct, TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await WaitForConfigChangeAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Таймаут — просто перепроверяем конфиг.
        }
    }

    private async Task WaitForConfigChangeAsync(CancellationToken ct)
    {
        await _configChanged.Reader.ReadAsync(ct);
        // Редакторы сохраняют файл в несколько приёмов — ждём, пока событие утихнет.
        await Task.Delay(Debounce, ct);
        _configChanged.Reader.TryRead(out _);
    }

    private FileSystemWatcher WatchConfig()
    {
        var watcher = new FileSystemWatcher(paths.BaseDir, ServicePaths.ConfigFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => _configChanged.Writer.TryWrite(true);
        watcher.Created += (_, _) => _configChanged.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => _configChanged.Writer.TryWrite(true);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
