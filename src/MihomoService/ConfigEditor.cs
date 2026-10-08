using System.Text;
using System.Text.RegularExpressions;

namespace MihomoService;

/// <summary>
/// Точечная правка config.yaml как текста: меняется одна строка, комментарии и форматирование
/// пользователя остаются как были (через YAML-модель они бы потерялись).
/// </summary>
internal static partial class ConfigEditor
{
    /// <summary>Записывает <c>service.kill-switch</c> в config.yaml (создаёт файл и раздел при необходимости).</summary>
    public static void SetKillSwitch(string path, bool enabled)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = SetServiceValue(text, "kill-switch", enabled ? "true" : "false");

        // Через временный файл: служба следит за config.yaml и не должна увидеть его наполовину записанным.
        var temp = path + ".tmp";
        File.WriteAllText(temp, updated, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    internal static string SetServiceValue(string text, string key, string value)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? [] : text.Split(newline).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var section = lines.FindIndex(l => ServiceHeader().IsMatch(l));
        if (section < 0)
        {
            if (lines.Any(l => l.StartsWith(RuntimeConfigBuilder.ServiceSection + ":", StringComparison.Ordinal)))
                throw new ConfigException($"Раздел {RuntimeConfigBuilder.ServiceSection} записан в одну строку — измените {key} вручную.");

            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add(RuntimeConfigBuilder.ServiceSection + ":");
            lines.Add($"  {key}: {value}");
            return string.Join(newline, lines) + newline;
        }

        var indent = "  ";
        var keyPattern = new Regex($@"^(\s+){Regex.Escape(key)}:\s*([^#\s][^#]*?)?(\s*#.*)?$");
        for (var i = section + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            if (!char.IsWhiteSpace(line[0]))
                break; // следующий раздел верхнего уровня

            var match = keyPattern.Match(line);
            if (match.Success)
            {
                lines[i] = $"{match.Groups[1].Value}{key}: {value}{match.Groups[3].Value}";
                return string.Join(newline, lines) + newline;
            }
            indent = line[..(line.Length - line.TrimStart().Length)];
        }

        lines.Insert(section + 1, $"{indent}{key}: {value}");
        return string.Join(newline, lines) + newline;
    }

    [GeneratedRegex(@"^service:\s*(#.*)?$")]
    private static partial Regex ServiceHeader();
}
