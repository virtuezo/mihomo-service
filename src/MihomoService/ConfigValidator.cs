using System.ComponentModel;

namespace MihomoService;

internal sealed record ValidationResult(RuntimeConfig? Config, string? Error)
{
    public bool Ok => Config is not null;
}

/// <summary>
/// Собирает runtime-конфиг из config.yaml во временный файл и прогоняет его через <c>mihomo -t</c>.
/// В рабочий runtime.yaml он попадает только после успешной проверки (<see cref="Commit"/>),
/// поэтому битая правка config.yaml не ломает уже работающее ядро.
/// </summary>
/// <param name="candidateFile">Куда собирать конфиг для проверки; у службы и у команды check — разные файлы.</param>
/// <param name="homeDir">Рабочий каталог ядра при проверке (-d); у check — временный, если data\ ещё нет.</param>
internal sealed class ConfigValidator(ServicePaths paths, CoreRunner core, string candidateFile, string homeDir)
{
    public ConfigValidator(ServicePaths paths, CoreRunner core) : this(paths, core, paths.CandidateConfig, paths.DataDir)
    {
    }

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(300);

    public async Task<ValidationResult> ValidateAsync(CancellationToken ct)
    {
        RuntimeConfig config;
        try
        {
            config = RuntimeConfigBuilder.Build(await ReadConfigAsync(ct));
        }
        catch (FileNotFoundException)
        {
            return new ValidationResult(null, $"Не найден {paths.ConfigFile}");
        }
        catch (ConfigException ex)
        {
            return new ValidationResult(null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValidationResult(null, $"Не удалось прочитать {paths.ConfigFile}: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(homeDir);
            Directory.CreateDirectory(Path.GetDirectoryName(candidateFile)!);
            await File.WriteAllTextAsync(candidateFile, config.Yaml, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValidationResult(null, $"Не удалось подготовить проверку конфигурации: {ex.Message}");
        }

        bool ok;
        string output;
        try
        {
            (ok, output) = await core.TestConfigAsync(candidateFile, homeDir, ct);
        }
        catch (Win32Exception ex)
        {
            // Например, антивирус убрал mihomo.exe: это отказ проверки, а не повод ронять службу.
            return new ValidationResult(null, $"Не удалось запустить {paths.CoreExe}: {ex.Message}");
        }
        return ok
            ? new ValidationResult(config, null)
            : new ValidationResult(null, "mihomo отклонил конфигурацию:" + Environment.NewLine + output);
    }

    public void Commit() => File.Move(candidateFile, paths.RuntimeConfig, overwrite: true);

    /// <summary>Редакторы часто держат файл открытым во время сохранения — даём им дописать.</summary>
    private async Task<string> ReadConfigAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(paths.ConfigFile, ct);
            }
            catch (IOException ex) when (ex is not FileNotFoundException && attempt < ReadAttempts)
            {
                await Task.Delay(ReadRetryDelay, ct);
            }
        }
    }
}
