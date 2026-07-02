# Operations

## Running as a Windows Service

The release ZIP (see the root [README](../README.md) and `build-release.ps1`) contains
self-contained, single-file executables at `service\SmtpGateway.Service.exe` and
`tui\SmtpGateway.Admin.Tui.exe`, plus four PowerShell scripts. All four scripts **require an
elevated (Administrator) PowerShell session** (`#Requires` is enforced with an explicit role
check) and are `#Requires -Version 7.0` (PowerShell 7+).

### Install

```powershell
./install-service.ps1 -ExePath C:\SmtpGateway\service\SmtpGateway.Service.exe [-ServiceName SmtpGateway] [-DisplayName "SMTP OAuth Gateway"]
```

Registers a Windows Service via `New-Service` with startup type `Automatic`, pointed directly at
the given executable path. `ServiceName` defaults to `SmtpGateway`, `DisplayName` to
`"SMTP OAuth Gateway"`. Fails if a service with that name is already registered (run
`uninstall-service.ps1` first to re-register). Service failure/recovery actions (`sc.exe` recovery
options) are **not** configured - they are left at Windows defaults by design; configure them
yourself via `sc.exe failure` or the Services MMC console if you want automatic restart on crash.

The executable reads `appsettings.json` from its own directory (standard ASP.NET
Core/Generic-Host config discovery) - place your edited config file next to
`SmtpGateway.Service.exe` before starting the service.

### Start / stop

```powershell
./start-service.ps1 [-ServiceName SmtpGateway]
./stop-service.ps1  [-ServiceName SmtpGateway]
```

`start-service.ps1` fails with a clear error if the service isn't registered yet.
`stop-service.ps1` stops it forcibly (`Stop-Service -Force`).

**A service restart (`stop-service.ps1` then `start-service.ps1`, or `Restart-Service`) is required
after any `appsettings.json` change** - there is no hot reload. This applies whether you edited the
file by hand or via `smtpgw-admin config set`.

### Uninstall

```powershell
./uninstall-service.ps1 [-ServiceName SmtpGateway]
```

Stops the service if it's running, then removes the registration via `Remove-Service`.

## Logs

Three independent log destinations are configured, all via Serilog/`Microsoft.Extensions.Logging`
in `SmtpGateway.Service/Program.cs`:

- **Console** - visible when running interactively (e.g. via `dotnet run` or the exe directly, not
  typically observed for a running Windows Service).
- **Rolling file** - `logs\smtpgateway-<date>.log` under the service executable's content root,
  one file per day (`RollingInterval.Day`). This is the primary place to look for routine
  per-message delivery logs, inbound accept/reject events, and warnings.
- **Windows Event Log** (`Application` log, source `SmtpGateway`) - registered at startup if
  possible (skipped with a warning logged to console/file if the event source can't be created,
  e.g. non-elevated context; this never blocks the SMTP/queue functionality). **Deliberately
  filtered** to carry only two things: the dedicated `SmtpGateway.Lifecycle` logger category
  (service/listener/worker start and stop messages) at `Information` and above, plus any log at
  `Critical` from *any* category. Routine per-message delivery logs use their own class-based
  categories and are never written to EventLog - check the rolling file for those.

## Admin TUI (`smtpgw-admin`)

The Admin TUI is `tui\SmtpGateway.Admin.Tui.exe`, a standalone `Spectre.Console.Cli` application
(command name `smtpgw-admin`). Every command accepts a global `--config <PATH>` option (defaults to
`appsettings.json` in the current directory) so you can point it at a config file elsewhere.

### `status`

```
smtpgw-admin status [--config <PATH>]
```

Prints a "Queue depth by status" table (one row per `QueueItemStatus` value) and a summary panel:
oldest queued item's age, total spool bytes, total attempts, recipients sent, recipients
permanently failed, poison item count, and the currently configured outbound provider.

### `queue list`

```
smtpgw-admin queue list [--status <STATUS>] [--config <PATH>]
```

Lists queue items (id, status, recipient count, created age, attempt count). `--status` filters to
one `QueueItemStatus` (`Queued`, `Leased`, `Sending`, `PartiallySent`, `Sent`, `RetryScheduled`,
`Poison`, `Expired`, `Discarded`). **Without `--status`, `Discarded` items are hidden** from the
default listing (queue history is never deleted, but a discarded item shouldn't clutter the
everyday view) - pass `--status Discarded` explicitly to see them.

### `queue show <id>`

```
smtpgw-admin queue show <ID> [--config <PATH>]
```

`<ID>` is the queue item's GUID. Prints full detail: envelope (mail-from, recipients), timestamps,
next-attempt time, lease owner/expiry, attempt count, last error, and spool path/hash
(SHA-256)/size - plus a per-recipient table (address, status, attempts, last error). The raw MIME
body content itself is never printed.

### `queue retry <id>`

```
smtpgw-admin queue retry <ID> [--config <PATH>]
```

