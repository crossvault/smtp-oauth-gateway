# Configuration reference

All gateway configuration lives under the `Gateway` section of `appsettings.json`, bound to
`SmtpGateway.Infrastructure.GatewayOptions`. **There is no hot reload**: configuration is bound
once at process startup (`ValidateOnStart()`), so the Windows Service (and, separately, the Admin
TUI, which re-reads the file fresh on every invocation) must be restarted/re-run to pick up any
change. See [docs/operations.md](operations.md) for how to apply a change to a running service.

## `Gateway` (top level - `GatewayOptions`)

| Key | Type | Default | Validation | Meaning |
|---|---|---|---|---|
| `Smtp` | object | *(required)* | `[Required]`; see below | Inbound SMTP listener settings. |
| `SpoolDirectory` | string | *(none - must be set)* | `[Required(AllowEmptyStrings = false)]` | Root directory of the durable raw-MIME file spool. Created automatically if missing. |
| `QueueDatabasePath` | string | *(none - must be set)* | `[Required(AllowEmptyStrings = false)]` | Path of the SQLite queue database file. |
| `QueueTtl` | `TimeSpan` | `5.00:00:00` (5 days) | **No `[Range]` attribute** - any value passes DataAnnotations validation, but is silently capped by `EffectiveQueueTtl` (see below) | How long an undelivered item stays queued before it expires. There is no lower-bound check: a zero/negative value is not rejected and effectively expires every item immediately. |
| `LeaseDuration` | `TimeSpan` | `00:05:00` | `[Range]` 1 second - 1 day | How long the delivery worker holds an item leased while attempting delivery. |
| `SenderRewriteAddress` | string? | `null` | none | If set, rewrites the outbound MIME `From:` header to this address before submission. `null`/empty leaves it unchanged. |
| `DeliveryPollInterval` | `TimeSpan` | `00:00:05` | `[Range]` 1 second - 1 hour | How long the delivery loop waits after finding an empty queue before checking again. |
| `TtlSweepInterval` | `TimeSpan` | `00:15:00` | `[Range]` 1 second - 1 day | How often the delivery loop runs the TTL-expiry sweep (and re-releases expired leases). |
| `OutboundProvider` | object | *(required)* | `[Required]`; see below | Discriminated outbound provider selection. |
| `MaxSpoolBytes` | `long?` | `null` (unlimited) | `[Range(1, long.MaxValue)]` | Optional cap on total spool footprint (sum of every queue item's size, regardless of status - nothing ever deletes a spool file). Only operators who set this get inbound backpressure. |
| `OutboundRateLimitPerMinute` | `int?` | `null` (unlimited) | `[Range(1, int.MaxValue)]` | Optional cap on outbound provider submissions per rolling minute. Only operators who set this get throttling. |

`EffectiveQueueTtl` = `QueueTtl`, capped at the hard maximum of 5 days (`RetryPolicy.MaxTtl`). This
cap cannot be raised by configuration - 5 days is the ceiling in this version.

## `Gateway:Smtp` (`SmtpInboundOptions`)

| Key | Type | Default | Validation | Meaning |
|---|---|---|---|---|
| `BindEndpoints` | string array | `[]` (empty - must be set explicitly) | `[MinLength(1)]`, plus format-parsed (`SmtpBindEndpointParser`) during startup validation | Bind endpoints, e.g. `"127.0.0.1:2525"` or `"[::1]:2525"`. Loopback-only unless `AllowNonLoopbackBind` is `true` (see below). Deliberately defaults to empty rather than a pre-populated port, because `Microsoft.Extensions.Configuration`'s array binding *appends* to a non-empty default instead of replacing it - a non-empty compile-time default would silently keep listening on it alongside whatever is configured. |
| `AllowNonLoopbackBind` | bool | `false` | none (policy enforced by `LoopbackEndpointValidator` when the listener is constructed) | When `false` (default), any non-loopback bind endpoint - a specific LAN IP **or** a wildcard (`0.0.0.0` / `[::]`) - fails the service startup. Set `true` to deliberately permit a network-reachable bind; the service then logs unmissable startup security warnings (see [docs/security.md](security.md)). |
| `AuthUsername` | string? | `null` | Both-or-neither: setting exactly one of `AuthUsername`/`AuthPassword` is a startup configuration error (`GatewayOptionsValidator`) | Optional inbound SMTP AUTH username. When **both** `AuthUsername` and `AuthPassword` are set, inbound SMTP AUTH (PLAIN/LOGIN) is enabled **and required** for every session (loopback included: correct creds `235`, wrong `535`, `MAIL` before `AUTH` `530`). Leave both empty for no inbound auth (unchanged behavior). |
| `AuthPassword` | string? | `null` | Both-or-neither (see `AuthUsername`); never logged | Optional inbound SMTP AUTH password. The inbound listener has **no STARTTLS**, so on a non-loopback bind these credentials cross the network in cleartext (see [docs/security.md](security.md)). |
| `ServerName` | string | `"smtpoauth"` | none | The SMTP server name presented in the banner/EHLO response. |
| `MaxMessageSizeBytes` | int | `26214400` (25 MB) | `[Range(1, int.MaxValue)]` | Maximum accepted message size; larger messages are rejected at `DATA` time. |
| `MaxRecipients` | int | `100` | `[Range(1, int.MaxValue)]` | Maximum recipients per mail; additional `RCPT TO` commands beyond this are rejected one at a time (the transaction isn't aborted). |
| `IdleTimeout` | `TimeSpan` | `00:01:00` (60s) | **No validation attribute** - any `TimeSpan` (including zero/negative) is accepted as-is | Session idle timeout. |

**Bind endpoints are only IP literals** - no hostname resolution - and only the format is checked
at startup validation time (`config validate` / `ValidateOnStart`). The **loopback-only**
enforcement (rejecting anything that isn't `127.0.0.1`/`::1` when `AllowNonLoopbackBind` is `false`)
happens later, when the SMTP listener is actually constructed at service startup
(`LoopbackEndpointValidator`, inside `SmtpGatewayListener`) - so a non-loopback bind address with the
flag left off currently passes `config validate` but fails when the service actually tries to start
the listener. Inbound SMTP AUTH is configurable via `AuthUsername`/`AuthPassword` (above), but there
is no inbound TLS/STARTTLS, per-connection rate limit, or concurrent-connection cap in this
version - see [docs/security.md](security.md).

## `Gateway:OutboundProvider` (`OutboundProviderOptions`)

| Key | Type | Default | Validation | Meaning |
|---|---|---|---|---|
| `Provider` | string | *(none - must be set)* | `[Required(AllowEmptyStrings = false)]` | Selects exactly one of `"GenericSmtp"`, `"M365Oauth"`, `"Graph"` (case-insensitive). An unrecognized value passes this attribute check but fails later when the provider is actually built (`OutboundProviderFactory.Create`), which happens both at service startup and via `config validate`. |
| `GenericSmtp` | object? | `null` | Validated only when `Provider` selects it | See below. |
| `M365Oauth` | object? | `null` | Validated only when `Provider` selects it | See below. |
| `Graph` | object? | `null` | Validated only when `Provider` selects it | See below. |

Only the section matching the active `Provider` needs to be populated; the other two can be
omitted entirely.

### `GenericSmtp` (`GenericSmtpSettings`)

| Key | Type | Default | Validation | Meaning |
|---|---|---|---|---|
| `Host` | string | *(none - must be set)* | `[Required(AllowEmptyStrings = false)]` | SMTP relay hostname. |
| `Port` | int | `587` | `[Range(1, 65535)]` | SMTP relay port. |
| `TlsMode` | enum | `StartTlsRequired` | none (`StartTlsRequired` \| `SslOnConnect` \| `None`) | TLS negotiation mode. |
| `AuthMode` | enum | `None` | none (`None` \| `UsernamePassword` \| `M365Oauth`) | Authentication mode. |
| `Username` | string? | `null` | **No `[Required]`** - not conditionally validated against `AuthMode` | SMTP AUTH username. Required in practice when `AuthMode` is `UsernamePassword` or `M365Oauth`, but this is only enforced at connection time, not by config validation. |
| `Password` | string? | `null` | **No `[Required]`** | SMTP AUTH password. Required in practice when `AuthMode` is `UsernamePassword`, only enforced at connection time. |
| `TrustAllCertificates` | bool | `false` | none | Insecure escape hatch: accepts any server certificate (skips hostname/trust-chain validation). Only for relays with self-signed/expired certs where the risk is already accepted. |

**Gotcha:** because `Username`/`Password` are not marked `[Required]`, an `AuthMode` of
`UsernamePassword` with a missing `Username`/`Password` passes `config validate` cleanly, but then
throws an `ArgumentNullException` the first time the delivery worker tries to connect - which the
provider's own generic exception handler currently classifies as a **retryable** failure, not a
permanent one. In practice this means a simple missing-credential typo retries forever (hourly)
instead of quickly surfacing as `Poison`; check `queue show <id>`'s last error and the provider
section values directly if a `GenericSmtp` item never progresses past `RetryScheduled`.

Setting `AuthMode` to `M365Oauth` directly under `GenericSmtp` (rather than using the dedicated
top-level `M365Oauth` provider) will fail fast, because this section has no way to supply the
required `ITokenProvider` - use the `M365Oauth` provider selection instead for Microsoft 365 OAuth.

### `M365Oauth` (`M365OauthSettings`)

All four fields are `[Required(AllowEmptyStrings = false)]` and are checked at both service
startup and `config validate`:

| Key | Meaning |
|---|---|
| `TenantId` | Entra (Azure AD) directory/tenant ID. |
| `ClientId` | Entra app registration's application (client) ID. |
| `ClientSecret` | Entra app registration's client secret value. |
| `Mailbox` | The sender mailbox; used as the SMTP AUTH username for XOAUTH2. |

Host, port, and TLS mode are **not configurable** for this provider - they are hardcoded to
`smtp.office365.com:587` with `StartTlsRequired`. See
[docs/microsoft365-setup.md](microsoft365-setup.md) for the full Entra/Exchange Online setup.

### `Graph` (`GraphSettings`)

All four fields are `[Required(AllowEmptyStrings = false)]`, checked the same way as `M365Oauth`:

| Key | Meaning |
|---|---|
| `TenantId` | Entra directory/tenant ID. |
| `ClientId` | Entra app registration's application (client) ID. |
| `ClientSecret` | Entra app registration's client secret value. |
| `Mailbox` | The sender mailbox (e.g. `gateway@tenant.onmicrosoft.com`); one fixed mailbox per provider instance - not derived dynamically from the envelope or MIME `From:` header. |

See [docs/microsoft-graph-setup.md](microsoft-graph-setup.md) for the full Entra/Exchange Online
setup, including the required Application Access Policy.

## Environment variable overrides

The Windows Service is a standard .NET Generic Host application (`Host.CreateApplicationBuilder`),
so it picks up the conventional environment-variable configuration provider automatically, layered
on top of `appsettings.json`. Use a double underscore (`__`) in place of each `:` section
separator; environment variable names are case-insensitive on Windows. For example:

```
Gateway__Smtp__BindEndpoints__0=127.0.0.1:2525
Gateway__OutboundProvider__Provider=GenericSmtp
Gateway__OutboundProvider__GenericSmtp__Host=smtp.example.com
Gateway__OutboundProvider__GenericSmtp__Password=REPLACE_WITH_YOUR_SMTP_PASSWORD
Gateway__MaxSpoolBytes=5368709120
```

Array values (like `BindEndpoints`) are addressed by index (`__0`, `__1`, ...). Because
configuration binding merges providers **by index**, an environment variable that overrides only
index `0` merges with (does not wholesale replace) any additional entries `appsettings.json` sets
at index `1`, `2`, etc. - set every index explicitly via environment variables if you want a full
replacement of a configured array.

Environment variables set on a Windows Service process only take effect if they are visible to the
service process itself (e.g. set via `setx` at the machine/user scope before the service starts, or
configured on the service's registry entry) - they are not picked up from an interactive shell the
operator happens to be using unless that same session started the service. As with any other
configuration change, **the service must be restarted** to pick up an environment variable change;
there is no hot reload.

## Secrets in configuration

`ClientSecret`, `Password`, and any other credential live in cleartext in `appsettings.json` (and
are shown/editable in cleartext by the Admin TUI's `config show`/`config set` commands - a
deliberate MVP simplicity decision, not an oversight). See [docs/security.md](security.md) for the
full rationale and the guarantee that secrets are never written to logs.
