# Troubleshooting

## Service won't start

Check the rolling log file (`logs\smtpgateway-<date>.log` next to the service executable) and the
Windows Event Log (`Application`, source `SmtpGateway`) first - a fatal startup error is always
logged at `Critical` (reaching both destinations) before the process exits non-zero. The most
common cause is a **configuration validation error**: `GatewayOptions` is validated on startup
(`ValidateOnStart()`), so a missing `Required` field, an out-of-range value, or an unrecognized
`OutboundProvider:Provider` value throws before either hosted service starts. Run:

```powershell
smtpgw-admin config validate --config path\to\appsettings.json
```

against the exact config file the service is using - it runs the same validation
(`GatewayOptionsValidator` plus building the outbound provider) outside the service process, with a
clear single-line error message, and never crashes even on a badly malformed file.

If `config validate` passes but the service still won't start, one likely remaining cause is a
**non-loopback `Smtp:BindEndpoints` value** - see "Service refuses to start with a non-loopback bind
address" below.

Also check that the configured port isn't already in use by another process (`netstat -ano | findstr :<port>`),
and - if you configured `Gateway:Smtp:BindEndpoints` to port `25` - see "Port 25 requires
Administrator rights" below.

## Service refuses to start with a non-loopback bind address

The listener is **loopback-only by default**. If `Smtp:BindEndpoints` contains anything other than
`127.0.0.1:<port>` / `[::1]:<port>` - a specific LAN IP, or a wildcard such as `0.0.0.0:<port>` /
`[::]:<port>` - and `Smtp:AllowNonLoopbackBind` is not `true`, `LoopbackEndpointValidator` fails the
service startup by design (logged `Critical`, non-zero exit; `config validate` does **not** catch
this - see [docs/configuration.md](configuration.md)). The startup error names the
`Smtp:AllowNonLoopbackBind` flag.

- If the network bind was **unintended**, fix `BindEndpoints` back to a loopback address - do not
  set the flag.
- If you genuinely need a network-reachable listener, **think twice first**: a non-loopback bind
  makes the gateway reachable from the network, and without inbound AUTH it is effectively an open
  relay. Only then set `Smtp:AllowNonLoopbackBind` to `true`, and read the network-binding guidance
  (configure `AuthUsername`/`AuthPassword`, mind the no-STARTTLS cleartext caveat, firewall the
  port) in [docs/security.md](security.md). Heed the startup warning matrix the service logs in this
  mode. A restart is required after the change.

## Clients get "530 authentication required"

If a legacy client's `MAIL FROM` is rejected with `530` (authentication required), then inbound SMTP
AUTH is configured - both `Smtp:AuthUsername` and `Smtp:AuthPassword` are set - and every session
(loopback included) must authenticate before submitting. Resolve it one of two ways:

