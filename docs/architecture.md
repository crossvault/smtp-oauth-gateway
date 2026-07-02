# Architecture

## Project layout

| Project | Role |
|---|---|
| `SmtpGateway.Core` | Pure domain logic: no file I/O, no network, no SQLite, no third-party mail libraries. Envelope/queue item/recipient models, the queue state machine (`QueueItemStatusResolver`), the retry/backoff schedule (`RetryPolicy`), TTL expiry rules, the sender-rewrite decision, and a pure sliding-window rate limiter. Everything here is trivially unit-testable with no fakes/mocks for infrastructure. |
| `SmtpGateway.Infrastructure` | Adapters around Core's ports: the `SmtpServer`-based inbound listener and message store, the SQLite-backed queue repository, the durable file spool, the three outbound providers (generic SMTP via MailKit, M365 SMTP OAuth, Microsoft Graph `sendMail`), MSAL token acquisition, and the bound configuration (`GatewayOptions` and friends). |
| `SmtpGateway.Service` | The composition root: a .NET Generic Host (`Microsoft.Extensions.Hosting.WindowsServices`) that binds/validates `GatewayOptions`, wires up DI, and runs two `BackgroundService`s - `InboundHostedService` (owns the SMTP listener) and `OutboundDeliveryHostedService` (drives the delivery loop). Also owns Serilog console/file logging and the filtered Windows EventLog integration.
| `SmtpGateway.Admin.Tui` | A standalone `Spectre.Console.Cli` executable (`smtpgw-admin`), not embedded in the service process. It reads the *same* `appsettings.json` and the *same* `SqliteQueueRepository`/`FileSpool` types as the service, so there is no parallel data model - just a separate process that happens to share Core/Infrastructure. |

The split exists so that (a) the retry/state-machine logic in Core can be tested with zero
infrastructure dependencies, (b) Infrastructure's adapters can be swapped or extended (e.g. a
fourth outbound provider) without touching the state machine, and (c) the Admin TUI can be shipped,
versioned, and run independently of the Windows Service process.

## Inbound flow: accept, durably persist, then 250

The `SmtpServer` package owns the wire protocol (`HELO`, `MAIL FROM`, `RCPT TO`, `DATA`, session
timeouts). Two extension points are wired into it, both in `SmtpGatewayListener`:

- `RecipientLimitMailboxFilter` (an `IMailboxFilter`) rejects individual `RCPT TO` commands once
  the per-mail recipient count exceeds `Smtp:MaxRecipients` - the running count lives in the
  session's own property bag and resets on every `MAIL FROM`.
- `SpoolingMessageStore` (an `IMessageStore`) runs once the client completes `DATA`, and is the
  only place a message is durably committed:
  1. Reject outright (`SizeLimitExceeded`) if the buffered message exceeds `Smtp:MaxMessageSizeBytes`.
  2. If `MaxSpoolBytes` is configured, sum every queue item's stored size (regardless of status -
     nothing ever deletes a spool file) and reject with `452 insufficient storage` (a *retryable*
     4yz code) if accepting this message would exceed the quota. Nothing is written to disk for a
     rejected message.
  3. Write the raw MIME to `FileSpool`: a temp file in the spool directory, flushed to disk
     (`Flush(flushToDisk: true)`), then atomically renamed into its final `{guid:N}.eml` path. The
     rename is the commit point - a final file only ever exists once its bytes are complete on
     disk, and `File.Move` refuses to silently overwrite an existing final file.
  4. Insert the `QueueItem` row and one `Recipients` row per recipient into SQLite, in the same
     logical step (see [docs/queue.md](queue.md) for the schema).
  5. Only after both the spool file and the queue row are durably committed does the store return
     `250 OK`. Any exception in steps 3-4 returns a non-success `SmtpResponse` (transaction
     failure) instead - the client sees the transaction as rejected and is expected to retry the
     whole thing; a spool-write failure after step 3 but before step 4 commits leaves an orphaned
     spool file rather than a half-registered queue item, which is deliberately treated as
     "never accepted" from the client's point of view.