Resets every recipient that is not already `Sent` back to `Retryable` with a fresh attempt count
(this is a manual retry, not a continuation of the automatic backoff schedule), and clears the
item's next-attempt time so it is immediately eligible for the delivery worker to pick up. Already-
`Sent` recipients (e.g. on a `PartiallySent` item) are left untouched and never resent. No
confirmation prompt.

### `queue discard <id>`

```
smtpgw-admin queue discard <ID> [--config <PATH>]
```

Marks the item `Discarded` so it is never leased for delivery again, without deleting it from queue
history. No confirmation prompt - this is an explicit product decision, so double-check the ID
before running it.

### `queue export <id>`

```
smtpgw-admin queue export <ID> [--config <PATH>]
```

Reads the item's raw MIME from the spool and writes it to the fixed path `exports/<id>.eml`,
relative to the current working directory (the directory is created if missing). There is no
destination-path prompt or option.

### `config show`

```
smtpgw-admin config show [--config <PATH>]
```

Prints every setting under the `Gateway` section as a `dotted:path -> value` table, read directly
from the raw JSON file. **Secrets (`ClientSecret`, `Password`, ...) are shown in cleartext** - an
explicit product decision, not an oversight.

### `config set`

```
smtpgw-admin config set <PATH> <VALUE> [--config <PATH>]
```

Sets a single setting by its `:`-delimited dotted path (e.g. `Smtp:MaxRecipients`, or
`OutboundProvider:GenericSmtp:Password`), creating intermediate JSON objects as needed, and
preserving every unrelated key. The write happens unconditionally - there is no rollback - after
which the command re-validates the resulting configuration (`GatewayOptionsValidator` plus
building the outbound provider) and prints a **warning only** if the result is now invalid; it does
not undo the write. `appsettings.json` may be reformatted in the process (comments and original
formatting are not preserved). Always prints
`Restart required: the running service must be restarted to pick up this change.`

### `config validate`

```
smtpgw-admin config validate [--config <PATH>]
```

Re-reads the config file, binds it to `GatewayOptions`, and runs `GatewayOptionsValidator` plus
`OutboundProviderFactory.Create` (which catches an invalid `Provider` value or a missing/invalid
provider-specific section). Prints `Configuration is valid.` and exits 0 on success, or a clear
single-line error and exits 1 otherwise. Note: this does **not** catch a non-loopback
`Smtp:BindEndpoints` value - that check only runs when the SMTP listener actually starts (see
[docs/configuration.md](configuration.md)).

### `provider test`

```
smtpgw-admin provider test [--timeout <SECONDS>] [--config <PATH>]
```

Runs an active, non-sending connectivity/health check against the currently configured outbound
provider: connect + TLS + authenticate + `NOOP` for `GenericSmtp`/`M365Oauth`, or a mailbox-
reachability `GET` for `Graph`. `--timeout` defaults to 10 seconds. **This command always exits
0** - a failed check is reported as a yellow warning line only (elapsed time and error message),
never as a command failure, and the result is never persisted.

## Generic SMTP relay setup

Use the `GenericSmtp` provider for anything that isn't Microsoft 365/Graph - an on-premises relay,
a third-party ESP's SMTP endpoint, a smart host, etc. Configure it under
`Gateway:OutboundProvider` in `appsettings.json`:

```json
{
  "OutboundProvider": {
    "Provider": "GenericSmtp",
    "GenericSmtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "TlsMode": "StartTlsRequired",
      "AuthMode": "UsernamePassword",
      "Username": "REPLACE_WITH_YOUR_SMTP_USERNAME",
      "Password": "REPLACE_WITH_YOUR_SMTP_PASSWORD",
      "TrustAllCertificates": false
    }
  }
}
```

- **Host/Port**: your relay's hostname and port. `587` (submission) with `StartTlsRequired` is the
  common default; use `465` with `SslOnConnect` for implicit-TLS relays, or `25`/`TlsMode: None`
  only for a trusted local network relay with no TLS support.
- **TlsMode**: `StartTlsRequired` (negotiate TLS after connecting - the default and recommended
  choice), `SslOnConnect` (implicit TLS/SMTPS from the first byte), or `None` (no TLS at all - only
  for relays you fully trust on a private network).
- **AuthMode**: `None` (no authentication - only for an already-trusted internal relay), or
  `UsernamePassword` (plain SMTP AUTH with `Username`/`Password`). `M365Oauth` is also a valid enum
  value here, but this section has no way to supply an OAuth token provider - use the dedicated
  `M365Oauth` top-level provider selection instead for Microsoft 365.
- **TrustAllCertificates**: leave `false` unless you must talk to a relay with a self-signed or
  expired certificate and have already accepted that risk - it disables all server-certificate
  validation.

After editing, run `smtpgw-admin config validate` and `smtpgw-admin provider test` to confirm
connectivity before restarting the service to pick up the change.
