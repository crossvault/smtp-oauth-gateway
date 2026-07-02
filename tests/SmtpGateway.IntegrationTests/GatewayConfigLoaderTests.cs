using SmtpGateway.Admin.Tui;
using Xunit;

namespace SmtpGateway.IntegrationTests;

public sealed class GatewayConfigLoaderTests : IDisposable
{
    private readonly string _root;

    public GatewayConfigLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.GatewayConfigLoaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_ValidFile_BindsGatewaySection()
    {
        var configPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(
            configPath,
            """
            {
              "Gateway": {
                "SpoolDirectory": "C:\\spool",
                "QueueDatabasePath": "C:\\queue.db",
                "OutboundProvider": { "Provider": "GenericSmtp" }
              }
            }
            """);

        var options = GatewayConfigLoader.Load(configPath);

        Assert.Equal(@"C:\spool", options.SpoolDirectory);
        Assert.Equal(@"C:\queue.db", options.QueueDatabasePath);
        Assert.Equal("GenericSmtp", options.OutboundProvider.Provider);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        var configPath = Path.Combine(_root, "does-not-exist.json");

        Assert.Throws<FileNotFoundException>(() => GatewayConfigLoader.Load(configPath));
    }

    [Fact]
    public void Load_FileWithoutGatewaySection_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(configPath, """{ "SomethingElse": {} }""");

        Assert.Throws<InvalidOperationException>(() => GatewayConfigLoader.Load(configPath));
    }
}