- **Make the client authenticate.** Point the legacy application at the configured username/password
  and let it do `AUTH LOGIN`/`AUTH PLAIN` before `MAIL FROM`. Correct credentials return `235`;
  wrong credentials return `535` (and the session still can't submit).
- **Remove the requirement.** If that client cannot authenticate and the listener is loopback-only,
  clear **both** credentials (e.g. `smtpgw-admin config set Smtp:AuthUsername ""` and
  `... Smtp:AuthPassword ""`) to disable inbound AUTH. Setting only one is itself a startup error.
  Restart the service afterward. Do **not** disable AUTH on a non-loopback bind (see
  [docs/security.md](security.md)).

## Messages stuck in the queue

Start with:

```powershell
smtpgw-admin queue show <ID>
```

This prints the item's overall status, next-attempt time, attempt count, last error, and a
per-recipient breakdown (each recipient's own status/attempts/last error). Interpretation:

- **`RetryScheduled`**: normal - it's waiting for its next scheduled attempt (see the retry/backoff
  schedule in [docs/queue.md](queue.md)). If it's been retrying for a long time with the same
  generic error, and the provider is `GenericSmtp` with `AuthMode: UsernamePassword`, double-check
  that `Username`/`Password` are actually set - a missing credential is not caught by config
  validation for this provider (see [docs/configuration.md](configuration.md)) and instead shows up
  as an indefinitely retried failure.
- **`Poison`**: every recipient permanently failed. Check the per-recipient `Last error` values,
  fix the underlying issue (bad recipient address, provider permission problem, etc.), then either
  `smtpgw-admin queue retry <ID>` to try again, or `smtpgw-admin queue discard <ID>` to give up on
  it, or `smtpgw-admin queue export <ID>` to pull the raw `.eml` out for manual handling.
- **`PartiallySent`**: some recipients already succeeded; only the remaining `Retryable`/
  `PermanentlyFailed` ones will be reattempted on `queue retry` - already-sent recipients are never
  resent.

Next, check outbound provider connectivity directly:

```powershell
smtpgw-admin provider test --config path\to\appsettings.json
```

This performs the same connect/TLS/auth (or Graph mailbox-reachability) check the delivery worker
itself does, and always exits 0 with either a green success line (with elapsed time) or a yellow
warning line explaining the failure (never echoing secrets/tokens in that message) - use it to
confirm the problem is genuinely a provider/network/credential issue rather than something specific
to one queued message.

## Port 25 requires Administrator rights

Binding to port 25 (rather than the default `2525`) requires the service process to run with
sufficient Windows privileges to bind a well-known port (typically the `LocalSystem`/`NetworkService`
service account already has this, but a custom service account may not). If the service fails to
start only when `Smtp:BindEndpoints` is changed to include a `:25` endpoint, this is the first thing
to check - either run the service under an account with that right, or keep using a non-privileged
port such as `2525` if legacy clients can be pointed at it instead.

## SMTP AUTH blocked by tenant Security Defaults (Microsoft 365 / `M365Oauth`)

Microsoft 365 disables SMTP AUTH tenant-wide by default on new tenants ("Security Defaults"). If
`provider test` (or real delivery) fails authentication against `smtp.office365.com:587` even
though the Entra app registration, client secret, and mailbox permissions all look correct, confirm
with the Microsoft 365 administrator that:

1. SMTP AUTH client submission is explicitly enabled for the dedicated send-mailbox
   (`Set-CASMailbox -Identity <mailbox> -SmtpClientAuthenticationDisabled $false`).
2. No tenant-wide Security Defaults or Conditional Access policy blocks legacy/basic authentication
   protocols entirely - if one does, SMTP AUTH will remain blocked regardless of the per-mailbox
   setting above, and switching to the `Graph` provider instead is the documented workaround (see
   [docs/microsoft365-setup.md](microsoft365-setup.md) and
   [docs/microsoft-graph-setup.md](microsoft-graph-setup.md)).

## Graph app permission / Application Access Policy misconfiguration

If the `Graph` provider fails `provider test` or real delivery, common causes are:

- **`Mail.Send` application permission missing or admin consent not granted** - surfaces as a `403`
  from Graph, classified as a permanent failure (not retried).
- **Application Access Policy scoped incorrectly** (or missing entirely) - `Mail.Send` is
  tenant-wide by default; if an `Application Access Policy` restricts the app to a security group
  that does not contain the configured `Mailbox`, Graph rejects the call for that mailbox
  specifically even though the same app can send as other mailboxes. Confirm the mailbox is a
  member of the group referenced by the policy (see
  [docs/microsoft-graph-setup.md](microsoft-graph-setup.md)).
- **Mailbox not found / typo in `Graph:Mailbox`** - surfaces as a `404`, permanent failure.
- **`401 Unauthorized`** is treated as *retryable* (a transient token/clock-skew issue can resolve
  itself on the next token acquisition) - if it persists across many attempts, it usually means the
  client secret has expired or been revoked, not a transient issue.
