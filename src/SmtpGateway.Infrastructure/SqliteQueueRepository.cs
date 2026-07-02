using System.Globalization;
using Microsoft.Data.Sqlite;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// SQLite-backed persistence for <see cref="QueueItem"/> rows and their per-recipient
/// delivery state. Creates the schema on first use and runs in WAL mode so the service and a
/// future TUI can share the same database file concurrently.
/// </summary>
public sealed class SqliteQueueRepository
{
    private const string DateTimeFormat = "o";

    private readonly string _connectionString;

    public SqliteQueueRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString;
        InitializeSchema();
    }

    public async Task EnqueueAsync(QueueItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO QueueItems
                    (Id, MailFrom, MimePath, Hash, SizeBytes, CreatedAtUtc, UpdatedAtUtc,
                     AttemptCount, NextAttemptUtc, LeaseOwner, LeaseExpiryUtc, LastError, Status)
                VALUES
                    (@Id, @MailFrom, @MimePath, @Hash, @SizeBytes, @CreatedAtUtc, @UpdatedAtUtc,
                     @AttemptCount, @NextAttemptUtc, @LeaseOwner, @LeaseExpiryUtc, @LastError, @Status);
                """;
            command.Parameters.AddWithValue("@Id", item.Id.ToString());
            command.Parameters.AddWithValue("@MailFrom", item.Envelope.MailFrom);
            command.Parameters.AddWithValue("@MimePath", item.MimePath);
            command.Parameters.AddWithValue("@Hash", item.Hash);
            command.Parameters.AddWithValue("@SizeBytes", item.SizeBytes);
            command.Parameters.AddWithValue("@CreatedAtUtc", Format(item.CreatedAtUtc));
            command.Parameters.AddWithValue("@UpdatedAtUtc", Format(item.UpdatedAtUtc));
            command.Parameters.AddWithValue("@AttemptCount", item.AttemptCount);
            command.Parameters.AddWithValue("@NextAttemptUtc", (object?)FormatNullable(item.NextAttemptUtc) ?? DBNull.Value);
            command.Parameters.AddWithValue("@LeaseOwner", (object?)item.LeaseOwner ?? DBNull.Value);
            command.Parameters.AddWithValue("@LeaseExpiryUtc", (object?)FormatNullable(item.LeaseExpiryUtc) ?? DBNull.Value);
            command.Parameters.AddWithValue("@LastError", (object?)item.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", item.Status.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < item.Recipients.Count; i++)
        {
            var recipient = item.Recipients[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Recipients (QueueItemId, OrdinalIndex, Address, Status, AttemptCount, LastError)
                VALUES (@QueueItemId, @OrdinalIndex, @Address, @Status, @AttemptCount, @LastError);
                """;
            command.Parameters.AddWithValue("@QueueItemId", item.Id.ToString());
            command.Parameters.AddWithValue("@OrdinalIndex", i);
            command.Parameters.AddWithValue("@Address", recipient.Address);
            command.Parameters.AddWithValue("@Status", recipient.Status.ToString());
            command.Parameters.AddWithValue("@AttemptCount", recipient.AttemptCount);
            command.Parameters.AddWithValue("@LastError", (object?)recipient.LastError ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims one Queued/RetryScheduled item whose NextAttemptUtc has passed (or is
    /// null) and marks it Leased. The claim happens in a single UPDATE statement with a
    /// row-selecting subquery, so concurrent callers can never double-lease the same item:
    /// SQLite serializes writers, and once one caller flips the row to Leased, the other's
    /// subquery no longer matches it.
    /// </summary>
    public async Task<QueueItem?> TryLeaseNextAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now + leaseDuration;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        Guid? claimedId = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE QueueItems
                SET Status = 'Leased', LeaseOwner = @LeaseOwner, LeaseExpiryUtc = @LeaseExpiryUtc, UpdatedAtUtc = @Now
                WHERE Id = (
                    SELECT Id FROM QueueItems
                    WHERE (
                        -- Explicit whitelist, not a catch-all: Discarded (and Leased/Sending/
                        -- Sent/Poison/Expired) items never match, so an administrator-discarded
                        -- item is never re-leased for delivery.
                        Status = 'Queued'
                        OR Status = 'RetryScheduled'
                        OR (
                            -- A partial-success item (some recipients already Sent) still has
                            -- automated retry work left only if at least one recipient is
                            -- Retryable; once the rest are permanently failed it is terminal
                            -- and must not be re-leased.
                            Status = 'PartiallySent'
                            AND EXISTS (
                                SELECT 1 FROM Recipients
                                WHERE Recipients.QueueItemId = QueueItems.Id AND Recipients.Status = 'Retryable'
                            )
                        )
                      )
                      AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= @Now)
                    ORDER BY CreatedAtUtc
                    LIMIT 1
                )
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("@LeaseOwner", leaseOwner);
            command.Parameters.AddWithValue("@LeaseExpiryUtc", Format(leaseExpiry));
            command.Parameters.AddWithValue("@Now", Format(now));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                claimedId = Guid.Parse(reader.GetString(0));
            }
        }

        return claimedId is null ? null : await GetByIdAsync(connection, null, claimedId.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only check for whether <see cref="TryLeaseNextAsync"/> would currently find an item to
    /// claim, using the identical eligibility filter but a plain SELECT (no mutation, no lease
    /// taken). Lets a caller with an optional rate limit decide whether there is real work before
    /// spending a rate-limit token on an empty queue.
    /// </summary>
    public async Task<bool> HasEligibleItemAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM QueueItems
                WHERE (
                    Status = 'Queued'
                    OR Status = 'RetryScheduled'
                    OR (
                        Status = 'PartiallySent'
                        AND EXISTS (
                            SELECT 1 FROM Recipients
                            WHERE Recipients.QueueItemId = QueueItems.Id AND Recipients.Status = 'Retryable'
                        )
                    )
                  )
                  AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= @Now)
            );
            """;
        command.Parameters.AddWithValue("@Now", Format(now));
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    public async Task UpdateRecipientStatusAsync(
        Guid queueItemId,
        string address,
        RecipientStatus status,
        int attemptCount,
        string? lastError,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Recipients
                SET Status = @Status, AttemptCount = @AttemptCount, LastError = @LastError
                WHERE QueueItemId = @QueueItemId AND Address = @Address;
                """;
            command.Parameters.AddWithValue("@Status", status.ToString());
            command.Parameters.AddWithValue("@AttemptCount", attemptCount);
            command.Parameters.AddWithValue("@LastError", (object?)lastError ?? DBNull.Value);
            command.Parameters.AddWithValue("@QueueItemId", queueItemId.ToString());
            command.Parameters.AddWithValue("@Address", address);
            var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                throw new InvalidOperationException(
                    $"No recipient '{address}' found for queue item '{queueItemId}'.");
            }
        }

        var recipients = await LoadRecipientsAsync(connection, transaction, queueItemId, ct).ConfigureAwait(false);
        var newStatus = QueueItemStatusResolver.Derive(recipients);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // State guard: only re-derive the overall status of an item that is still under
            // automated control. An administrator can Discard (or the TTL sweep can Expire) an
            // item while the worker holds its lease and a network Submit is in flight on the same
            // shared WAL database; without this guard the worker's unconditional post-attempt
            // re-derivation would silently resurrect that item (e.g. Discarded -> RetryScheduled)
            // and it would be delivered on a later attempt. The recipient-row history above is
            // still recorded either way. For the normal Leased/Queued item path this WHERE clause
            // always matches, so behavior is unchanged.
            command.CommandText =
                """
                UPDATE QueueItems SET Status = @Status, UpdatedAtUtc = @UpdatedAtUtc
                WHERE Id = @Id AND Status NOT IN ('Discarded', 'Expired');
                """;
            command.Parameters.AddWithValue("@Status", newStatus.ToString());
            command.Parameters.AddWithValue("@UpdatedAtUtc", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@Id", queueItemId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Resets any item whose lease has expired back to Queued. Used at service startup for lease recovery.</summary>
    public async Task<int> ReleaseExpiredLeasesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueItems
            SET Status = 'Queued', LeaseOwner = NULL, LeaseExpiryUtc = NULL, UpdatedAtUtc = @Now
            WHERE Status = 'Leased' AND LeaseExpiryUtc IS NOT NULL AND LeaseExpiryUtc < @Now;
            """;
        command.Parameters.AddWithValue("@Now", Format(now));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Transitions every non-Sent, non-already-Expired item whose <c>CreatedAtUtc + effectiveTtl</c>
    /// has passed (per <see cref="QueueItemExpiryPolicy.IsExpired"/>) to <see cref="QueueItemStatus.Expired"/>.
    /// Sent items are done and never expire retroactively. Runs as a single UPDATE statement.
    /// </summary>
    /// <returns>The number of items transitioned to Expired.</returns>
    public async Task<int> ExpireOverdueAsync(TimeSpan effectiveTtl, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - effectiveTtl;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueItems
            SET Status = 'Expired', UpdatedAtUtc = @Now
            WHERE Status NOT IN ('Sent', 'Expired') AND CreatedAtUtc <= @Cutoff;
            """;
        command.Parameters.AddWithValue("@Now", Format(now));
        command.Parameters.AddWithValue("@Cutoff", Format(cutoff));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a queue item's overall <see cref="QueueItem.AttemptCount"/> and
    /// <see cref="QueueItem.NextAttemptUtc"/> directly, without touching recipient rows or the
    /// derived <see cref="QueueItemStatus"/>. Used by the outbound delivery worker to record a
    /// single item-level retry schedule after a delivery attempt leaves at least one recipient
    /// retryable, rather than per-recipient attempt counts blowing up the backoff schedule.
    /// </summary>
    public async Task SetNextAttemptAsync(
        Guid queueItemId, int attemptCount, DateTimeOffset nextAttemptUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // State guard (see UpdateRecipientStatusAsync): never schedule a further attempt for an
        // item an administrator has Discarded or the TTL sweep has Expired out from under an
        // in-flight lease. For a normal Leased item this WHERE clause always matches.
        command.CommandText =
            """
            UPDATE QueueItems SET AttemptCount = @AttemptCount, NextAttemptUtc = @NextAttemptUtc, UpdatedAtUtc = @Now
            WHERE Id = @Id AND Status NOT IN ('Discarded', 'Expired');
            """;
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@NextAttemptUtc", Format(nextAttemptUtc));
        command.Parameters.AddWithValue("@Now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@Id", queueItemId.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit administrator action: marks an item as <see cref="QueueItemStatus.Discarded"/>
    /// without touching recipient rows. Discarded items remain readable via
    /// <see cref="GetByIdAsync(Guid, CancellationToken)"/>/<see cref="ListAsync"/> (queue history
    /// is never deleted), but <see cref="TryLeaseNextAsync"/> never matches Discarded, so no
    /// further delivery attempts are made.
    /// </summary>
    public async Task DiscardAsync(Guid queueItemId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueItems SET Status = 'Discarded', UpdatedAtUtc = @Now WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@Id", queueItemId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException($"Queue item '{queueItemId}' was not found.");
        }
    }

    /// <summary>
    /// Explicit administrator action: resets every recipient that is not already
    /// <see cref="RecipientStatus.Sent"/> back to <see cref="RecipientStatus.Retryable"/> with a
    /// fresh attempt count (this is a manual retry, not a continuation of the automatic backoff
    /// schedule), resets the item-level <see cref="QueueItem.AttemptCount"/> to 0 so the backoff
    /// cadence that drives <see cref="RetryPolicy.GetDelay"/> also restarts from the beginning
    /// (1min/5min/15min) rather than jumping straight to the mature hourly delay, clears the
    /// item's <see cref="QueueItem.NextAttemptUtc"/> so it is immediately eligible, and recomputes
    /// the overall status via <see cref="QueueItemStatusResolver"/> exactly like
    /// <see cref="UpdateRecipientStatusAsync"/> does. Already-Sent recipients (e.g. on a
    /// PartiallySent item) are left untouched and are never resent.
    /// </summary>
    public async Task RetryAsync(Guid queueItemId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Recipients
                SET Status = 'Retryable', AttemptCount = 0, LastError = NULL
                WHERE QueueItemId = @QueueItemId AND Status <> 'Sent';
                """;
            command.Parameters.AddWithValue("@QueueItemId", queueItemId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var recipients = await LoadRecipientsAsync(connection, transaction, queueItemId, ct).ConfigureAwait(false);
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException($"Queue item '{queueItemId}' was not found.");
        }

        var newStatus = QueueItemStatusResolver.Derive(recipients);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE QueueItems
                SET Status = @Status, AttemptCount = 0, NextAttemptUtc = NULL, UpdatedAtUtc = @UpdatedAtUtc
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Status", newStatus.ToString());
            command.Parameters.AddWithValue("@UpdatedAtUtc", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@Id", queueItemId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sums <see cref="QueueItem.SizeBytes"/> across every queue item regardless of
    /// <see cref="QueueItemStatus"/> in a single SQL aggregate, since nothing ever deletes a
    /// spool file - a Sent/Poison/Expired/Discarded item's bytes stay on disk forever and still
    /// count toward total spool footprint. Used by <see cref="SpoolingMessageStore"/> to enforce
    /// disk-quota backpressure.
    /// </summary>
    public async Task<long> GetTotalSpoolBytesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(SizeBytes), 0) FROM QueueItems;";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<QueueItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetByIdAsync(connection, null, id, ct).ConfigureAwait(false);
    }

    /// <summary>Lists every queue item. No filtering/paging - sufficient for tests, not the full future TUI query surface.</summary>
    public async Task<IReadOnlyList<QueueItem>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id FROM QueueItems ORDER BY CreatedAtUtc;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var items = new List<QueueItem>(ids.Count);
        foreach (var id in ids)
        {
            var item = await GetByIdAsync(connection, null, id, ct).ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static async Task<QueueItem?> GetByIdAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken ct)
    {
        QueueItemRow? row = null;
        await using (var command = connection.CreateCommand())
        {
            if (transaction is not null)
            {
                command.Transaction = transaction;
            }

            command.CommandText =
                """
                SELECT Id, MailFrom, MimePath, Hash, SizeBytes, CreatedAtUtc, UpdatedAtUtc,
                       AttemptCount, NextAttemptUtc, LeaseOwner, LeaseExpiryUtc, LastError, Status
                FROM QueueItems WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", id.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row = ReadQueueItemRow(reader);
            }
        }

        if (row is null)
        {
            return null;
        }

        var recipients = await LoadRecipientsAsync(connection, transaction, id, ct).ConfigureAwait(false);
        return BuildQueueItem(row, recipients);
    }

    private static async Task<List<RecipientDelivery>> LoadRecipientsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid queueItemId, CancellationToken ct)
    {
        var recipients = new List<RecipientDelivery>();
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText =
            """
            SELECT Address, Status, AttemptCount, LastError
            FROM Recipients
            WHERE QueueItemId = @QueueItemId
            ORDER BY OrdinalIndex;
            """;
        command.Parameters.AddWithValue("@QueueItemId", queueItemId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var address = reader.GetString(0);
            var status = Enum.Parse<RecipientStatus>(reader.GetString(1));
            var attemptCount = reader.GetInt32(2);
            var lastError = reader.IsDBNull(3) ? null : reader.GetString(3);
            recipients.Add(new RecipientDelivery(address, status, attemptCount, lastError));
        }

        return recipients;
    }

    private static QueueItem BuildQueueItem(QueueItemRow row, List<RecipientDelivery> recipients)
    {
        var envelope = new Envelope(row.MailFrom, recipients.Select(r => r.Address));
        return new QueueItem
        {
            Id = row.Id,
            Envelope = envelope,
            Recipients = recipients,
            MimePath = row.MimePath,
            Hash = row.Hash,
            SizeBytes = row.SizeBytes,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
            AttemptCount = row.AttemptCount,
            NextAttemptUtc = row.NextAttemptUtc,
            LeaseOwner = row.LeaseOwner,
            LeaseExpiryUtc = row.LeaseExpiryUtc,
            LastError = row.LastError,
            Status = row.Status,
        };
    }

    private static QueueItemRow ReadQueueItemRow(SqliteDataReader reader) =>
        new(
            Id: Guid.Parse(reader.GetString(0)),
            MailFrom: reader.GetString(1),
            MimePath: reader.GetString(2),
            Hash: reader.GetString(3),
            SizeBytes: reader.GetInt64(4),
            CreatedAtUtc: ParseDateTimeOffset(reader.GetString(5)),
            UpdatedAtUtc: ParseDateTimeOffset(reader.GetString(6)),
            AttemptCount: reader.GetInt32(7),
            NextAttemptUtc: reader.IsDBNull(8) ? null : ParseDateTimeOffset(reader.GetString(8)),
            LeaseOwner: reader.IsDBNull(9) ? null : reader.GetString(9),
            LeaseExpiryUtc: reader.IsDBNull(10) ? null : ParseDateTimeOffset(reader.GetString(10)),
            LastError: reader.IsDBNull(11) ? null : reader.GetString(11),
            Status: Enum.Parse<QueueItemStatus>(reader.GetString(12)));

    private void InitializeSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS QueueItems (
                Id TEXT PRIMARY KEY,
                MailFrom TEXT NOT NULL,
                MimePath TEXT NOT NULL,
                Hash TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL,
                NextAttemptUtc TEXT NULL,
                LeaseOwner TEXT NULL,
                LeaseExpiryUtc TEXT NULL,
                LastError TEXT NULL,
                Status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Recipients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QueueItemId TEXT NOT NULL REFERENCES QueueItems (Id) ON DELETE CASCADE,
                OrdinalIndex INTEGER NOT NULL,
                Address TEXT NOT NULL,
                Status TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL,
                LastError TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Recipients_QueueItemId ON Recipients (QueueItemId);
            CREATE INDEX IF NOT EXISTS IX_QueueItems_Status ON QueueItems (Status, NextAttemptUtc);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ApplyPragmasAsync(connection, ct).ConfigureAwait(false);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    private static string? FormatNullable(DateTimeOffset? value) =>
        value?.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.ParseExact(value, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record QueueItemRow(
        Guid Id,
        string MailFrom,
        string MimePath,
        string Hash,
        long SizeBytes,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        int AttemptCount,
        DateTimeOffset? NextAttemptUtc,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiryUtc,
        string? LastError,
        QueueItemStatus Status);
}
