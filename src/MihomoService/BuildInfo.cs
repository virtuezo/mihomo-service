using System.Reflection;

namespace MihomoService;

/// <summary>Версия сборки для лога и справки: номер, коммит (его добавляет SDK после «+») и время сборки.</summary>
internal static class BuildInfo
{
    public static string Version { get; } = Describe(typeof(BuildInfo).Assembly);

    private static string Describe(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        // Из полного хеша коммита — 7 знаков, как в git log --oneline.
        var plus = version.IndexOf('+');
        if (plus >= 0 && version.Length > plus + 8)
            version = version[..(plus + 8)];
        var built = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;
        return built is null ? version : $"{version}, собрана {built} UTC";
    }
}
