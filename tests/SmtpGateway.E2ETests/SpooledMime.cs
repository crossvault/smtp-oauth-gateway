using System.Security.Cryptography;
using MimeKit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Helpers for inspecting the raw MIME the gateway persisted to its spool - the source of truth
/// that must round-trip a message's content unchanged. Used by the content-fidelity live tests to
/// verify bodies and attachment bytes survive the loopback -> queue -> spool path intact.
/// </summary>
internal static class SpooledMime
{
    public static async Task<MimeMessage> LoadAsync(LivePipelineHarness harness, string mimePath, CancellationToken ct) =>
        await ParseAsync(await harness.ReadSpoolAsync(mimePath, ct), ct);

    /// <summary>
    /// Parses raw MIME bytes into a <see cref="MimeMessage"/>. Lets the mailbox-verification tests
    /// inspect the received MIME (fetched via Graph's <c>$value</c>) with the exact same helpers the
    /// spool checks use, keeping the two sides of each fidelity assertion symmetric.
    /// </summary>
    public static async Task<MimeMessage> ParseAsync(byte[] rawMime, CancellationToken ct)
    {
        using var stream = new MemoryStream(rawMime);
        return await MimeMessage.LoadAsync(stream, ct);
    }

    /// <summary>Lowercase hex SHA-256, matching the spool's own hashing convention.</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Decodes every attachment part in <paramref name="message"/> and maps its file name to the
    /// SHA-256 hash of its decoded bytes, so a test can prove each attachment's content is byte-for-
    /// byte identical to what it sent.
    /// </summary>
    public static Dictionary<string, string> AttachmentHashesByFileName(MimeMessage message)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attachment in message.Attachments.OfType<MimePart>())
        {
            if (attachment.FileName is not { } fileName || attachment.Content is not { } content)
            {
                continue;
            }

            using var stream = new MemoryStream();
            content.DecodeTo(stream);
            result[fileName] = Sha256Hex(stream.ToArray());
        }

        return result;
    }
}
