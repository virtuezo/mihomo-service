using System.Text;

namespace MihomoService;

/// <summary>Потокобезопасная запись строк в файл с простой ротацией: file.log → file.log.1.</summary>
internal sealed class RotatingLogWriter(string path, long maxBytes = 5 * 1024 * 1024) : IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line);
                if (_writer.BaseStream.Length >= maxBytes)
                    Rotate();
            }
            catch (IOException)
            {
                // Лог не должен ронять службу: строку теряем, файл попробуем открыть на следующей записи.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        // BOM нужен Windows PowerShell 5.1: без него Get-Content читает UTF-8 как ANSI.
        // StreamWriter пишет его только в пустой файл, при дозаписи в существующий — нет.
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
    }

    private void Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        File.Move(path, path + ".1", overwrite: true);
    }
}

internal sealed class FileLoggerProvider(RotatingLogWriter writer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..], writer);

    public void Dispose() => writer.Dispose();

    private sealed class FileLogger(string category, RotatingLogWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Level(logLevel)} [{category}] {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            writer.WriteLine(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "FATAL",
        };
    }
}
