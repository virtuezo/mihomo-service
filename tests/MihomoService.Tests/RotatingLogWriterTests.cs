namespace MihomoService.Tests;

public sealed class RotatingLogWriterTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _dir = Directory.CreateTempSubdirectory("mihomo-log-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void New_file_starts_with_bom_and_reopening_does_not_add_another()
    {
        var path = Path.Combine(_dir, "service.log");

        using (var writer = new RotatingLogWriter(path))
            writer.WriteLine("первая");
        using (var writer = new RotatingLogWriter(path))
            writer.WriteLine("вторая");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Bom, bytes[..3]);
        Assert.Equal(1, CountBoms(bytes));
        Assert.Equal(["первая", "вторая"], File.ReadAllLines(path));
    }

    [Fact]
    public void Rotated_file_and_new_file_both_start_with_bom()
    {
        var path = Path.Combine(_dir, "mihomo.log");

        using (var writer = new RotatingLogWriter(path, maxBytes: 20))
        {
            writer.WriteLine("строка до ротации"); // 37 байт — превышает порог, уходит в .1
            writer.WriteLine("ok"); // 7 байт с BOM — ниже порога, остаётся в основном файле
        }

        Assert.Equal(Bom, File.ReadAllBytes(path + ".1")[..3]);
        Assert.Equal(Bom, File.ReadAllBytes(path)[..3]);
        Assert.Equal(["ok"], File.ReadAllLines(path));
    }

    [Fact]
    public void Reader_without_share_delete_defers_rotation_instead_of_retrying_every_line()
    {
        var path = Path.Combine(_dir, "service.log");
        using var writer = new RotatingLogWriter(path, maxBytes: 100);
        writer.WriteLine("начало");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // как Get-Content
        {
            for (var i = 0; i < 50; i++)
                writer.WriteLine($"строка {i}");
            Assert.False(File.Exists(path + ".1"));
        }
        Assert.Contains("строка 49", ReadShared(path));

        // Читатель закрыл файл — ротация при следующем превышении порога.
        for (var i = 0; i < 10 && !File.Exists(path + ".1"); i++)
            writer.WriteLine("после");
        Assert.Contains("строка 49", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void Writes_after_dispose_are_ignored()
    {
        var path = Path.Combine(_dir, "late.log");
        var writer = new RotatingLogWriter(path);
        writer.Dispose();

        writer.WriteLine("поздно");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Failed_write_loses_the_line_but_not_the_writer()
    {
        var path = Path.Combine(_dir, "full.log");
        var writer = new RotatingLogWriter(path);
        writer.WriteLine("до");

        // Блокировка диапазона даёт тот же путь, что и нехватка места: WriteFile падает, байты остаются в буфере.
        using (var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            locker.Lock(0, long.MaxValue);
            writer.WriteLine("потеряна");
            locker.Unlock(0, long.MaxValue);
        }
        writer.WriteLine("после");
        writer.Dispose();

        Assert.Equal(["до", "после"], File.ReadAllLines(path));
    }

    // Писатель держит файл открытым на запись — File.ReadAllText с ним не уживается.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private static int CountBoms(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length - 2).Count(i => bytes.AsSpan(i, 3).SequenceEqual(Bom));
}
