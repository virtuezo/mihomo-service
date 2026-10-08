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
internal sealed class ConfigValidator(ServicePaths paths, CoreRunner core)
{
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
        catch (IOException ex)
        {
            return new ValidationResult(null, $"Не удалось прочитать {paths.ConfigFile}: {ex.Message}");
        }

        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.CandidateConfig, config.Yaml, ct);

        var (ok, output) = await core.TestConfigAsync(paths.CandidateConfig, ct);
        return ok
            ? new ValidationResult(config, null)
            : new ValidationResult(null, "mihomo отклонил конфигурацию:" + Environment.NewLine + output);
    }

    public void Commit() => File.Move(paths.CandidateConfig, paths.RuntimeConfig, overwrite: true);

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
