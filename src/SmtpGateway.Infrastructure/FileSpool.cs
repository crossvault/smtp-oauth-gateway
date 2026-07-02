using System.Security.Cryptography;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// The result of durably writing a mail's raw MIME to the spool.
/// </summary>
/// <param name="Path">The absolute path of the final, immutable spool file.</param>
/// <param name="Hash">The lowercase hex-encoded SHA-256 hash of the written content.</param>
/// <param name="SizeBytes">The size, in bytes, of the written content.</param>
public readonly record struct SpoolWriteResult(string Path, string Hash, long SizeBytes);

/// <summary>
/// Durable, crash-safe file spool for raw MIME messages. Writes go to a temp file in the
/// same directory, are flushed to disk, and are then atomically renamed into their final,
/// immutable location - the rename is the commit point, so a final file only ever exists
/// once its content is complete on disk.
/// </summary>
public sealed class FileSpool
{
    private readonly string _rootDirectory;

    public FileSpool(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>
    /// The final, immutable path a message for <paramref name="key"/> would be (or is) stored at.
    /// </summary>
    public string GetFinalPath(Guid key) => Path.Combine(_rootDirectory, $"{key:N}.eml");

    public async Task<SpoolWriteResult> WriteAsync(Guid key, byte[] rawMime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawMime);

        var finalPath = GetFinalPath(key);
        var tempPath = Path.Combine(_rootDirectory, $"{key:N}.{Guid.NewGuid():N}.tmp");

        var committed = false;
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.SequentialScan))
            {
                await stream.WriteAsync(rawMime, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            // The rename is the commit point: File.Move throws if finalPath already exists,
            // so a completed write never silently overwrites a prior one.
            File.Move(tempPath, finalPath);
            committed = true;
        }
        finally
        {
            // If the write or the rename threw (disk full, IO error, cancellation on client
            // disconnect during DATA, or a pre-existing final file), the temp file was never
            // renamed into place and would otherwise leak forever - invisible to the MaxSpoolBytes
            // quota, which only sums committed queue rows. Best-effort delete it; the message was
            // never acknowledged, so nothing references it.
            if (!committed && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup; a failure here must not mask the original write error.
                }
            }
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(rawMime));
        return new SpoolWriteResult(finalPath, hash, rawMime.LongLength);
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken ct) =>
        await File.ReadAllBytesAsync(path, ct);
}
