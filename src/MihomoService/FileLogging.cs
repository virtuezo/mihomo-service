using System.Text;

namespace MihomoService;

/// <summary>Потокобезопасная запись строк в файл с простой ротацией: file.log → file.log.1.</summary>
internal sealed class RotatingLogWriter(string path, long maxBytes = 5 * 1024 * 1024) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly long _maxBytes = maxBytes;
    private StreamWriter? _writer;
    private long _rotateAt = maxBytes;
    private bool _disposed;

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line);
                if (_writer.BaseStream.Length >= _rotateAt)
                    Rotate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Лог не должен ронять службу: строку теряем, файл попробуем открыть на следующей записи.
                // Dispose снова сбрасывает недописанный буфер и при той же ошибке (нет места на диске) бросает её
                // повторно — поэтому поле обнуляется заранее, а ошибка закрытия гасится.
                var writer = _writer;
                _writer = null;
                try { writer?.Dispose(); }
                catch (Exception dispose) when (dispose is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
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
        try
        {
            // Свой поток открыт с FileShare.Delete, поэтому файл переименовывается, не закрывая его.
            File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл (или .1) держит другой процесс без FILE_SHARE_DELETE — Get-Content, findstr, type.
            // Пишем дальше в тот же файл и пробуем снова, когда он подрастёт ещё на десятую часть лимита.
            _rotateAt = _writer!.BaseStream.Length + Math.Max(1, _maxBytes / 10);
            return;
        }
        _writer!.Dispose();
        _writer = null;
        _rotateAt = _maxBytes;
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
