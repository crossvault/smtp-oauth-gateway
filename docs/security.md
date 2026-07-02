# Security model

## Inbound: loopback-only by default, opt-in non-loopback, optional AUTH, no local TLS

- **Loopback-only binding by default.** By default `Gateway:Smtp:BindEndpoints` must resolve to
  `127.0.0.1` and/or `::1`; `LoopbackEndpointValidator` refuses to construct the SMTP listener on
  anything else (a specific LAN IP **or** a wildcard `0.0.0.0` / `[::]`, both of which it classifies
  as non-loopback). This check runs when the listener is actually built at service startup, so it
  fails the whole service startup (logged `Critical`, non-zero exit) rather than silently binding to
  an unsafe address, and the startup error names the flag that overrides it. Kept at the default,
  the gateway is purely a local relay for processes on the same machine.
- **Opt-in non-loopback binding (`Smtp:AllowNonLoopbackBind`).** Setting this flag to `true`
  deliberately permits both specific LAN IPs and wildcard binds. It is an explicit operator choice,
  not an accident: when any non-loopback endpoint is active the service emits an unmissable startup
  **warning matrix** (all `LogWarning`, so they cannot be filtered below the normal level):
  1. **Network-reachable.** Whenever a non-loopback endpoint is bound, the service warns that it is
     reachable from the network, not just this host.
  2. **No AUTH (relay risk).** If non-loopback **and** no inbound AUTH is configured, it warns
     strongly that any host that can reach the port can submit mail through the gateway to your
     outbound provider - i.e. an open relay to whoever can reach it.
  3. **AUTH but cleartext.** If non-loopback **and** inbound AUTH is configured, it warns that the
     inbound listener has **no STARTTLS by design**, so the AUTH credentials cross the network in
     **cleartext** and the endpoint must be restricted to a trusted network.
- **Optional, all-or-nothing inbound SMTP AUTH.** `Smtp:AuthUsername` and `Smtp:AuthPassword` are
  empty by default (no inbound authentication). When **both** are set, inbound SMTP AUTH
  (PLAIN/LOGIN) is enabled **and required** for *every* session - loopback included: a session must
  authenticate before `MAIL FROM` (an unauthenticated `MAIL` is rejected `530`), correct credentials
  return `235`, and wrong credentials `535`. Setting exactly one of the two is a startup
  configuration error (`GatewayOptionsValidator`) rather than silently leaving AUTH off. Credentials
  are compared with `CryptographicOperations.FixedTimeEquals` (constant-time) and the password is
  never logged. This is optional-but-recommended when you bind a network address; on loopback the
  default (no AUTH) remains safe because reaching the listener already means running on the host.
- **No local STARTTLS.** The inbound listener never negotiates TLS with the connecting client -
  traffic between the client and the gateway is plaintext SMTP, and (as warning 3 above states) so
  are any inbound AUTH credentials. On loopback this is not an interception risk in the way a
  LAN-facing SMTP session would be, but it does mean message content (and any AUTH credentials) is
  plaintext-observable to anything able to read the relevant traffic - trivially so on a network
  segment, or on the host to a process with `NET_ADMIN`/packet-capture rights. **Recommendation:**
  keep the listener loopback-only wherever possible; if you must bind a network address, configure
  `AuthUsername`/`AuthPassword`, treat those credentials as sniffable on that segment, and restrict
  the port to the specific source host(s) with **Windows Firewall**.
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

- No dedicated open-relay protection exists beyond the default loopback-only binding: at the
  default there is no path for a remote client to reach the listener at all. If you opt in to a
  non-loopback bind (`Smtp:AllowNonLoopbackBind`), that protection is on you - configure inbound
  AUTH (`AuthUsername`/`AuthPassword`) and firewall the port, as the startup warning matrix spells
  out above.
- No HTTP health-check endpoint, named-pipe status API, or metrics endpoint exists - status is
  exposed only via the Admin TUI, the SQLite queue file, and the logs (see
  [docs/operations.md](operations.md)).
- No provider circuit breaker exists; a persistent outbound provider outage is handled entirely
  through the retry/backoff schedule, queue status visibility, logs, and the Admin TUI - not a
  separate breaker/half-open state machine.
