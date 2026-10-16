using System.Text;
using System.Text.RegularExpressions;

namespace MihomoService;

/// <summary>
/// Точечная правка config.yaml как текста: меняется одна строка, комментарии и форматирование
/// пользователя остаются как были (через YAML-модель они бы потерялись).
/// </summary>
internal static partial class ConfigEditor
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed record Line(string Text, string Eol);

    /// <summary>Записывает <c>service.kill-switch</c> в config.yaml (создаёт файл и раздел при необходимости).</summary>
    public static void SetKillSwitch(string path, bool enabled)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var bom = bytes.AsSpan().StartsWith(Utf8Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, bom ? Utf8Bom.Length : 0, bytes.Length - (bom ? Utf8Bom.Length : 0));
        }
        catch (DecoderFallbackException)
        {
            // Перезапись испортила бы символы не в UTF-8 (например, комментарии в cp1251).
            throw new ConfigException($"{path} не в кодировке UTF-8: пересохраните его в UTF-8 или измените " +
                $"{RuntimeConfigBuilder.ServiceSection}.kill-switch вручную.");
        }
        var updated = SetServiceValue(text, "kill-switch", enabled ? "true" : "false");

        // Через временный файл: служба следит за config.yaml и не должна увидеть его наполовину записанным.
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
            // Replace, а не Move: у config.yaml остаются его права доступа (install-service закрывает его
            // от обычных пользователей — в нём учётные данные прокси).
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new IOException($"Не удалось записать {path}: {ex.Message}", ex);
        }
    }

    internal static string SetServiceValue(string text, string key, string value)
    {
        var lines = SplitLines(text);
        // Новые строки получают перевод строки, которым уже пользуется файл.
        var newline = lines.Select(l => l.Eol).FirstOrDefault(e => e.Length > 0) ?? "\n";

        var section = lines.FindIndex(l => ServiceHeader().IsMatch(l.Text));
        if (section < 0)
        {
            if (lines.Any(l => l.Text.StartsWith(RuntimeConfigBuilder.ServiceSection + ":", StringComparison.Ordinal)))
                throw new ConfigException($"Раздел {RuntimeConfigBuilder.ServiceSection} записан в одну строку — измените {key} вручную.");

            if (lines.Count > 0)
            {
                lines[^1] = lines[^1] with { Eol = lines[^1].Eol.Length > 0 ? lines[^1].Eol : newline };
                if (lines[^1].Text.Trim().Length > 0)
                    lines.Add(new Line("", newline));
            }
            lines.Add(new Line(RuntimeConfigBuilder.ServiceSection + ":", newline));
            lines.Add(new Line($"  {key}: {value}", newline));
            return Join(lines);
        }

        var indent = "  ";
        var keyPattern = new Regex($@"^([ \t]+){Regex.Escape(key)}:[ \t]*([^#\s][^#]*?)?([ \t]*#.*)?$");
        for (var i = section + 1; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            if (!char.IsWhiteSpace(line[0]))
                break; // следующий раздел верхнего уровня

            var match = keyPattern.Match(line);
            if (match.Success)
            {
                lines[i] = lines[i] with { Text = $"{match.Groups[1].Value}{key}: {value}{match.Groups[3].Value}" };
                return Join(lines);
            }
            indent = line[..(line.Length - line.TrimStart().Length)];
        }

        if (lines[section].Eol.Length == 0)
            lines[section] = lines[section] with { Eol = newline };
        lines.Insert(section + 1, new Line($"{indent}{key}: {value}", newline));
        return Join(lines);
    }

    /// <summary>Строки вместе с их переводами строк: в файле они могут быть разными (CRLF вперемешку с LF).</summary>
    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            var eolLength = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(new Line(text[start..i], text.Substring(i, eolLength)));
            i += eolLength - 1;
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(new Line(text[start..], ""));
        return lines;
    }

    private static string Join(List<Line> lines) => string.Concat(lines.Select(l => l.Text + l.Eol));

    [GeneratedRegex(@"^service:[ \t]*(#.*)?$")]
    private static partial Regex ServiceHeader();
}
