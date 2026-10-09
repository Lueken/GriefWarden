using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace GriefWarden;

/// <summary>
/// Age-based retention, tiered by evidentiary value, with a size ceiling as a backstop.
///
/// GriefWarden shipped with four insert-only tables and nothing that ever deleted a row, so
/// the database grew without bound. Measured on The Quire at roughly 2,000 rows and 390 KB a
/// day with a handful of players; a busy 16-slot server projects to well over a gigabyte a
/// year.
///
/// Age is the policy and size is only the backstop, deliberately. Retention should be a
/// promise about coverage ("anything in the last 90 days can be investigated"), not about
/// disk. A size-first policy silently shortens the investigation window exactly when the
/// server is busiest, which is when griefing is most likely: the wrong failure mode to build
/// in on purpose.
/// </summary>
public class Retention {
    // Database.ActionType: BROKE, KILLED, TAKEN. What an investigation is actually made of.
    private static readonly int[] Destructive = {
        (int)Database.ActionType.BROKE,
        (int)Database.ActionType.KILLED,
        (int)Database.ActionType.TAKEN,
    };

    // Tables where the cutoff depends on what the row records: a BROKE is evidence, a
    // PLACED is context, and they sit side by side in the same table.
    private static readonly string[] Tiered = { "blocklogs", "entitylogs", "containerlogs" };

    // Tables where every row is the same class of thing, so the whole table shares one
    // cutoff. Sessions ride the evidence window because presence is what pairs with an old
    // break.
    private static readonly string[] WholeTable = { "sessions", "containersnapshots" };

    // Everything, for the size backstop only.
    private static readonly string[] AllTables = {
        "blocklogs", "entitylogs", "containerlogs", "sessions", "containersnapshots"
    };

    private readonly GriefWardenConfig config;
    private bool running;

    public Retention(GriefWardenConfig config) {
        this.config = config;

        if (!config.RetentionEnabled) {
            Main.API.Logger.Notification("GriefWarden: retention disabled by config; the log will grow without bound.");
            return;
        }

        // Not at boot. Startup is already the busiest moment for disk, and nothing about
        // retention is urgent enough to compete with chunk loading.
        Main.API.Event.RegisterCallback(_ => Run("startup"), 120000);
        Main.API.Event.RegisterGameTickListener(_ => Run("scheduled"), config.PruneIntervalHours * 3600 * 1000);

        Main.API.Logger.Notification(
            $"GriefWarden: retention on. Destructive+presence {config.DestructiveRetentionDays}d, context {config.ContextRetentionDays}d, ceiling {config.MaxDatabaseMb} MB."
        );
    }

    /// <summary>Queue a prune. Safe to call from a command; overlapping runs are refused.</summary>
    public void Run(string trigger) {
        if (!config.RetentionEnabled) return;
        if (running) {
            Main.API.Logger.Notification("GriefWarden: a prune is already running, skipping " + trigger);
            return;
        }
        running = true;
        Main.Database.EnqueueMaintenance(connection => {
            try {
                Prune(connection, trigger);
            }
            catch (Exception ex) {
                Main.API.Logger.Error("GriefWarden: retention pass failed: " + ex);
            }
            finally {
                running = false;
            }
        });
    }

    private void Prune(SqliteConnection connection, string trigger) {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long destructiveCutoff = now - (long)config.DestructiveRetentionDays * 86400;
        long contextCutoff = now - (long)config.ContextRetentionDays * 86400;

        var deleted = new Dictionary<string, int>();
        int total = 0;

        foreach (string table in Tiered) {
            // Two passes per table because the cutoff depends on what the row records, not
            // on which table it landed in. Container TAKEN is evidence; container PLACED is
            // someone tidying their own chest.
            total += deleted[table + ":destructive"] =
                DeleteOlderThan(connection, table, destructiveCutoff, Destructive, include: true);
            total += deleted[table + ":context"] =
                DeleteOlderThan(connection, table, contextCutoff, Destructive, include: false);
        }

        foreach (string table in WholeTable) {
            total += deleted[table] = DeleteAllOlderThan(connection, table, destructiveCutoff);
        }

        if (total > 0) {
            var parts = new List<string>();
            foreach (var kv in deleted) {
                if (kv.Value > 0) parts.Add($"{kv.Key} {kv.Value}");
            }
            Main.API.Logger.Notification($"GriefWarden: retention ({trigger}) removed {total} rows: {string.Join(", ", parts)}");
        }

        EnforceSizeCeiling(connection, trigger);

        if (config.VacuumAfterPrune && total > 0) Vacuum(connection, trigger);
    }

