namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Builds the fixed export destination path for the 'queue export' command: always
/// "exports/&lt;id&gt;.eml" relative to the current working directory - no destination-path prompt.
/// </summary>
public static class ExportPathBuilder
{
    public const string ExportDirectoryName = "exports";

    public static string BuildPath(Guid queueItemId) =>
        Path.Combine(ExportDirectoryName, $"{queueItemId}.eml");
}
