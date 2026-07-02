using SmtpGateway.Admin.Tui;
using Xunit;

namespace SmtpGateway.IntegrationTests;

public sealed class ExportPathBuilderTests
{
    [Fact]
    public void BuildPath_ReturnsFixedExportsDirectoryWithIdAsFileName()
    {
        var id = Guid.NewGuid();

        var path = ExportPathBuilder.BuildPath(id);

        Assert.Equal(Path.Combine("exports", $"{id}.eml"), path);
    }

    [Fact]
    public void BuildPath_DifferentIds_ProduceDifferentPaths()
    {
        var path1 = ExportPathBuilder.BuildPath(Guid.NewGuid());
        var path2 = ExportPathBuilder.BuildPath(Guid.NewGuid());

        Assert.NotEqual(path1, path2);
    }
}
