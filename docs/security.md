# Security model

## Inbound: loopback-only, no local authentication, no local TLS

- **Loopback-only binding.** `Gateway:Smtp:BindEndpoints` must resolve to `127.0.0.1` and/or
  `::1`; `LoopbackEndpointValidator` refuses to construct the SMTP listener on anything else. This
  check runs when the listener is actually built at service startup, so it fails the whole service
  startup (logged `Critical`, non-zero exit) rather than silently binding to an unsafe address. The
  gateway is not, and cannot be configured as, an SMTP server reachable from the network - it is
  purely a local relay for processes on the same machine.
- **No local SMTP authentication.** `AuthenticationRequired(false)` is hardcoded in
  `SmtpGatewayListener` - there is no configuration option to require an inbound username/password
  or any other credential for a local client to submit mail. This is safe *because* of the
  loopback-only guarantee above: anything that can reach the listener at all is already running on
  the same machine (or has been given access to it via a container/VM boundary the operator
  controls), which is the same trust boundary an operator implicitly grants any other local-only
  service.
- **No local STARTTLS.** The inbound listener never negotiates TLS with the connecting legacy
  client - traffic between the legacy application and the gateway is plaintext SMTP. This is an
  explicit MVP scope decision: since the connection never leaves loopback, this is not an
  interception risk in the way a LAN-facing SMTP session would be, but this design does mean any
  message content is momentarily plaintext-observable to anything with the ability to read
  loopback traffic on the host (e.g. another process with `NET_ADMIN`/packet-capture rights) - the
  same assumption already implied by the loopback trust boundary.
- **Recipient/size limits reject, never degrade.** `MaxRecipients` and `MaxMessageSizeBytes` are
  enforced by outright rejecting the offending command/transaction - there is no silent truncation
  or partial acceptance.

## Outbound: TLS and provider authentication

- **TLS defaults to `StartTlsRequired`** for the `GenericSmtp` (and hardcoded for `M365Oauth`)
  provider. `SslOnConnect` and `None` are available but must be explicitly chosen.
- **Server certificate validation is on by default** (OS trust store + hostname validation).
  `TrustAllCertificates: true` is an explicit, opt-in escape hatch that disables all certificate
  validation for that provider connection - only intended for legacy relays with self-signed or
  expired certificates where the operator has already accepted the risk. It defaults to `false`.
- **Microsoft Graph** calls are always plain HTTPS to `https://graph.microsoft.com/v1.0` (or an
  overridden base URL, which exists purely as a test seam) - there is no way to disable TLS or
  certificate validation for the Graph provider.
- **OAuth is client-credentials only.** Both the M365 SMTP OAuth and Graph providers use MSAL's
  confidential-client, client-credentials flow (tenant ID + client ID + client secret -> app-only
  access token). There is **no certificate-based credential**, **no delegated/user sign-in flow**,
  and **no device-code flow** supported - this is a deliberate scope decision, not a gap to be
  filled incrementally; a plain enum (`AuthMode`) selects between `None`, `UsernamePassword`, and
  `M365Oauth`, not a pluggable auth-provider registry.
- Access tokens are cached in memory only by `MsalTokenProvider`, refreshed proactively about 5
  minutes before expiry, and are **never persisted to disk**.

## Secrets handling

- `appsettings.json` stores every credential - `GenericSmtp:Password`, `M365Oauth:ClientSecret`,
  `Graph:ClientSecret` - **in cleartext**. There is no DPAPI/Credential Manager/certificate-store
  integration and no external secret store in this version; this is an explicit MVP simplicity
  decision. Protect the file with normal Windows filesystem permissions (e.g. restrict read access
  to the service account and administrators) if that matters in your environment.
- The Admin TUI's `config show`/`config set` commands display and accept secrets **in cleartext**
  on the console - also a deliberate decision (there is no masking/redaction), so treat any
  terminal session running `smtpgw-admin config show` as sensitive.
- **Secrets are never written to logs**, in any of the three log destinations (console, rolling
  file, Windows EventLog). Provider connectivity-check failures (`provider test`,
  `TestConnectionAsync`/`TestMailboxAccessAsync`) are deliberately classified into a short, fixed
  message (e.g. `"Authentication failed."`, `"SMTP command failed: <code>."`) rather than ever
  echoing an underlying exception's own message - so a password or bearer token can never leak into
  a validation-failure message even via an unanticipated exception path.
- The MIME body of a message is also never logged - inbound/outbound log lines reference queue
  item IDs, sizes, and recipient counts only.

## Spool storage: simple local cleartext `.eml` files

The durable spool (`Gateway:SpoolDirectory`) stores every accepted message as a plain, unencrypted
`.eml` file on the local filesystem, indefinitely (nothing is ever deleted automatically - a
`Sent`/`Poison`/`Expired`/`Discarded` item's spool file is retained). **This is an intentional MVP
simplicity decision**: there is no forced NTFS ACL hardening, no BitLocker requirement, no
SQLCipher/at-rest encryption of the SQLite queue database, and no application-level encryption of
spooled message content. The gateway does not manage or enforce disk encryption itself.

If your environment requires encryption at rest for mail content, apply it at the OS/volume level
(e.g. enable BitLocker on the volume hosting `SpoolDirectory`/`QueueDatabasePath`, or restrict
filesystem ACLs on those paths to the service account) - this is entirely compatible with the
gateway's design (it only needs ordinary read/write access to those paths) but is left to the
operator to configure, rather than being a gateway responsibility or a hardcoded requirement.

## Known non-goals

- No open-relay protection is needed beyond loopback-only binding, because there is no path for a
  remote client to ever reach the listener.
- No HTTP health-check endpoint, named-pipe status API, or metrics endpoint exists - status is
  exposed only via the Admin TUI, the SQLite queue file, and the logs (see
  [docs/operations.md](operations.md)).
- No provider circuit breaker exists; a persistent outbound provider outage is handled entirely
  through the retry/backoff schedule, queue status visibility, logs, and the Admin TUI - not a
  separate breaker/half-open state machine.
