using Spectre.Console;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Shared renderer for 'config show': builds the dotted-path -&gt; value table read directly from
/// appsettings.json, used by both the <c>config show</c> command and the interactive shell's
/// configuration screen. Secrets (ClientSecret, Password, ...) are shown in CLEARTEXT - an explicit
/// product decision, not an oversight.
/// </summary>
public static class ConfigShowRenderer
{
    /// <summary>Loads the raw config document at <paramref name="path"/> and builds the settings table.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file is not a JSON object at its root.</exception>
    public static Table Build(string path)
    {
        var root = ConfigDocument.LoadRoot(path);
        var gateway = ConfigDocument.GetOrCreateGatewaySection(root);
        var rows = ConfigDocument.Flatten(gateway);

        var table = new Table().Title($"Gateway configuration ('{Markup.Escape(path)}')");
        table.AddColumn("Path");
        table.AddColumn("Value");
        foreach (var (rowPath, value) in rows)
        {
            // Config keys and (cleartext) values are operator-controlled strings that Spectre would
            // otherwise parse as markup - a value containing '[' (e.g. a password) would throw a
            // malformed-markup exception and break the display. Escape both cells.
            table.AddRow(Markup.Escape(rowPath), Markup.Escape(value));
        }

        return table;
    }
}
