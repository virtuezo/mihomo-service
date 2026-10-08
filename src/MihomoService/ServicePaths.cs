namespace MihomoService;

/// <summary>
/// Раскладка файлов службы. Всё хранится рядом с исполняемым файлом:
/// <code>
/// MihomoService.exe
/// mihomo.exe          — ядро
/// config.yaml         — пользовательский конфиг (прокси)
/// data\               — рабочий каталог ядра (runtime.yaml, cache.db, geo-базы)
/// logs\               — service.log и mihomo.log
/// </code>
/// </summary>
internal sealed class ServicePaths(string baseDir)
{
    public const string ConfigFileName = "config.yaml";

    public string BaseDir { get; } = baseDir;
    public string ConfigFile => Path.Combine(BaseDir, ConfigFileName);
    public string CoreExe => Path.Combine(BaseDir, "mihomo.exe");
    public string DataDir => Path.Combine(BaseDir, "data");
    public string LogDir => Path.Combine(BaseDir, "logs");
    public string RuntimeConfig => Path.Combine(DataDir, "runtime.yaml");
    public string CandidateConfig => Path.Combine(DataDir, "runtime.new.yaml");
    public string ServiceLogFile => Path.Combine(LogDir, "service.log");
    public string CoreLogFile => Path.Combine(LogDir, "mihomo.log");

    public static ServicePaths FromAppBase() =>
        new(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
