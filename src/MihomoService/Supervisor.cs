using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.WindowsServices;

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
            // Штатная остановка хоста сообщила бы SCM SERVICE_STOPPED с кодом 0, и действия восстановления
            // (перезапуск службы) не сработали бы. Завершение процесса SCM считает сбоем и перезапускает службу;
            // ядро завершится вместе с job-объектом, а фильтры kill-switch постоянные.
            if (WindowsServiceHelpers.IsWindowsService())
            {
                LogKillSwitchState();
                Environment.Exit(1);
            }
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            LogKillSwitchState();
        }
    }

    private void LogKillSwitchState()
    {
        if (killSwitch.Enabled)
            logger.LogInformation("Kill-switch остаётся включённым: пока туннель не поднят, интернет закрыт");
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        paths.EnsureDirectories();
        logger.LogInformation("Каталог службы: {Dir}", paths.BaseDir);

        // До всего остального, включая проверку ядра: даже если служба дальше упадёт, сеть уже закрыта.
        killSwitch.ApplyStartup();

        if (!core.CoreExists)
            throw new FileNotFoundException($"Не найдено ядро {paths.CoreExe}");
        logger.LogInformation("Ядро: {Version}", await core.GetVersionAsync(stoppingToken));

        using var watcher = WatchConfig();
        var backoff = MinBackoff;
        // Конфигурация, записанная в runtime.yaml и применённая к kill-switch. Проверяется один раз:
        // после падения или перезапуска ядро стартует с ней же, а изменения config.yaml проверяет SuperviseAsync.
        RuntimeConfig? config = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Сначала сеть: проверка конфига может скачивать geo-базы, а без сети она бы провалилась.
            await network.WaitAsync(stoppingToken);

            config ??= await TryApplyConfigAsync(stoppingToken);
            if (config is null)
            {
                logger.LogWarning("Ядро не запущено: исправьте {File}, служба подхватит изменения автоматически " +
                    "(повторная проверка — через {Minutes:F0} мин)", paths.ConfigFile, InvalidConfigRetry.TotalMinutes);
                await WaitForConfigChangeAsync(stoppingToken, InvalidConfigRetry);
                continue;
            }

            var startedAt = Stopwatch.GetTimestamp();
            using var process = core.Start(paths.RuntimeConfig);
            var (exit, applied) = await SuperviseAsync(process, config.Yaml, stoppingToken);
            config = applied ?? config;

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

    /// <returns>Причина выхода и новая применённая конфигурация, если она успела записаться в runtime.yaml.</returns>
    private async Task<(CoreExit Exit, RuntimeConfig? Applied)> SuperviseAsync(
        Process process, string runningYaml, CancellationToken stoppingToken)
    {
        var exited = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (true)
            {
                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    var changed = WaitForConfigChangeAsync(waitCts.Token);
                    if (await Task.WhenAny(exited, changed) == exited)
                    {
                        // Отменённое ожидание возвращает уже прочитанное уведомление в канал — изменение
                        // проверит следующий запуск SuperviseAsync.
                        await waitCts.CancelAsync();
                        await changed.ContinueWith(static _ => { }, TaskScheduler.Default);
                        return (CoreExit.Crashed, null);
                    }

                    try
                    {
                        await changed;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return (CoreExit.Stopping, null);
                    }
                }

                logger.LogInformation("{File} изменён, проверяю новую конфигурацию", ServicePaths.ConfigFileName);
                // Проверка может идти минутами (mihomo -t скачивает недостающие geo-базы) — всё это время
                // следим за ядром, чтобы не оставить его лежать.
                using var applyCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var applying = TryApplyConfigAsync(applyCts.Token);
                if (await Task.WhenAny(exited, applying) == exited && !applying.IsCompleted)
                {
                    await applyCts.CancelAsync();
                    await applying.ContinueWith(static _ => { }, TaskScheduler.Default);
                    if (applying.IsCompletedSuccessfully && applying.Result is { } done)
                        return (CoreExit.Crashed, done);
                    stoppingToken.ThrowIfCancellationRequested();
                    logger.LogWarning("mihomo завершился во время проверки {File}: проверка будет повторена после перезапуска ядра",
                        ServicePaths.ConfigFileName);
                    _configChanged.Writer.TryWrite(true);
                    return (CoreExit.Crashed, null);
                }

                var applied = await applying;
                if (applied is null)
                {
                    logger.LogWarning("Новая конфигурация отклонена, ядро продолжает работать на прежней");
                    continue;
                }
                // Изменились только настройки службы (например, allow-lan) или форматирование — ядро
                // перезапускать незачем. Kill-switch меняет и конфиг ядра (MATCH,REJECT), поэтому перезапускает его.
                if (applied.Yaml == runningYaml)
                {
                    logger.LogInformation("Конфигурация ядра не изменилась, перезапуск не нужен");
                    continue;
                }
                return (CoreExit.Restart, applied);
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

        try
        {
            validator.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Не удалось записать {File}", paths.RuntimeConfig);
            return null;
        }
        var config = result.Config!;
        killSwitch.Apply(config);
        foreach (var warning in config.Warnings)
            logger.LogWarning("{Warning}", warning);
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
        try
        {
            // Редакторы сохраняют файл в несколько приёмов — ждём, пока событие утихнет.
            await Task.Delay(Debounce, ct);
        }
        catch (OperationCanceledException)
        {
            // Уведомление уже прочитано — возвращаем его, чтобы изменение не потерялось.
            _configChanged.Writer.TryWrite(true);
            throw;
        }
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
        watcher.Error += (_, e) =>
        {
            // При переполнении буфера изменение могло потеряться, при других ошибках слежение останавливается.
            logger.LogWarning(e.GetException(), "Ошибка слежения за {File}: конфигурация будет перепроверена",
                ServicePaths.ConfigFileName);
            _configChanged.Writer.TryWrite(true);
            if (watcher.EnableRaisingEvents)
                return;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or ObjectDisposedException)
            {
                logger.LogError(ex, "Слежение за {File} не восстановлено: изменения применятся после перезапуска службы",
                    ServicePaths.ConfigFileName);
            }
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