The inbound listener itself never authenticates a session (`AuthenticationRequired(false)` is
hardcoded) and only ever binds to addresses `LoopbackEndpointValidator` confirms are loopback
(`127.0.0.1` / `::1`); see [docs/security.md](security.md) for the full inbound security model.

## Queue state machine

Every queued mail is one `QueueItem` with one `RecipientDelivery` row per recipient. The
*per-recipient* status (`Pending` / `Sent` / `Retryable` / `PermanentlyFailed`) is what actually
drives delivery; the *item-level* `QueueItemStatus` is either derived from the recipient set
(`QueueItemStatusResolver.Derive`) or set directly by lease/TTL/operator logic that has nothing to
do with individual recipients:

| Status | Set by | Meaning |
|---|---|---|
| `Queued` | Derived (any recipient still `Pending`), or initial insert | Waiting to be picked up, or still has at least one recipient with no final/retryable outcome yet. |
| `Leased` | `SqliteQueueRepository.TryLeaseNextAsync` (lease claim), never derived | A worker holds an active, time-boxed lease and is about to attempt delivery. |
| `Sending` | *(defined in the enum; not currently set by any code path)* | Documented as "actively submitting", but the current delivery worker goes directly from `Leased` to a recipient-derived status once the provider call returns - there is no separate in-flight "Sending" window today. |
| `PartiallySent` | Derived | At least one recipient `Sent`, and at least one other recipient still `Retryable` or `PermanentlyFailed`. |
| `Sent` | Derived | Every recipient `Sent`. Terminal; never re-leased, never expired retroactively. |
| `RetryScheduled` | Derived | Nothing sent yet, but at least one recipient is still `Retryable` (regardless of any `PermanentlyFailed` mixed in). |
| `Poison` | Derived | Every recipient `PermanentlyFailed`. Dead; requires operator action (`queue retry`, `queue discard`, or `queue export`). Never automatically re-leased. |
| `Expired` | `SqliteQueueRepository.ExpireOverdueAsync` (TTL sweep), never derived | `CreatedAtUtc + EffectiveQueueTtl` has passed. Applies to **any** non-`Sent`, non-already-`Expired` item - including `Discarded`, `Poison`, and even a currently `Leased` item - so a `Discarded` or `Poison` item's status can later be overwritten to `Expired` once the TTL elapses; this changes only the displayed label, not deliverability (both are equally excluded from leasing). |
| `Discarded` | `queue discard` (operator action only) | An administrator explicitly gave up. Never re-leased, but stays visible in history (queue rows are never deleted). |

Leasing (`TryLeaseNextAsync`) is a single `UPDATE ... WHERE Id = (SELECT ... LIMIT 1) RETURNING Id`
statement against an explicit status whitelist (`Queued`, `RetryScheduled`, or `PartiallySent` with
at least one `Retryable` recipient) with `NextAttemptUtc IS NULL OR NextAttemptUtc <= now`, so two
concurrent leasers can never claim the same row (SQLite serializes writers).

## Outbound providers

Exactly one provider is active at a time, selected by `OutboundProvider:Provider`:

- **`GenericSmtp`** (`GenericSmtpProvider`, MailKit) - connects to any SMTP relay you configure
  (host/port/TLS mode/auth). Use this for an on-premises relay, a third-party ESP's SMTP endpoint,
  or any provider that isn't Microsoft 365/Graph. Supports per-recipient outcomes: MailKit's
  `OnRecipientAccepted`/`OnRecipientNotAccepted` hooks are overridden so one rejected `RCPT TO`
  doesn't abort delivery to the other, accepted recipients.
