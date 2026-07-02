using System.Security.Cryptography;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class FileSpoolTests : IDisposable
{
    private readonly string _root;

    public FileSpoolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.FileSpoolTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsBytesAndReportsMatchingHashAndSize()
    {
        var spool = new FileSpool(_root);
        var key = Guid.NewGuid();
        var bytes = "From: a@example.com\r\nTo: b@example.com\r\n\r\nHello"u8.ToArray();

        var result = await spool.WriteAsync(key, bytes, CancellationToken.None);

        Assert.True(File.Exists(result.Path));
        Assert.Equal(bytes.LongLength, result.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Hash);

        var readBack = await spool.ReadAsync(result.Path, CancellationToken.None);
        Assert.Equal(bytes, readBack);
    }

    [Fact]
    public async Task WriteAsync_DoesNotLeaveTemporaryFilesBehind()
    {
        var spool = new FileSpool(_root);
        var key = Guid.NewGuid();
        var bytes = "raw mime content"u8.ToArray();

        await spool.WriteAsync(key, bytes, CancellationToken.None);

        var remainingFiles = Directory.GetFiles(_root);
        Assert.Single(remainingFiles);
        Assert.Equal(spool.GetFinalPath(key), remainingFiles[0]);
    }

    [Fact]
    public void StrayTruncatedTempFile_NeverProducesOrCorruptsFinalFile()
    {
        var spool = new FileSpool(_root);
        var key = Guid.NewGuid();
        Directory.CreateDirectory(_root);

        // Simulate a crash mid-write: a temp file matching the spool's naming convention
        // exists but was never completed and therefore never renamed into place.
        var strayTempPath = Path.Combine(_root, $"{key:N}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(strayTempPath, "truncated partial cont"u8.ToArray());

        var finalPath = spool.GetFinalPath(key);

        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task WriteAsync_DoesNotOverwriteAnExistingFinalFile()
    {
        var spool = new FileSpool(_root);
        var key = Guid.NewGuid();
        var original = "original content"u8.ToArray();
        var attemptedOverwrite = "different content"u8.ToArray();

        var firstResult = await spool.WriteAsync(key, original, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(
            () => spool.WriteAsync(key, attemptedOverwrite, CancellationToken.None));

        var stillOnDisk = await spool.ReadAsync(firstResult.Path, CancellationToken.None);
        Assert.Equal(original, stillOnDisk);
    }

    [Fact]
    public async Task ConcurrentWrites_ToDifferentKeys_DoNotCollideOrCorruptEachOther()
    {
        var spool = new FileSpool(_root);
        var entries = Enumerable.Range(0, 20)
            .Select(i => (Key: Guid.NewGuid(), Bytes: System.Text.Encoding.UTF8.GetBytes($"message body number {i}")))
            .ToList();

        var results = await Task.WhenAll(
            entries.Select(e => spool.WriteAsync(e.Key, e.Bytes, CancellationToken.None)));

        for (var i = 0; i < entries.Count; i++)
        {
            var readBack = await spool.ReadAsync(results[i].Path, CancellationToken.None);
            Assert.Equal(entries[i].Bytes, readBack);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(entries[i].Bytes)), results[i].Hash);
        }
    }

    [Fact]
    public async Task WriteAsync_FailedCommit_DoesNotLeaveTemporaryFileBehind()
    {
        var spool = new FileSpool(_root);
        var key = Guid.NewGuid();
        await spool.WriteAsync(key, "first"u8.ToArray(), CancellationToken.None);

        // A second write to the same key fails at the File.Move commit point (the final file
        // already exists), exercising the failure path where the temp file was created but never
        // renamed into place.
        await Assert.ThrowsAsync<IOException>(
            () => spool.WriteAsync(key, "second"u8.ToArray(), CancellationToken.None));

        // Only the committed .eml remains: the failed write's temp file was cleaned up rather than
        // leaking forever (an orphan the MaxSpoolBytes quota, which only sums committed queue rows,
        // would never account for).
        var files = Directory.GetFiles(_root);
        Assert.Single(files);
        Assert.Equal(spool.GetFinalPath(key), files[0]);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_CreatesRootDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(_root));

        _ = new FileSpool(_root);

        Assert.True(Directory.Exists(_root));
    }
}
