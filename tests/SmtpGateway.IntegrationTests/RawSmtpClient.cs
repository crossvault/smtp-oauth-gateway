using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// One parsed SMTP reply: the numeric status code plus the text of each line that made up the
/// (possibly multi-line) reply. For a multi-line reply such as an EHLO capability listing, every
/// continuation line shares the same <see cref="Code"/> and its text is captured in <see cref="Lines"/>.
/// </summary>
internal sealed class RawSmtpReply(int code, IReadOnlyList<string> lines)
{
    public int Code { get; } = code;

    public IReadOnlyList<string> Lines { get; } = lines;

    /// <summary>The leading digit of the status code (2 = success, 4 = transient, 5 = permanent).</summary>
    public int CodeClass => Code / 100;

    public override string ToString() => $"{Code} [{string.Join(" | ", Lines)}]";
}

/// <summary>
/// A deliberately unhelpful, byte-level SMTP client for negative/protocol-robustness testing. Unlike
/// MailKit it performs no client-side validation and no auto-EHLO, so a test can send exactly the
/// bytes it wants - malformed commands, out-of-order commands, a half-written DATA payload followed
/// by an abrupt socket close - and observe precisely what the server does. Reads full multi-line
/// replies correctly (continuation lines have a '-' as the fourth character, the final line a space).
/// </summary>
internal sealed class RawSmtpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;

    private RawSmtpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.ASCII);
    }

    public static async Task<RawSmtpClient> ConnectAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        return new RawSmtpClient(tcp);
    }

    /// <summary>Reads one complete SMTP reply, collapsing multi-line continuations into a single result.</summary>
    public async Task<RawSmtpReply> ReadReplyAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var code = -1;

        while (true)
        {
            var line = await _reader.ReadLineAsync(ct)
                ?? throw new IOException("The server closed the connection while a reply was being read.");

            if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out code))
            {
                throw new IOException($"Malformed SMTP reply line: '{line}'.");
            }

            lines.Add(line.Length > 4 ? line[4..] : string.Empty);

            // RFC 5321 4.2.1: a hyphen as the fourth character marks a continuation line; a space
            // (or a bare 3-char line) marks the final line of the reply.
            if (line.Length < 4 || line[3] == ' ')
            {
                break;
            }
        }

        return new RawSmtpReply(code, lines);
    }

    /// <summary>Writes a command followed by CRLF (no reply is read).</summary>
    public async Task SendLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>Sends a command line and reads the resulting reply.</summary>
    public async Task<RawSmtpReply> CommandAsync(string command, CancellationToken ct)
    {
        await SendLineAsync(command, ct);
        return await ReadReplyAsync(ct);
    }

    /// <summary>Writes raw bytes verbatim, exactly as given, with no framing or CRLF added.</summary>
    public async Task SendRawAsync(string payload, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>
    /// Abruptly tears the TCP connection down (RST-style) without a QUIT, simulating a crashed or
    /// killed client. Used to prove the server cleans up an in-flight transaction with no partial commit.
    /// </summary>
    public void AbortConnection()
    {
        _tcp.LingerState = new LingerOption(enable: true, seconds: 0);
        _tcp.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
