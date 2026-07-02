using System.Text.Json.Nodes;
using SmtpGateway.Admin.Tui;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>Pure dotted-path get/set/flatten logic - no file I/O needed for most of these.</summary>
public sealed class ConfigDocumentTests
{
    [Fact]
    public void SetPath_ThenGetPath_RoundTrips()
    {
        var section = new JsonObject();

        ConfigDocument.SetPath(section, "Smtp:MaxRecipients", "250");

        Assert.Equal("250", ConfigDocument.GetPath(section, "Smtp:MaxRecipients"));
    }

    [Fact]
    public void SetPath_NestedProviderSecret_RoundTrips()
    {
        var section = new JsonObject();

        ConfigDocument.SetPath(section, "OutboundProvider:GenericSmtp:Password", "s3cr3t");

        Assert.Equal("s3cr3t", ConfigDocument.GetPath(section, "OutboundProvider:GenericSmtp:Password"));
    }

    [Fact]
    public void SetPath_CreatesIntermediateObjects()
    {
        var section = new JsonObject();

        ConfigDocument.SetPath(section, "OutboundProvider:Graph:Mailbox", "gateway@example.com");

        Assert.True(section["OutboundProvider"] is JsonObject);
        Assert.True(((JsonObject)section["OutboundProvider"]!)["Graph"] is JsonObject);
        Assert.Equal("gateway@example.com", ConfigDocument.GetPath(section, "OutboundProvider:Graph:Mailbox"));
    }

