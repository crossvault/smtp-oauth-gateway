# SmtpGateway

*Deutsche Version: [README.de.md](README.de.md)*

[![CI](https://github.com/crossvault/smtp-oauth-gateway/actions/workflows/ci.yml/badge.svg)](https://github.com/crossvault/smtp-oauth-gateway/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Let an old application that can only send plain, unencrypted email send it through a modern,
authenticated mail service instead - without touching the old application's code.**

Many legacy or on-premises programs know only one way to send mail: plain SMTP with no encryption
and no login. Modern mail services (like Microsoft 365) no longer accept that. SmtpGateway sits in
between. It runs as a Windows Service on the same machine, pretends to be that old-style mail
server on `127.0.0.1` (your own computer only), safely stores every message it receives, and then
forwards it in the background over a proper, authenticated, encrypted connection.

---

## Getting started (the simple version)

This is written for someone who is **not** a system administrator. You just want your old program
to send email again. Follow the steps in order.

Before you start you need two things:

- A **Windows machine** where you can run programs as administrator (this is where your old
  application already runs, or a machine it can reach on `127.0.0.1`).
- **PowerShell 7 or newer** (the install scripts require it - the older "Windows PowerShell 5.1"
  that ships with Windows is not enough). If you don't have it, install it once from the Microsoft
  Store, or run `winget install Microsoft.PowerShell` in a terminal.

You also need one email account that SmtpGateway will send *through*. For Microsoft 365 this means
an administrator has to register the gateway once in your organisation - the exact clicks are in
[docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md) (recommended) or
[docs/microsoft365-setup.md](docs/microsoft365-setup.md). If you have a normal SMTP relay with a
username and password instead, you can skip the Microsoft steps.

### 1. Download

Go to the project's **Releases** page:
<https://github.com/crossvault/smtp-oauth-gateway/releases>

Download the latest release `.zip` file (and, if you want to verify it, its matching `.sha256`
checksum file).

### 2. Extract it

Unzip it to a permanent location, for example `C:\SmtpGateway`. Don't run it from your Downloads
folder or the Desktop - the service will keep running from wherever you put it. Inside you will
find, among other things:

- `service\SmtpGateway.Service.exe` - the gateway itself, and next to it `appsettings.json`
- `tui\SmtpGateway.Admin.Tui.exe` - a small admin tool to check on it
- `install-service.ps1`, `start-service.ps1`, `stop-service.ps1`, `uninstall-service.ps1`

You do **not** need to install .NET or anything else - everything is included.

### 3. Fill in the configuration

SmtpGateway is configured by a single file, `service\appsettings.json`. There are two ways to fill
it in: let the admin tool's guided wizard do it (easiest), or edit the file by hand.

**Option A (recommended): the setup wizard.** Run the admin tool with the `setup` command - this
does **not** need an elevated/Administrator terminal, since it only writes a configuration file:

```powershell
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe setup --config C:\SmtpGateway\service\appsettings.json
```

The wizard walks you through three short pages - inbound listening (just keep the default
`127.0.0.1:2525`), the storage locations, and your outbound provider (Graph / M365Oauth /
GenericSmtp and that provider's fields) - then shows a review page. Press **Save** and it writes
`service\appsettings.json` for you and reminds you a service restart is needed; **Cancel** writes
nothing. If the file already exists, its current values are offered as defaults and the whole file
is rewritten on Save (no backup is kept).

> The `--config` above points the wizard at the service's own `service\appsettings.json`. Without
> it, the tool reads and writes an `appsettings.json` in the current directory instead (i.e. next to
> the `tui\` executable), which is **not** the file the service uses.

**Option B: edit the file by hand.** Open `service\appsettings.json` in a text editor (Notepad is
fine). The file that ships in the ZIP has every option filled in with comments; you only need to
fill in the details for **one** way of sending. Here is the smallest configuration that works, for
the most common case - sending through Microsoft 365 with Microsoft Graph:

```json
{
  "Gateway": {
    "Smtp": {
      "BindEndpoints": [ "127.0.0.1:2525" ]
    },
    "SpoolDirectory": "C:\\ProgramData\\SmtpGateway\\spool",
    "QueueDatabasePath": "C:\\ProgramData\\SmtpGateway\\queue.db",
    "OutboundProvider": {
      "Provider": "Graph",
      "Graph": {
        "TenantId": "00000000-0000-0000-0000-000000000000",
        "ClientId": "00000000-0000-0000-0000-000000000000",
        "ClientSecret": "REPLACE_WITH_YOUR_CLIENT_SECRET",
        "Mailbox": "gateway@yourcompany.com"
      }
    }
  }
}
```

Replace the four `Graph` values with the ones your administrator gives you when they follow
[docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md). `Mailbox` is the address the mail
will be sent *from*.

- Prefer classic **Microsoft 365 SMTP** instead of Graph? Set `"Provider": "M365Oauth"` and fill in
  the `M365Oauth` block (same four values); see [docs/microsoft365-setup.md](docs/microsoft365-setup.md).
- Using a plain **SMTP relay** with a host, username and password? Set `"Provider": "GenericSmtp"`
  and fill in the `GenericSmtp` block; see [docs/operations.md](docs/operations.md).

The full list of every setting is in [docs/configuration.md](docs/configuration.md). Note: there is
**no automatic reload** - if you change this file later, you must restart the service (step 4's
scripts do that).

### 4. Install and start the service

Right-click the Windows Start button, choose **Terminal (Admin)** or **PowerShell (Admin)**, and
confirm the "Run as administrator" prompt. Then run these two commands (adjust the path if you
extracted somewhere other than `C:\SmtpGateway`):

```powershell
C:\SmtpGateway\install-service.ps1 -ExePath C:\SmtpGateway\service\SmtpGateway.Service.exe
C:\SmtpGateway\start-service.ps1
```

The first registers the service (it will start automatically with Windows from now on); the second
starts it right away. If you ever need to stop or remove it, use `stop-service.ps1` and
`uninstall-service.ps1` from the same elevated terminal.

### 5. Point your old application at the gateway

In your legacy application's mail/SMTP settings, use:

- **Server / host:** `127.0.0.1`
- **Port:** `2525` (the port from `BindEndpoints` in step 3)
- **Username / password:** none - leave them blank
- **Encryption (TLS/SSL/STARTTLS):** none / off

That's the whole point: your old application talks plainly to `127.0.0.1`, and SmtpGateway does the
secure, authenticated part for it.

### 6. Send a test message and check it arrived

Send one email from your application. Then, in the same elevated terminal, ask the admin tool what
happened:

```powershell
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe status
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe queue list
```

`status` shows an overview; `queue list` shows recent messages. A message that was accepted and
delivered shows up as `Sent`. If it's still `Queued` or `RetryScheduled`, give it a moment - the
gateway retries in the background. For a full, copy-paste walkthrough (including how to send a test
message without your application), see [docs/smoke-test.md](docs/smoke-test.md).

> The admin tool reads the same `appsettings.json`. If you run it from a different folder, add
> `--config C:\SmtpGateway\service\appsettings.json` to each command. In its own help text the tool
> calls itself `smtpgw-admin`.

> Tip: running `SmtpGateway.Admin.Tui.exe` with **no arguments** opens an interactive menu (with a
> dashboard, queue browser, configuration view, first-time setup, and provider test) - handy for
> exploring without remembering command names. Every command shown above still works exactly as
> written when you pass arguments, so scripting is unaffected.

### When something goes wrong

- The service won't start, nothing gets accepted, or messages sit in the queue and never send:
  start with [docs/troubleshooting.md](docs/troubleshooting.md).
- To check just the outbound connection (does the login/certificate work?) without sending a real
  message: `SmtpGateway.Admin.Tui.exe provider test`.

---

## What it is and how it works (for technical readers)

SmtpGateway is a send-only outbound relay. It is **not** a mail server: no POP3, no IMAP, no
inbound mailbox storage. It accepts mail on a loopback-only (by default) SMTP listener, writes each message
durably to a file spool **and** a SQLite queue, returns `250 OK` only once **both** are committed,
and then a background worker delivers each message through exactly one configured provider with
retry and backoff.

```
  legacy app
      |  plain, unauthenticated SMTP
      v
  127.0.0.1:2525  ── SmtpGateway (Windows Service) ──────────────────┐
      |                                                              |
      |  accept + persist                                            |
      v                                                              |
  file spool  +  SQLite queue      ->  250 OK returned only          |
      |            (durable)             after BOTH are committed    |
      v                                                              |
  delivery worker (retry / backoff, per-recipient status)           |
      |                                                              |
      +---------------------+---------------------+                  |
      v                     v                     v                  |
  Generic SMTP        M365 SMTP OAuth      Microsoft Graph           |
   (TLS relay)         (XOAUTH2, 587)      (sendMail, raw MIME)      |
      |                     |                     |                  |
      +---------------------+---------------------+------------------+
                            v
                  recipients' mailboxes
```

### Features

- **Loopback-only inbound by default.** The listener binds to `127.0.0.1` / `::1` and refuses to
  start on any other address unless you explicitly opt in with `Smtp:AllowNonLoopbackBind`. Binding
  to a LAN/wildcard address is possible but deliberately gated, logs unmissable startup security
  warnings, and should be paired with the optional inbound SMTP AUTH (see
  "Binding to a network address (advanced)" below and [docs/security.md](docs/security.md)).
- **Optional inbound SMTP AUTH.** Set `Smtp:AuthUsername` **and** `Smtp:AuthPassword` to require
  every inbound session to authenticate (PLAIN/LOGIN); leave both empty (the default) for no inbound
  auth. Intended mainly for a network-bound listener - note the inbound listener has no STARTTLS, so
  those credentials cross the network in cleartext.
- **Durable, at-least-once delivery.** Spool file + SQLite queue; `250 OK` only after both commit;
  retry/backoff with per-recipient delivery status and a queue TTL (capped at 5 days).
- **Three outbound providers, exactly one active:**
  - **Generic SMTP** relay with TLS (STARTTLS or implicit) and optional username/password auth.
  - **Microsoft 365 SMTP AUTH OAuth** - XOAUTH2 client-credentials against `smtp.office365.com:587`.
  - **Microsoft Graph `sendMail`** - raw MIME upload, needs only the `Mail.Send` application
    permission.
- **Companion Admin tool** (`SmtpGateway.Admin.Tui.exe`, self-named `smtpgw-admin`) for status,
  queue inspection/retry/discard/export, config view/edit/validate, and a live provider test.
- **Self-contained.** Ships as a win-x64 ZIP; end users never install .NET.
- Optional spool-size backpressure and outbound rate limiting.

### Binding to a network address (advanced)

> The recommended and default setup keeps the listener on loopback (`127.0.0.1`) - the beginner
> steps above already do this, and most deployments never need to change it. Only read on if a
> legacy client on **another** host must reach the gateway.

By default the gateway refuses to bind anything but `127.0.0.1` / `::1`, and the startup error names
the flag that overrides this. To listen on a specific LAN IP or a wildcard (`0.0.0.0` / `[::]`):

1. Set `Gateway:Smtp:AllowNonLoopbackBind` to `true` and list the network endpoint in
   `BindEndpoints` (e.g. `"192.168.1.10:2525"`). The service then logs an unmissable startup
   **WARNING** that it is reachable from the network.
2. **Configure inbound SMTP AUTH** (recommended, not enforced): set both `Smtp:AuthUsername` and
   `Smtp:AuthPassword` so an unauthenticated host cannot relay mail through your provider. Without
   it, anyone who can reach the port is an open relay.
3. Be aware the inbound listener has **no STARTTLS**: those AUTH credentials (and message content)
   cross the network in **cleartext**. Treat them as sniffable on that network segment.
4. Restrict the port to the specific source host(s) with **Windows Firewall**.

As with every configuration change, a service restart is required. See
[docs/security.md](docs/security.md) for the full warning matrix and
[docs/configuration.md](docs/configuration.md) for the exact keys and validation rules.

### Documentation

| Doc | Covers |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Project layout, inbound flow, queue state machine, outbound providers, delivery worker, backpressure/rate limiting |
| [docs/configuration.md](docs/configuration.md) | Full `appsettings.json` reference, defaults, validation rules, environment variable overrides |
| [docs/operations.md](docs/operations.md) | Running as a Windows Service, logs, the Admin tool command reference, generic SMTP relay setup |
| [docs/microsoft365-setup.md](docs/microsoft365-setup.md) | Microsoft 365 SMTP AUTH OAuth setup (Entra app, permissions, PowerShell steps) |
| [docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md) | Microsoft Graph `sendMail` provider setup |
| [docs/security.md](docs/security.md) | Security model: loopback-only, TLS, OAuth, secrets handling, spool storage decisions |
| [docs/queue.md](docs/queue.md) | Queue/spool design, retry/backoff schedule, TTL, per-recipient status, limitations |
| [docs/testing.md](docs/testing.md) | Test projects, how to run them, coverage approach |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Common failure modes and what to check |
| [docs/smoke-test.md](docs/smoke-test.md) | End-to-end smoke test with a real SMTP client |

### The admin tool at a glance

```
smtpgw-admin                        # no arguments: open the interactive menu (dashboard, queue, config, setup, test)
smtpgw-admin setup                  # first-install wizard: fill in appsettings.json (inbound, storage, provider)
smtpgw-admin status                 # queue and provider status dashboard
smtpgw-admin queue list             # list queue items (filter with --status)
smtpgw-admin queue show <id>        # full detail for one item
smtpgw-admin queue retry <id>       # reset non-sent recipients to retryable
smtpgw-admin queue discard <id>     # stop further delivery attempts
smtpgw-admin queue export <id>      # write raw MIME to exports/<id>.eml
smtpgw-admin config show            # show every Gateway setting (secrets in cleartext)
smtpgw-admin config set <path> <v>  # set one setting, e.g. Smtp:MaxRecipients
smtpgw-admin config validate        # validate appsettings.json
smtpgw-admin provider test          # connectivity/health check against the active provider
```

The executable in the ZIP is `tui\SmtpGateway.Admin.Tui.exe`; `smtpgw-admin` is the name it uses in
its own help output. Add `--config <path-to-appsettings.json>` when running it from elsewhere.

---

## Building from source

You need the **.NET 10 SDK** (pinned to `10.0.301` via `global.json`) on Windows. From the
repository root:

```powershell
git clone https://github.com/crossvault/smtp-oauth-gateway.git
cd smtp-oauth-gateway
dotnet build
dotnet test
```

- Builds treat **all compiler warnings as errors** (`Directory.Build.props`).
- Tests use **xUnit v3** on the **Microsoft.Testing.Platform** runner; `dotnet test` is still the
  entry point and needs no special flags.
- The live end-to-end suite (`tests/SmtpGateway.E2ETests`) sends real mail through a Microsoft 365
  tenant and is **optional** - it self-skips cleanly when no credentials are configured, so a normal
  `dotnet test` run never touches the network.
- To produce a release ZIP yourself: `./build-release.ps1 -Version 0.1.0` (self-contained,
  single-file win-x64 builds of the service and the admin tool, plus the scripts, a sample config,
  `LICENSE`, an SBOM and third-party notices).

CI (`.github/workflows/ci.yml`) restores, builds in Release, runs the tests, and audits dependencies
for known vulnerabilities on `windows-latest`.

## Known limitations

Delivery is **at-least-once**, so a message can in rare crash/retry scenarios be delivered more than
once - do not use SmtpGateway where exactly-once delivery is required. Inbound is **loopback-only by
default**; it can be bound to a network address only via the explicit `Smtp:AllowNonLoopbackBind`
opt-in, which is strongly discouraged unless you also configure inbound SMTP AUTH and firewall the
port (see [docs/security.md](docs/security.md)). The service is **Windows-only** (it relies on
Windows Service hosting and publishes as win-x64). See [docs/queue.md](docs/queue.md) and
[docs/security.md](docs/security.md) for the precise guarantees.

## Contributing

Contributions are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md).

## License and warranty

SmtpGateway is released under the **MIT License**, Copyright (c) crossVault GmbH. See
[LICENSE](LICENSE) for the full text.

**This software is provided "as is", without warranty of any kind, express or implied.** You use it
at your own risk; crossVault GmbH accepts no liability for any damage, data loss, or missed or
duplicated email arising from its use.
