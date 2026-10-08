using System.Diagnostics;
using System.Text;

namespace MihomoService;

/// <summary>Запуск mihomo.exe: проверка конфига, версия и основной процесс ядра.</summary>
internal sealed class CoreRunner(ServicePaths paths, ILogger<CoreRunner> logger) : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly JobObject _job = new();
    private readonly RotatingLogWriter _coreLog = new(paths.CoreLogFile);

    public bool CoreExists => File.Exists(paths.CoreExe);

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        var (_, output) = await RunToEndAsync(["-v"], ct);
        return output.Split('\n', 2)[0].Trim();
    }

    /// <summary><c>mihomo -t</c>: ядро само проверяет конфиг, не поднимая TUN.</summary>
    public async Task<(bool Ok, string Output)> TestConfigAsync(string configFile, CancellationToken ct)
    {
        var (exitCode, output) = await RunToEndAsync(["-t", "-d", paths.DataDir, "-f", configFile], ct);
        return (exitCode == 0, output);
    }

    public Process Start(string configFile)
    {
        var process = new Process { StartInfo = CreateStartInfo(["-d", paths.DataDir, "-f", configFile]) };
        process.OutputDataReceived += OnCoreOutput;
        process.ErrorDataReceived += OnCoreOutput;
        process.Start();
        AssignToJob(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        logger.LogInformation("mihomo запущен, PID {Pid}", process.Id);
        return process;
    }

    public async Task StopAsync(Process process)
    {
        if (process.HasExited)
            return;

        logger.LogInformation("Останавливаю mihomo, PID {Pid}", process.Id);
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("mihomo не завершился за {Seconds} с", StopTimeout.TotalSeconds);
        }
    }

    public void Dispose()
    {
        _job.Dispose();
        _coreLog.Dispose();
    }

    private void OnCoreOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            _coreLog.WriteLine(e.Data);
    }

    private async Task<(int ExitCode, string Output)> RunToEndAsync(string[] args, CancellationToken ct)
    {
        using var process = Process.Start(CreateStartInfo(args))!;
        AssignToJob(process);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    private void AssignToJob(Process process)
    {
        try
        {
            _job.Assign(process);
        }
        catch (Exception ex)
        {
            // Не фатально: ядро работает, просто не будет убито автоматически при падении службы.
            logger.LogWarning(ex, "Не удалось поместить mihomo (PID {Pid}) в job-объект", process.Id);
        }
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(paths.CoreExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = paths.DataDir,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }
}
