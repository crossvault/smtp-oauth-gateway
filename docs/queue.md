# Queue and spool design

## Storage: SQLite queue + file spool

Every accepted mail is stored in two places, committed in this order (see
[docs/architecture.md](architecture.md#inbound-flow-accept-durably-persist-then-250) for the full
inbound sequence):

1. **File spool** (`FileSpool`, rooted at `Gateway:SpoolDirectory`): the raw MIME bytes, written to
   a temp file, flushed to disk, then atomically renamed to `{queueItemId:N}.eml`. The rename is
   the commit point.
2. **SQLite queue** (`SqliteQueueRepository`, at `Gateway:QueueDatabasePath`): a `QueueItems` table
   (one row per mail: id, mail-from, spool path, SHA-256 hash, size, timestamps, attempt count,
   next-attempt time, lease owner/expiry, last error, overall status) and a `Recipients` table
   (one row per recipient of that mail: address, per-recipient status, attempt count, last error),
   linked by `QueueItemId` with `ON DELETE CASCADE`.

The database is opened with `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout=5000` on every
connection, so the Windows Service and the Admin TUI (a separate process) can safely read/write the
same database file concurrently without a "database is locked" failure under normal contention.

## Lease-based claiming

`SqliteQueueRepository.TryLeaseNextAsync` claims exactly one eligible item per call, atomically, via
a single `UPDATE ... WHERE Id = (SELECT ... LIMIT 1) RETURNING Id` statement - SQLite serializes
writers, so two concurrent callers can never claim the same row. Eligible means: status is
`Queued` or `RetryScheduled`, **or** status is `PartiallySent` with at least one recipient still
`Retryable` (a `PartiallySent` item with no retryable recipients left is terminal and must never be
re-leased) - and `NextAttemptUtc` is null or already in the past. The claimed row is set to
`Leased` with a lease owner and a `LeaseExpiryUtc` of now + `Gateway:LeaseDuration` (default 5
minutes).

If a process crashes or is killed mid-delivery, a `Leased` item's lease eventually passes its
expiry. `ReleaseExpiredLeasesAsync` resets any such item back to `Queued`; this runs once
unconditionally at service startup, and again on every `TtlSweepInterval` cadence while running, so
a stranded lease is recovered without operator intervention within one sweep interval.

## Retry/backoff schedule (`RetryPolicy`)

Once a delivery attempt leaves at least one recipient `Retryable`, the next attempt is scheduled
`RetryPolicy.GetDelay(attemptCount)` after the current attempt, following a fixed staged schedule
(no jitter, no exponential formula):

| Attempt # | Delay before this attempt |
|---|---|
| 1 | 1 minute |
| 2 | 5 minutes |
| 3 | 15 minutes |
| 4 and every attempt after | 1 hour |

If the provider itself supplies a retry-after hint (e.g. Graph's `429` `Retry-After` header) and
that hint is larger than the staged delay, the hint takes priority for that attempt.

## Queue TTL and expiry

`Gateway:QueueTtl` (default and hard maximum: 5 days - `RetryPolicy.DefaultTtl` /
`RetryPolicy.MaxTtl`) is the time-to-live measured from `CreatedAtUtc`. `EffectiveQueueTtl` applies
`RetryPolicy.ValidateTtl`, which only caps a configured value **above** 5 days down to 5 days -
there is no lower-bound check, so an operator-configured zero or negative TTL is not rejected and
will cause items to expire essentially immediately.

`ExpireOverdueAsync` runs on the `TtlSweepInterval` cadence (default 15 minutes) and transitions
every item whose `CreatedAtUtc + EffectiveQueueTtl` has passed to `Expired`, as a single `UPDATE`
statement with the condition `Status NOT IN ('Sent', 'Expired')`. Note that this condition excludes
only `Sent` and `Expired` - **not** `Discarded`, `Poison`, or even a currently `Leased` item - so a
`Discarded` or `Poison` item's displayed status is later overwritten to `Expired` once its TTL
elapses too. This does not change deliverability (both `Discarded`/`Poison` and `Expired` items are
equally excluded from leasing), only the label an operator sees in `queue list`/`queue show`.
Expired items, like `Poison` and `Discarded` items, are never deleted - they remain visible in queue
history.

## Per-recipient status vs. overall item status

Delivery outcomes are tracked **per recipient** (`RecipientStatus`: `Pending`, `Sent`, `Retryable`,
`PermanentlyFailed`), because a single mail can have some recipients accepted and others rejected
by the same provider call. The overall `QueueItemStatus` is *derived* from the set of recipient
statuses by `QueueItemStatusResolver.Derive` (see [docs/architecture.md](architecture.md#queue-state-machine)
for the full state table):

- **`PartiallySent`**: at least one recipient `Sent`, and at least one other recipient still
  `Retryable` or `PermanentlyFailed`. A re-leased `PartiallySent` item only resubmits to its
  `Pending`/`Retryable` recipients - already-`Sent` ones are never resent.
- **`Poison`**: every recipient `PermanentlyFailed` (no pending, no sent, no retryable left). This
  is a dead end for automated delivery - the item is never re-leased - and requires operator
  action:
  - `queue retry <id>` resets every non-`Sent` recipient to `Retryable` with a fresh attempt count,
    resets the item-level attempt count to 0 so the backoff cadence restarts from the beginning
    (1min/5min/15min) rather than continuing at the mature hourly delay, and clears the next-attempt
    time, making the item immediately eligible again. Already-`Sent` recipients are left alone.
  - `queue discard <id>` marks the item `Discarded`, permanently excluding it from further leasing
    while keeping it visible in history.
  - `queue export <id>` writes the item's raw MIME to `exports/<id>.eml` so an operator can inspect
    or manually redeliver the message content outside the gateway.
- **`Discarded`**: set only by the explicit `queue discard` operator action, never derived from
  recipient state. Discarded items remain visible in `queue list --status Discarded` /
  `queue show`, but `TryLeaseNextAsync` never matches them.

## Limitations

- **At-least-once delivery; duplicates are possible.** If a delivery attempt fails ambiguously
  after the `DATA` phase has already been sent to the provider (e.g. the connection drops after the
  provider accepted the message but before the gateway received a definitive response), the
  recipient may be marked `Retryable` and resent on the next attempt even though the provider
  already delivered the first copy. The gateway does not implement provider-side idempotency
  keys/deduplication - this is an inherent risk of at-least-once delivery over SMTP/HTTP, not a bug
  to be fixed later.
- **Microsoft Graph has no per-recipient granularity.** A single `Submit` call's outcome (success,
  retryable, or permanent) is applied uniformly to every recipient in the envelope - Graph's
  `sendMail` API has no concept of "accept some recipients, reject others." If granular
  per-recipient retry/failure behavior matters, use the `GenericSmtp` or `M365Oauth` provider
  instead (see [docs/microsoft-graph-setup.md](microsoft-graph-setup.md#limitations)).
- **Exchange Online sending limits.** Both the M365 SMTP OAuth and Graph providers ultimately send
  through Exchange Online, which is not a bulk-mail system; sustained high-volume sending through a
  single mailbox can hit Microsoft 365 sending limits regardless of correct OAuth configuration
  (see [docs/microsoft365-setup.md](microsoft365-setup.md#known-limitations)).
- **The optional outbound rate limit** (`Gateway:OutboundRateLimitPerMinute`) exists specifically to
  let an operator stay comfortably under a provider's own sending-rate limit (e.g. Exchange
  Online's), trading throughput for headroom before the provider itself starts throttling or
  flagging the sending pattern - it is not a general-purpose traffic-shaping feature and is
  unconfigured (unlimited) by default.