    /// <summary>
    /// Delete rows older than a cutoff, either only the listed action types or everything
    /// except them.
    ///
    /// Batched via a subquery on the primary key rather than DELETE ... LIMIT, because that
    /// syntax needs SQLITE_ENABLE_UPDATE_DELETE_LIMIT at compile time and the bundled
    /// SQLite generally does not have it. The subquery form works everywhere and uses the
    /// timestamp index either way.
    /// </summary>
    private int DeleteOlderThan(SqliteConnection connection, string table, long cutoff, int[] actions, bool include) {
        string op = include ? "IN" : "NOT IN";
        string list = string.Join(",", actions);
        int removed = 0;

        while (true) {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $@"DELETE FROM {table} WHERE id IN (
                     SELECT id FROM {table}
                      WHERE timestamp_utc < $cutoff AND actiontype {op} ({list})
                      LIMIT $batch
                   )";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$batch", config.PruneBatchSize);

            int n = cmd.ExecuteNonQuery();
            removed += n;

            // A short batch means the table is drained. Yield between full batches so a
            // large first prune does not monopolise the writer for minutes.
            if (n < config.PruneBatchSize) break;
        }
        return removed;
    }

    /// <summary>
    /// Delete rows older than a cutoff regardless of action type, for tables where every
    /// row is the same class of thing. Batched for the same reason as the tiered delete: an
    /// unbounded DELETE holds the write lock long enough to stall logging.
    /// </summary>
    private int DeleteAllOlderThan(SqliteConnection connection, string table, long cutoff) {
        int removed = 0;

        while (true) {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $@"DELETE FROM {table} WHERE id IN (
                     SELECT id FROM {table} WHERE timestamp_utc < $cutoff LIMIT $batch
                   )";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$batch", config.PruneBatchSize);

            int n = cmd.ExecuteNonQuery();
            removed += n;
            if (n < config.PruneBatchSize) break;
        }
        return removed;
    }

    /// <summary>
    /// The backstop. If age-based pruning left the file over the ceiling, drop the oldest
    /// rows regardless of class until it fits.
    ///
    /// Context goes before evidence, and within each the oldest goes first, so the thing
    /// lost last is the most recent destructive event.
    /// </summary>
    private void EnforceSizeCeiling(SqliteConnection connection, string trigger) {
        long ceiling = (long)config.MaxDatabaseMb * 1024 * 1024;
        long size = Main.Database.FileSizeBytes();
        if (size <= ceiling) return;

        Main.API.Logger.Warning(
            $"GriefWarden: database is {size / 1048576} MB, over the {config.MaxDatabaseMb} MB ceiling after age pruning. " +
            "The retention window is too long for this server's activity; trimming oldest rows to fit."
        );

        // Bounded so a wrong ceiling cannot spin here forever deleting the whole log.
        for (int round = 0; round < 40 && Main.Database.FileSizeBytes() > ceiling; round++) {
            int removed = 0;
            foreach (bool destructive in new[] { false, true }) {
                foreach (string table in AllTables) {
                    removed += DeleteOldestAny(connection, table, destructive);
                }
            }
            if (removed == 0) break; // nothing left to give
        }

        // Only here does VACUUM earn its cost: the point of this branch is reclaiming disk,
        // and without it the file never shrinks and the ceiling stays breached forever.
        Vacuum(connection, trigger);
    }

    private int DeleteOldestAny(SqliteConnection connection, string table, bool destructive) {
        string op = destructive ? "IN" : "NOT IN";
        string list = string.Join(",", Destructive);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $@"DELETE FROM {table} WHERE id IN (
                 SELECT id FROM {table} WHERE actiontype {op} ({list})
                  ORDER BY timestamp_utc ASC LIMIT $batch
               )";
        cmd.Parameters.AddWithValue("$batch", config.PruneBatchSize);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rewrites the database to reclaim free pages. Blocking, needs roughly double the file
    /// size in scratch space, and is the reason this is not on by default.
    /// </summary>
    public void Vacuum(SqliteConnection connection, string trigger) {
        try {
            long before = Main.Database.FileSizeBytes();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
            long after = Main.Database.FileSizeBytes();
            Main.API.Logger.Notification(
                $"GriefWarden: vacuum ({trigger}) {before / 1048576} MB -> {after / 1048576} MB"
            );
        }
        catch (Exception ex) {
            Main.API.Logger.Error("GriefWarden: vacuum failed: " + ex);
        }
    }

    public void RunVacuum(string trigger) {
        Main.Database.EnqueueMaintenance(connection => Vacuum(connection, trigger));
    }

    /// <summary>Row counts and cutoffs, for the status command.</summary>
    public string Describe() {
        if (!config.RetentionEnabled) return "Retention is disabled. The log grows without bound.";
        return $"Destructive (BROKE/KILLED/TAKEN) and presence kept {config.DestructiveRetentionDays} days, "
             + $"everything else {config.ContextRetentionDays} days, "
             + $"ceiling {config.MaxDatabaseMb} MB, pruning every {config.PruneIntervalHours}h. "
             + $"Database is currently {Main.Database.FileSizeBytes() / 1048576.0:0.0} MB.";
    }
}
