using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmtpGateway.Infrastructure.Tests;

/// <summary>
/// Minimal, script-driven fake SMTP server for integration-style tests. Always accepts MAIL FROM,
/// accepts DATA once at least one recipient was accepted, and lets a test script a per-recipient
/// RCPT TO response (e.g. one recipient 250, another 550) - just enough control flow to drive
/// GenericSmtpProvider's per-recipient result tests, not a full SMTP implementation.
/// Optionally also scripts a single-round-trip AUTH XOAUTH2 exchange: when
/// <paramref name="expectedXoauth2InitialResponse"/> is supplied, the server advertises
/// "AUTH XOAUTH2" in EHLO and replies to "AUTH XOAUTH2 &lt;base64&gt;" with success only if the
/// base64 blob matches exactly, otherwise with <paramref name="xoauth2FailureResponse"/>.
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly IReadOnlyDictionary<string, string> _recipientResponses;
    private readonly bool _dropOnConnect;
    private readonly string? _expectedXoauth2InitialResponse;
    private readonly string _xoauth2FailureResponse;

    public int Port { get; }

    public FakeSmtpServer(
        IReadOnlyDictionary<string, string>? recipientResponses = null,
        bool dropOnConnect = false,
        string? expectedXoauth2InitialResponse = null,
        string xoauth2FailureResponse = "535 5.7.8 Authentication credentials invalid")
    {
        _recipientResponses = recipientResponses ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _dropOnConnect = dropOnConnect;
        _expectedXoauth2InitialResponse = expectedXoauth2InitialResponse;
        _xoauth2FailureResponse = xoauth2FailureResponse;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await HandleClientAsync(client, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        if (_dropOnConnect)
        {
            // Simulate a connection dropped mid-handshake: close without ever sending the
            // greeting, so the connecting client fails before any command is attempted.
            client.Client.Close();
            return;
        }

        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 fake.test ESMTP").ConfigureAwait(false);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
            {
                if (_expectedXoauth2InitialResponse is not null)
                {
                    await writer.WriteLineAsync("250-fake.test").ConfigureAwait(false);
                    await writer.WriteLineAsync("250 AUTH XOAUTH2").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync("250 fake.test").ConfigureAwait(false);
                }
            }
            else if (line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var mechanism = parts.Length >= 2 ? parts[1] : string.Empty;
                var initialResponse = parts.Length >= 3 ? parts[2] : string.Empty;

                if (_expectedXoauth2InitialResponse is null
                    || !mechanism.Equals("XOAUTH2", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("504 Unrecognized authentication mechanism").ConfigureAwait(false);
                }
                else if (initialResponse == _expectedXoauth2InitialResponse)
                {
                    await writer.WriteLineAsync("235 2.7.0 Authentication successful").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync(_xoauth2FailureResponse).ConfigureAwait(false);
                }
            }
            else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
            }
            else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
            {
                var address = ExtractAddress(line);
                var response = _recipientResponses.TryGetValue(address, out var scripted)
                    ? scripted
                    : "250 OK";
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 Start mail input").ConfigureAwait(false);

                string? dataLine;
                while ((dataLine = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null && dataLine != ".")
                {
                }

                await writer.WriteLineAsync("250 OK message accepted").ConfigureAwait(false);
            }
            else if (line.StartsWith("NOOP", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                return;
            }
            else
            {
                await writer.WriteLineAsync("500 Command not recognized").ConfigureAwait(false);
            }
        }
    }

    private static string ExtractAddress(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        return start >= 0 && end > start ? line.Substring(start + 1, end - start - 1) : line;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown; the process is exiting the test anyway.
        }

        _cts.Dispose();
    }
}