    [Fact]
    public void SetPath_PreservesUnrelatedExistingKeys()
    {
        var section = new JsonObject
        {
            ["OutboundProvider"] = new JsonObject
            {
                ["Provider"] = "GenericSmtp",
                ["GenericSmtp"] = new JsonObject { ["Host"] = "smtp.example.com", ["Port"] = 587 },
            },
        };

        ConfigDocument.SetPath(section, "OutboundProvider:GenericSmtp:Password", "newpass");

        Assert.Equal("GenericSmtp", ConfigDocument.GetPath(section, "OutboundProvider:Provider"));
        Assert.Equal("smtp.example.com", ConfigDocument.GetPath(section, "OutboundProvider:GenericSmtp:Host"));
        Assert.Equal("587", ConfigDocument.GetPath(section, "OutboundProvider:GenericSmtp:Port"));
        Assert.Equal("newpass", ConfigDocument.GetPath(section, "OutboundProvider:GenericSmtp:Password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":LeadingColon")]
    [InlineData("TrailingColon:")]
    [InlineData("Double::Colon")]
    public void SetPath_MalformedPath_ThrowsFormatExceptionWithoutCorruptingDocument(string malformedPath)
    {
        var section = new JsonObject { ["Existing"] = "untouched" };

        Assert.Throws<FormatException>(() => ConfigDocument.SetPath(section, malformedPath, "value"));

        Assert.Equal("untouched", ConfigDocument.GetPath(section, "Existing"));
    }

    [Fact]
    public void SetPath_ThroughExistingScalar_ThrowsFormatExceptionWithoutCorruptingDocument()
    {
        var section = new JsonObject
        {
            ["OutboundProvider"] = new JsonObject { ["Provider"] = "GenericSmtp" },
        };

        // "Provider" already holds a scalar string - "Provider:Sub" would have to turn it into an
        // object, which must fail clearly rather than silently overwriting it.
        Assert.Throws<FormatException>(() => ConfigDocument.SetPath(section, "OutboundProvider:Provider:Sub", "value"));

        Assert.Equal("GenericSmtp", ConfigDocument.GetPath(section, "OutboundProvider:Provider"));
    }

    [Fact]
    public void GetPath_JsonNullLeaf_ReturnsClrNullNotTheStringNull()
    {
        // A JSON null leaf must be reported as "no value" (C# null), not the literal string "null".
        // Otherwise callers (e.g. the setup wizard's Prefill) treat "null" as a real credential and
        // accidentally enable inbound AUTH.
        var section = new JsonObject
        {
            ["Smtp"] = new JsonObject { ["AuthUsername"] = null },
        };

        Assert.Null(ConfigDocument.GetPath(section, "Smtp:AuthUsername"));
    }

    [Fact]
    public void GetPath_AbsentKey_ReturnsNull()
    {
        var section = new JsonObject { ["Smtp"] = new JsonObject() };

        Assert.Null(ConfigDocument.GetPath(section, "Smtp:AuthUsername"));
    }

    [Fact]
    public void GetPath_RealScalar_ReturnsItsStringValue()
    {
        var section = new JsonObject { ["Smtp"] = new JsonObject { ["AuthUsername"] = "operator" } };

        Assert.Equal("operator", ConfigDocument.GetPath(section, "Smtp:AuthUsername"));
    }

    [Fact]
    public void Flatten_JsonNullLeaf_StillRendersAsTheStringNull()
    {
        // 'config show' (Flatten) intentionally renders a JSON null leaf as "null" for display; the
        // GetPath change must not alter that behavior.
        var section = new JsonObject
        {
            ["Smtp"] = new JsonObject { ["AuthUsername"] = null },
        };

        var rows = ConfigDocument.Flatten(section);

        Assert.Contains(("Smtp:AuthUsername", "null"), rows);
    }

    [Fact]
    public void Flatten_ReturnsEverySortedLeafPath()
    {
        var section = new JsonObject
        {
            ["Smtp"] = new JsonObject { ["MaxRecipients"] = 100 },
            ["OutboundProvider"] = new JsonObject { ["Provider"] = "GenericSmtp" },
        };

        var rows = ConfigDocument.Flatten(section);

        Assert.Contains(("OutboundProvider:Provider", "GenericSmtp"), rows);
        Assert.Contains(("Smtp:MaxRecipients", "100"), rows);
        Assert.Equal(rows.OrderBy(r => r.Path, StringComparer.Ordinal), rows);
    }

    [Fact]
    public void Flatten_ShowsSecretsInCleartext()
    {
        var section = new JsonObject
        {
            ["OutboundProvider"] = new JsonObject
            {
                ["GenericSmtp"] = new JsonObject { ["Password"] = "s3cr3t-plain" },
            },
        };

        var rows = ConfigDocument.Flatten(section);

        Assert.Contains(("OutboundProvider:GenericSmtp:Password", "s3cr3t-plain"), rows);
    }

    [Fact]
    public void GetOrCreateGatewaySection_MissingSection_CreatesEmptyObject()
    {
        var root = new JsonObject();

        var gateway = ConfigDocument.GetOrCreateGatewaySection(root);

        Assert.Empty(gateway);
        Assert.Same(gateway, root[ConfigDocument.GatewaySectionName]);
    }

    [Fact]
    public void GetOrCreateGatewaySection_NonObjectSection_Throws()
    {
        var root = new JsonObject { [ConfigDocument.GatewaySectionName] = "not-an-object" };

        Assert.Throws<InvalidOperationException>(() => ConfigDocument.GetOrCreateGatewaySection(root));
    }

    [Fact]
    public void LoadRoot_MissingFile_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(Path.GetTempPath(), "SmtpGateway.ConfigDocumentTests", Guid.NewGuid().ToString("N") + ".json");

        Assert.Throws<FileNotFoundException>(() => ConfigDocument.LoadRoot(path));
    }

    [Fact]
    public void LoadRoot_TolerantOfCommentsAndTrailingCommas()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SmtpGateway.ConfigDocumentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  // a comment
                  "Gateway": { "SpoolDirectory": "C:\\spool", },
                }
                """);

            var root = ConfigDocument.LoadRoot(path);
            var gateway = ConfigDocument.GetOrCreateGatewaySection(root);

            Assert.Equal(@"C:\spool", ConfigDocument.GetPath(gateway, "SpoolDirectory"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_ThenLoadRoot_RoundTripsAndPreservesUnrelatedTopLevelKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SmtpGateway.ConfigDocumentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Gateway": { "SpoolDirectory": "C:\\spool" },
                  "Logging": { "LogLevel": { "Default": "Information" } }
                }
                """);

            var root = ConfigDocument.LoadRoot(path);
            var gateway = ConfigDocument.GetOrCreateGatewaySection(root);
            ConfigDocument.SetPath(gateway, "QueueDatabasePath", @"C:\queue.db");
            ConfigDocument.Save(root, path);

            var reloaded = ConfigDocument.LoadRoot(path);
            var reloadedGateway = ConfigDocument.GetOrCreateGatewaySection(reloaded);

            Assert.Equal(@"C:\spool", ConfigDocument.GetPath(reloadedGateway, "SpoolDirectory"));
            Assert.Equal(@"C:\queue.db", ConfigDocument.GetPath(reloadedGateway, "QueueDatabasePath"));
            Assert.Equal("Information", reloaded["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
