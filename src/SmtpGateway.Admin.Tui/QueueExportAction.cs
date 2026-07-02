using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// The shared 'queue export' write path: reads a queue item's raw MIME from the file spool and
/// writes it to the fixed <c>exports/&lt;id&gt;.eml</c> destination (created if missing), returning
/// the absolute path written. Used by both the <c>queue export</c> command and the interactive
/// shell so the two hit the exact same spool/file code path.
/// </summary>
public static class QueueExportAction
{
    public static async Task<string> ExportAsync(GatewayOptions options, QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(item);

        var spool = new FileSpool(options.SpoolDirectory);
        var rawMime = await spool.ReadAsync(item.MimePath, cancellationToken).ConfigureAwait(false);

        var exportPath = ExportPathBuilder.BuildPath(item.Id);
        var exportDirectory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
        }

        await File.WriteAllBytesAsync(exportPath, rawMime, cancellationToken).ConfigureAwait(false);
        return Path.GetFullPath(exportPath);
    }
}
