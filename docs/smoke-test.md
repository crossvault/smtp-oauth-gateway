# Smoke test: verifying the gateway end-to-end

This walks through confirming a fresh install actually accepts, queues, and (once a real outbound
provider is configured) delivers a message, using nothing but a Windows Service, the Admin TUI, and
a plain-text SMTP conversation - no third-party mail client required, though any legacy SMTP client
that can be pointed at a custom host/port works equally well.

## Prerequisites

- The service is installed and running (`docs/operations.md`'s Install/Start steps), with
  `Gateway:Smtp:BindEndpoints` set to something like `"127.0.0.1:2525"` (the `smtpgw-admin setup`
  wizard can create that `appsettings.json` for you - see [docs/operations.md](operations.md#setup)).
- You know the configured port. The examples below use `2525`.
- `smtpgw-admin` (the Admin TUI executable) is available, pointed at the same `appsettings.json`
  the service is using (`--config <PATH>` if it isn't in the current directory).

## 1. Confirm the service is listening

```powershell
Test-NetConnection -ComputerName 127.0.0.1 -Port 2525
```

`TcpTestSucceeded : True` confirms the listener is up. If this fails, see
[docs/troubleshooting.md](troubleshooting.md#service-wont-start).

## 2. Send a plain-text test message

The simplest option is a raw SMTP conversation over `Test-NetConnection`'s underlying TCP
connection, or any terminal client that speaks raw TCP (e.g. `ncat`/PowerShell's `System.Net.Sockets.TcpClient`).
The following PowerShell snippet sends one plain-text message start to finish and prints every
server response:

```powershell
$client = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 2525)
$stream = $client.GetStream()
$writer = New-Object System.IO.StreamWriter($stream)
$writer.NewLine = "`r`n"
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($stream)

function Send-Line($line) {
    Write-Host ">> $line"
    $writer.WriteLine($line)
    Write-Host "<< $($reader.ReadLine())"
}

Write-Host "<< $($reader.ReadLine())"   # 220 banner
Send-Line "EHLO smoke-test"
1..10 | ForEach-Object { if ($stream.DataAvailable) { $reader.ReadLine() } }  # drain EHLO extension lines
Send-Line "MAIL FROM:<smoke-test@example.com>"
Send-Line "RCPT TO:<destination@example.com>"
Send-Line "DATA"
$writer.WriteLine("Subject: SmtpGateway smoke test")
$writer.WriteLine("From: smoke-test@example.com")
$writer.WriteLine("To: destination@example.com")
$writer.WriteLine("")
$writer.WriteLine("This is a plain-text smoke test message.")
$writer.WriteLine(".")
Write-Host "<< $($reader.ReadLine())"   # 250 OK for the whole transaction
Send-Line "QUIT"
$client.Close()
```

Replace `destination@example.com` with a real mailbox you can check once an outbound provider is
configured (for the very first smoke test, before any provider is set up, any syntactically valid
address is fine - the message will simply queue and not yet deliver).

A successful transaction ends with a `250` response after the final `.` line - this is the point at
which the message has already been durably written to the spool file and the SQLite queue (see
[docs/architecture.md](architecture.md#inbound-flow-accept-durably-persist-then-250)); the
connection can be closed immediately afterward with no risk of losing the message.

## 3. Confirm it was accepted and queued

```powershell
smtpgw-admin status --config path\to\appsettings.json
smtpgw-admin queue list --config path\to\appsettings.json
```

`status` should show a queue depth count in `Queued` (or already `RetryScheduled`/`Sent` if the
delivery worker already picked it up) that increased by one, and the summary panel's "Total spool
bytes" reflecting the new message. `queue list` should show a new row with a fresh GUID and a
"Created age" of just a few seconds.

Copy that GUID and inspect the full detail:

```powershell
smtpgw-admin queue show <ID> --config path\to\appsettings.json
```

Confirm `Mail from`/`Recipients` match what you sent, and note the `Status` field.

## 4. Confirm delivery (once a real outbound provider is configured)

With a real `GenericSmtp`/`M365Oauth`/`Graph` provider configured (see
[docs/operations.md](operations.md#generic-smtp-relay-setup),
[docs/microsoft365-setup.md](microsoft365-setup.md), or
[docs/microsoft-graph-setup.md](microsoft-graph-setup.md)) and the service restarted to pick up the
config, repeat the send in step 2 with a real destination mailbox you can check, then poll:

```powershell
smtpgw-admin queue show <ID> --config path\to\appsettings.json
```

- `Status: Sent` with every recipient `Sent` means the outbound provider accepted the message for
  delivery (for `Graph`, this specifically means Graph returned `202 Accepted` on the `send` call -
  not a final-delivery confirmation, see [docs/queue.md](queue.md#limitations)). Check the
  destination mailbox to confirm it actually arrived.
- `Status: RetryScheduled` means it hasn't been attempted successfully yet - check
  `smtpgw-admin provider test` and the rolling log file for the underlying error (see
  [docs/troubleshooting.md](troubleshooting.md#messages-stuck-in-the-queue)).
- `Status: Poison` means every attempt permanently failed - check the per-recipient last error in
  the same `queue show` output.

You can also confirm the outbound path independently of a real send with:

```powershell
smtpgw-admin provider test --config path\to\appsettings.json
```

which performs a connect/TLS/auth (or Graph mailbox-reachability) check without sending anything,
and always exits 0 with a clear success/warning line.
