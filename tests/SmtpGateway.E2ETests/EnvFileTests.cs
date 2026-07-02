using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Unit tests for the <see cref="EnvFile"/> parser and walk-up behavior, exercised through the
/// explicit-start-directory seam against scoped temp directories. Not live tests: no real
/// <c>.env</c>, no network, never skip. Each test uses its own uniquely-named temp directory and
/// cleans it up, so they are parallel-safe.
/// </summary>
public sealed class EnvFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "smtpgw-envfile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ParsesKeyValues_IgnoringBlankAndCommentLines()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, ".env"),
            "# a comment\n\nKEY_ONE=value-one\n  KEY_TWO = value-two \nnot-a-pair\n");

        var values = EnvFile.TryLoad(_root);

        Assert.Equal("value-one", values["KEY_ONE"]);
        Assert.Equal("value-two", values["KEY_TWO"]);
        Assert.False(values.ContainsKey("not-a-pair"));
    }

    [Fact]
    public void TryLoad_WalksUpToParentDirectory()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".env"), "PARENT_KEY=parent-value\n");
        var nested = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(nested);

        var values = EnvFile.TryLoad(nested);

        Assert.Equal("parent-value", values["PARENT_KEY"]);
    }
}
