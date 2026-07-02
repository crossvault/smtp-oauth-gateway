using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Minimal, always-accepts fake SMTP server for the outbound delivery worker integration test.
/// Not reused from SmtpGateway.Infrastructure.Tests: that project's FakeSmtpServer type is
/// internal to its own assembly (no InternalsVisibleTo configured), so it is not visible here.
/// This is intentionally a much smaller subset - just enough to let a real GenericSmtpProvider
/// complete a full send successfully.
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public int Port { get; }

    public FakeSmtpServer()
    {
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

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
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
                await writer.WriteLineAsync("250 fake.test").ConfigureAwait(false);
            }
            else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
            }
            else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
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
