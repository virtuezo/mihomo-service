namespace MihomoService.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Version_names_commit_and_build_time()
    {
        // По одному хешу коммита не отличить сборки с незакоммиченными правками — нужно и время сборки.
        Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{7}, собрана \d{4}-\d\d-\d\d \d\d:\d\d UTC$", BuildInfo.Version);
    }
}
