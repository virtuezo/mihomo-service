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
    private static readonly TimeSpan WatcherRestartDelay = TimeSpan.FromSeconds(5);
    // Конфиг мог быть отклонён из-за временной причины (не скачалась geo-база), поэтому проверка повторяется.
    private static readonly TimeSpan InvalidConfigRetry = TimeSpan.FromMinutes(2);

    // Изменения config.yaml считает FileSystemWatcher; проверенным становится значение, снятое перед чтением
    // файла. Поэтому прерванное ожидание или проверка (упало ядро) ничего не теряют: изменение просто остаётся
    // непроверенным. _wake лишь будит ожидающего.
    private long _changes;
    private long _checkedChanges;
    private readonly Channel<bool> _wake =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private enum CoreExit { Crashed, Restart, Stopping }

    private bool ChangePending => Interlocked.Read(ref _changes) != _checkedChanges;

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
        logger.LogInformation("MihomoService {Version}, каталог службы: {Dir}", BuildInfo.Version, paths.BaseDir);

        // До всего остального, включая проверку ядра: даже если служба дальше упадёт, сеть уже закрыта.
        killSwitch.ApplyStartup();

        if (!core.CoreExists)
            throw new FileNotFoundException($"Не найдено ядро {paths.CoreExe}");
        logger.LogInformation("Ядро: {Version}", await core.GetVersionAsync(stoppingToken));

        using var watcher = WatchConfig();
        var backoff = MinBackoff;
        // Конфигурация, записанная в runtime.yaml и применённая к kill-switch.
        RuntimeConfig? config = null;
        var crashed = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Сначала сеть: проверка конфига может скачивать geo-базы, а без сети она бы провалилась.
            await network.WaitAsync(stoppingToken);

            // Изменение config.yaml проверяется и перед каждым запуском ядра: ядро, которое падает сразу после
            // старта, не дало бы SuperviseAsync дождаться конца проверки, и исправление так и не применилось бы.
            RuntimeConfig? applied = null;
            if (config is null || ChangePending)
            {
                if (config is not null)
                    await Task.Delay(Debounce, stoppingToken); // сохранение файла могло ещё идти
                applied = await TryApplyConfigAsync(stoppingToken);
            }
            if (applied is null && config is null)
            {
                logger.LogWarning("Ядро не запущено: исправьте {File}, служба подхватит изменения автоматически " +
                    "(повторная проверка — через {Minutes:F0} мин)", paths.ConfigFile, InvalidConfigRetry.TotalMinutes);
                await WaitForConfigChangeAsync(stoppingToken, InvalidConfigRetry);
                continue;
            }
            if (applied is null && crashed)
                RestoreAppliedConfig(config!);
            config = applied ?? config!;

            var startedAt = Stopwatch.GetTimestamp();
            using var process = core.Start(paths.RuntimeConfig);
            var (exit, appliedWhileRunning) = await SuperviseAsync(process, config.Yaml, stoppingToken);
            config = appliedWhileRunning ?? config;
            crashed = exit == CoreExit.Crashed;

            if (exit == CoreExit.Stopping)
                break;
            if (exit == CoreExit.Restart)
            {
                backoff = MinBackoff;
                continue;
            }

            if (Stopwatch.GetElapsedTime(startedAt) > StableRun)
                backoff = MinBackoff;
            if (process.ExitCode == 0)
            {
                // Код 0 — mihomo остановился по сигналу. При выключении Windows сигнал доходит до ядра раньше,
                // чем SCM останавливает службу, поэтому запись — после паузы и только если остановки не было.
                await Task.Delay(backoff, stoppingToken);
                logger.LogWarning("mihomo завершил работу по сигналу (код 0, подробности в {Log}), перезапускаю",
                    paths.CoreLogFile);
            }
            else
            {
                logger.LogError("mihomo завершился с кодом {Code} (подробности в {Log}), перезапуск через {Delay} с",
                    process.ExitCode, paths.CoreLogFile, backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
            }
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
                        // Изменение, если оно было, остаётся непроверенным — его проверят перед перезапуском ядра.
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
                    try
                    {
                        // Проверка успела закончиться — ядро перезапустится с её результатом.
                        return (CoreExit.Crashed, await applying);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("mihomo завершился во время проверки {File}: она будет повторена перед перезапуском ядра",
                            ServicePaths.ConfigFileName);
                        return (CoreExit.Crashed, null);
                    }
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
        // Снимается до чтения файла: изменение, сделанное во время проверки, останется непроверенным.
        var changes = Interlocked.Read(ref _changes);
        var result = await validator.ValidateAsync(ct);
        _checkedChanges = changes;
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

    /// <summary>
    /// Перед перезапуском упавшего ядра на прежней конфигурации: runtime.yaml и фильтры kill-switch снова
    /// приводятся к ней — файл могли испортить, а прошлое применение фильтров могло не удаться (ошибка WFP).
    /// </summary>
    private void RestoreAppliedConfig(RuntimeConfig config)
    {
        try
        {
            if (!File.Exists(paths.RuntimeConfig) || File.ReadAllText(paths.RuntimeConfig) != config.Yaml)
            {
                File.WriteAllText(paths.RuntimeConfig, config.Yaml);
                logger.LogWarning("{File} отличался от применённой конфигурации и записан заново", paths.RuntimeConfig);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Не удалось восстановить {File}", paths.RuntimeConfig);
        }
        killSwitch.Apply(config);
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

    /// <summary>Ждёт непроверенного изменения config.yaml.</summary>
    private async Task WaitForConfigChangeAsync(CancellationToken ct)
    {
        while (!ChangePending)
            await _wake.Reader.ReadAsync(ct);
        // Редакторы сохраняют файл в несколько приёмов — ждём, пока событие утихнет.
        await Task.Delay(Debounce, ct);
    }

    private void OnConfigChanged()
    {
        Interlocked.Increment(ref _changes);
        _wake.Writer.TryWrite(true);
    }

    private FileSystemWatcher WatchConfig()
    {
        var watcher = new FileSystemWatcher(paths.BaseDir, ServicePaths.ConfigFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => OnConfigChanged();
        watcher.Created += (_, _) => OnConfigChanged();
        watcher.Renamed += (_, _) => OnConfigChanged();
        watcher.Error += (_, e) =>
        {
            // Изменение могло потеряться — конфигурация в любом случае перепроверяется.
            OnConfigChanged();
            var error = e.GetException();
            if (error is InternalBufferOverflowException)
            {
                logger.LogWarning("Переполнен буфер слежения за {File}: конфигурация будет перепроверена",
                    ServicePaths.ConfigFileName);
                return;
            }
            // Слежение остановилось, но EnableRaisingEvents может остаться true: если ReadDirectoryChangesW
            // отказал сразу, .NET закрывает дескриптор, не сбрасывая флаг. Поэтому перезапуск — выключением
            // и включением, с паузой, чтобы повторяющаяся ошибка не крутила цикл.
            logger.LogWarning(error, "Ошибка слежения за {File}: слежение будет перезапущено", ServicePaths.ConfigFileName);
            _ = RestartWatcherAsync(watcher);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private async Task RestartWatcherAsync(FileSystemWatcher watcher)
    {
        await Task.Delay(WatcherRestartDelay);
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.EnableRaisingEvents = true;
            OnConfigChanged(); // изменения, пропущенные за время простоя
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or ObjectDisposedException)
        {
            logger.LogError(ex, "Слежение за {File} не восстановлено: изменения применятся после перезапуска службы",
                ServicePaths.ConfigFileName);
        }
    }
}