- **`M365Oauth`** - also `GenericSmtpProvider` under the hood, but hardcoded to
  `smtp.office365.com:587` with `StartTlsRequired` and SASL XOAUTH2 authentication via an MSAL
  client-credentials token (scope `https://outlook.office365.com/.default`). Use this for
  Microsoft 365 when SMTP AUTH is allowed for the tenant/mailbox; see
  [docs/microsoft365-setup.md](microsoft365-setup.md).
- **`Graph`** (`GraphSendMailProvider`) - Microsoft Graph's raw-MIME `sendMail` endpoint in a single
  call (`POST /users/{mailbox}/sendMail` with the base64-encoded MIME as the body), authenticated with
  an MSAL client-credentials token (scope `https://graph.microsoft.com/.default`). This path needs only
  the low-privilege `Mail.Send` application permission. Use this when SMTP AUTH is blocked for the
  tenant. Has **no per-recipient granularity** - the whole envelope gets one outcome - and a
  `202 Accepted` is Graph's own acceptance signal, not final-delivery confirmation.
  See [docs/microsoft-graph-setup.md](microsoft-graph-setup.md).

All three report a per-recipient `SubmitOutcome` (`Success` / `RetryableFailure` /
`PermanentFailure`, with an optional server-provided `RetryAfter` hint such as Graph's 429
`Retry-After` header) back to `IOutboundProvider.Submit`.

## Delivery worker: lease, retry, backoff

`OutboundDeliveryWorker.ProcessNextAsync` is the single unit of outbound work, driven in a loop by
`OutboundDeliveryHostedService`:

1. If an outbound rate limit is configured and its rolling window is exhausted, return
   immediately without leasing anything (see below).
2. Lease the next eligible item (`TryLeaseNextAsync`), read its raw MIME from the spool, and apply
   the sender-rewrite policy if `SenderRewriteAddress` is configured (rewrites the MIME `From:`
   header in memory only - the spool file and queue row's stored envelope are never touched).
3. Submit only the recipients that still need an attempt (`Pending`/`Retryable` - not
   already-`Sent` ones, so a re-leased `PartiallySent` item never resends to a recipient it already
   delivered to) to the configured `IOutboundProvider`.
4. Persist each recipient's outcome, re-derive the item's overall status, and - if any recipient
   is still retryable - schedule the next attempt via `RetryPolicy.GetDelay(attemptCount)`: 1
   minute, 5 minutes, 15 minutes, then hourly for every attempt after that, or the provider's own
   `RetryAfter` hint if it's larger.

The hosted service loops immediately while there is work, and waits `DeliveryPollInterval` (default
5s) once the queue is empty. On a cadence of `TtlSweepInterval` (default 15 minutes) it also
re-releases any lease that has expired mid-run (protecting against a crashed/killed process
stranding an item as `Leased`) and runs the TTL-expiry sweep. Expired leases are also released once,
unconditionally, at service startup.

## Backpressure and rate limiting (Phase 6 hardening)

Both are **optional and unconfigured by default** (fully unlimited unless explicitly turned on):

- **Disk-quota backpressure** (`Gateway:MaxSpoolBytes`): once the sum of every stored queue item's
  size would exceed this, new inbound mail is rejected at `DATA` time with a retryable `452`
  response - inbound never blocks or degrades service availability, it simply asks the legacy
  client to try again later. This protects the host filesystem from being filled by a queue that
  can't drain (e.g. a persistently broken outbound provider).
- **Outbound rate limiting** (`Gateway:OutboundRateLimitPerMinute`): a pure, deterministic
  sliding-window limiter (`SlidingWindowRateLimiter`) caps how many provider submissions the
  delivery loop makes per rolling minute. When exhausted, `ProcessNextAsync` simply returns
  `false` without leasing anything, and the hosted service's normal idle-poll wait
  (`DeliveryPollInterval`) naturally defers the next attempt - no separate timer is needed. Use
  this to stay under a provider's own sending-rate limits (e.g. Exchange Online throttling).
