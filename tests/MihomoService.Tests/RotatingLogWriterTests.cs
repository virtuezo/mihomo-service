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

    private static int CountBoms(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length - 2).Count(i => bytes.AsSpan(i, 3).SequenceEqual(Bom));
}
