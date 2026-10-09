using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace GriefWarden;

/// <summary>
/// Putting back what somebody broke, with the safety the original command did not have.
///
/// The version this replaces had four problems, and the combination made it something you
/// could not safely point at a real incident:
///
///   1. **No time window.** It rolled back every break that player had made inside the
///      radius for as far back as retention went — ninety days. Aimed at a builder who had
///      legitimately demolished a wing of an inn that morning, it would have tried to
///      reinstate months of their own finished work.
///   2. **No preview, and it acted immediately.** The only way to find out what it would do
///      was to let it do it. There is no undo.
///   3. **It silently skipped anything "chiseled".** On a server where most decorative
///      storage is chiselled, the command quietly declined to restore exactly the blocks a
///      report was usually about, reported success anyway, and never said a word.
///   4. **It overwrote whatever was standing there now.** No check that the position was
///      still empty, so restoring a day-old break could delete a day-old rebuild.
///
/// So: dry run by default, --apply to commit, a window that defaults to a day, every
/// refusal counted and explained, and nothing written over a block that somebody has since
/// put there.
///
/// Restoration goes by block CODE rather than by the numeric id the log also carries. The
/// code is the identifier that means something to a person reading a preview
/// ("game:chest-east", not "19534"), and it is the one that survives the block registry
/// being rebuilt when the mod list changes. The id stays as a fallback for the rare row
/// whose code no longer resolves, and the preview says which path each block took.
/// </summary>
public class Rollback {
    private readonly string dbPath;

    public Rollback(string dbPath) {
        this.dbPath = dbPath;
    }

    private class Candidate {
        public BlockPos Pos = null!;
        public long Timestamp;
        public string RawBlock = "";
        public int LoggedId;
        public Block? Resolved;
        public string? RefusedBecause;
        public bool ByIdFallback;
        public int SnapshotItems;   // contents the container held when it died, if any
    }

    /// <summary>
    /// Plans a rollback and either describes it or performs it.
    ///
    /// Everything up to the decision is read-only, so a dry run is genuinely free and can
    /// be run as many times as it takes to believe the output.
    /// </summary>
    public void Run(IServerPlayer caller, int groupId, string playerName, int radius, long sinceUnix, bool apply) {
        System.Threading.Tasks.Task.Run(() => {
            List<string> lines;
            List<Candidate> toRestore = new();

            try {
                using var connection = new SqliteConnection("Data Source=" + dbPath);
                connection.Open();

                int? playerId = ResolvePlayerId(connection, playerName);
                if (playerId == null) {
                    Say(caller, $"No player called \"{playerName}\" has ever been logged.");
                    return;
                }

                Vec3i centre = caller.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
                List<Candidate> candidates = LoadCandidates(connection, playerId.Value, centre, radius, sinceUnix);
                AttachSnapshots(connection, candidates);
                lines = Describe(playerName, radius, sinceUnix, candidates, apply, toRestore);
            }
            catch (Exception ex) {
                Main.API.Logger.Error("GriefWarden: rollback planning failed: " + ex);
                Say(caller, "Rollback planning failed, see server-main.log.");
                return;
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                int restored = 0;
                int blocked = 0;

                if (apply) {
                    foreach (Candidate c in toRestore) {
                        // Re-checked on the main thread, immediately before writing. The
                        // planning pass ran on a worker thread and the world can have moved
                        // on since; this is the check that actually protects somebody's
                        // rebuild.
                        Block standing = Main.API.World.BlockAccessor.GetBlock(c.Pos);
                        if (standing != null && standing.Id != 0) {
                            blocked++;
                            continue;
                        }
                        Main.API.World.BlockAccessor.SetBlock(c.Resolved!.Id, c.Pos);
                        restored++;
                    }

                    lines.Add($"<strong>Restored {restored} block{(restored == 1 ? "" : "s")}.</strong>");
                    if (blocked > 0) {
                        lines.Add($"Left {blocked} alone: something had been built there since.");
                    }
                }

                foreach (string line in lines) Say(caller, line);
            }, "GriefWardenRollback");
        });
    }

    // ----------------------------------------------------------------- planning

    private List<Candidate> LoadCandidates(SqliteConnection connection, int playerId, Vec3i centre, int radius, long sinceUnix) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT timestamp_utc, block, x, y, z, oldblockid FROM blocklogs
                             WHERE player_id = $pid AND actiontype = 0
                               AND timestamp_utc >= $since
                               AND x BETWEEN $x - $r AND $x + $r
                               AND y BETWEEN $y - $r AND $y + $r
                               AND z BETWEEN $z - $r AND $z + $r
                             ORDER BY timestamp_utc ASC";
        cmd.Parameters.AddWithValue("$pid", playerId);
        cmd.Parameters.AddWithValue("$since", sinceUnix);
        cmd.Parameters.AddWithValue("$x", centre.X);
        cmd.Parameters.AddWithValue("$y", centre.Y);
        cmd.Parameters.AddWithValue("$z", centre.Z);
        cmd.Parameters.AddWithValue("$r", radius);

        // One entry per position. Ordered oldest-first and kept first-wins, so what goes
        // back is the state before this player touched the spot rather than whatever they
        // happened to break there last.
        var byPosition = new Dictionary<string, Candidate>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            int x = reader.GetInt32(2), y = reader.GetInt32(3), z = reader.GetInt32(4);
            string key = $"{x}|{y}|{z}";
            if (byPosition.ContainsKey(key)) continue;

            var c = new Candidate {
                Timestamp = reader.GetInt64(0),
                RawBlock = reader.IsDBNull(1) ? "" : reader.GetString(1),
                LoggedId = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Pos = new BlockPos(
                    x + (int)Main.API.World.DefaultSpawnPosition.X, y,
                    z + (int)Main.API.World.DefaultSpawnPosition.Z),
            };
            Classify(c);
            byPosition[key] = c;
        }

        return new List<Candidate>(byPosition.Values);
    }

    /// <summary>
    /// Decides whether one logged break can be put back, and if not, why not. Every refusal
    /// gets a reason because the whole point of the rewrite is that refusals stop being
    /// invisible.
    /// </summary>
    private void Classify(Candidate c) {
        // A chiselled block's shape lived in its block entity, and that is long gone. The
        // block type can be put back but it would return as a blank cube, which is not a
        // restoration, it is a different kind of damage. Refuse, loudly.
        if (c.RawBlock.IndexOf("chiseled", StringComparison.OrdinalIgnoreCase) >= 0
            || c.RawBlock.IndexOf("microblock", StringComparison.OrdinalIgnoreCase) >= 0) {
            c.RefusedBecause = "chiselled: its shape was stored in the block entity and is not in the log";
            return;
        }

        AssetLocation? code = CodeFrom(c.RawBlock);
        if (code != null) {
            Block? block = Main.API.World.GetBlock(code);
            if (block != null && block.Id != 0) {
                c.Resolved = block;
                return;
            }
        }

        // Fall back to the numeric id the row also carries. Worth trying, worth flagging:
        // ids are handles into this world's block registry rather than names, so one that
        // no longer matches its original code will put back the wrong block entirely.
        if (c.LoggedId != 0) {
            Block? byId = Main.API.World.GetBlock(c.LoggedId);
            if (byId != null && byId.Id != 0) {
                c.Resolved = byId;
                c.ByIdFallback = true;
                return;
            }
        }

        c.RefusedBecause = code == null
            ? "the logged block string could not be read"
            : $"{code} is not a block this server knows any more";
    }

    /// <summary>
    /// Pulls the block code out of a stored string like "game:block chest-east/19534".
    /// </summary>
    private static AssetLocation? CodeFrom(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        int colon = raw.IndexOf(':');
        int space = raw.IndexOf(' ');
        int slash = raw.LastIndexOf('/');
        if (colon < 0 || space < colon || slash < space) return null;

        string domain = raw.Substring(0, colon);
        string path = raw.Substring(space + 1, slash - space - 1);
        if (domain.Length == 0 || path.Length == 0) return null;

        try {
            return new AssetLocation(domain, path);
        }
        catch {
            return null;
        }
    }

    /// <summary>
    /// Marks candidates that were containers with something in them, so the preview can say
    /// plainly that putting the chest back does not put the contents back.
    /// </summary>
    private void AttachSnapshots(SqliteConnection connection, List<Candidate> candidates) {
        int spawnX = (int)Main.API.World.DefaultSpawnPosition.X;
        int spawnZ = (int)Main.API.World.DefaultSpawnPosition.Z;

        foreach (Candidate c in candidates) {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT total_items FROM containersnapshots
                                 WHERE x = $x AND y = $y AND z = $z AND timestamp_utc BETWEEN $t - 5 AND $t + 5
                                 ORDER BY ABS(timestamp_utc - $t) LIMIT 1";
            cmd.Parameters.AddWithValue("$x", c.Pos.X - spawnX);
            cmd.Parameters.AddWithValue("$y", c.Pos.Y);
            cmd.Parameters.AddWithValue("$z", c.Pos.Z - spawnZ);
            cmd.Parameters.AddWithValue("$t", c.Timestamp);

            object? result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value) c.SnapshotItems = Convert.ToInt32(result);
        }
    }

    // ----------------------------------------------------------------- reporting

    private List<string> Describe(string playerName, int radius, long sinceUnix,
                                  List<Candidate> candidates, bool apply, List<Candidate> toRestore) {
        var lines = new List<string>();

        if (candidates.Count == 0) {
            lines.Add($"No breaks by {playerName} within {radius} blocks since {Util.FormatTimestamp(sinceUnix)}.");
            return lines;
        }

        var byBlock = new Dictionary<string, int>();
        var refusals = new Dictionary<string, int>();
        int idFallbacks = 0;
        int containersWithContents = 0;
        int itemsLost = 0;

        foreach (Candidate c in candidates) {
            if (c.RefusedBecause != null) {
                refusals.TryGetValue(c.RefusedBecause, out int n);
                refusals[c.RefusedBecause] = n + 1;
                continue;
            }

            toRestore.Add(c);
            string name = c.Resolved!.Code?.ToString() ?? "unknown";
            byBlock.TryGetValue(name, out int count);
            byBlock[name] = count + 1;
            if (c.ByIdFallback) idFallbacks++;
            if (c.SnapshotItems > 0) {
                containersWithContents++;
                itemsLost += c.SnapshotItems;
            }
        }

        string verb = apply ? "Restoring" : "Would restore";
        lines.Add($"<strong>{verb} {toRestore.Count} of {candidates.Count} logged breaks</strong> by {playerName}, "
                + $"radius {radius}, since {Util.FormatTimestamp(sinceUnix)}.");

        foreach (var kv in SortedTop(byBlock, 8)) lines.Add($"  {kv.Value}x {kv.Key}");
        if (byBlock.Count > 8) lines.Add($"  ...and {byBlock.Count - 8} other block types");

        foreach (var kv in refusals) {
            lines.Add($"<font color=\"#D9A05B\">Refusing {kv.Value}: {kv.Key}</font>");
        }

        if (idFallbacks > 0) {
            lines.Add($"<font color=\"#D9A05B\">{idFallbacks} will be restored from a numeric block id because their "
                    + "code no longer resolves. Check those by eye before trusting them.</font>");
        }

        if (containersWithContents > 0) {
            lines.Add($"<font color=\"#D9A05B\">{containersWithContents} of these were containers holding {itemsLost} "
                    + "items. The container comes back EMPTY: contents are recorded in the manifest, not restored.</font>");
        }

        if (!apply) {
            lines.Add("<strong>Nothing has been changed.</strong> Add --apply to commit. There is no undo.");
        }

        return lines;
    }

    private static List<KeyValuePair<string, int>> SortedTop(Dictionary<string, int> counts, int take) {
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
        return list;
    }

    private int? ResolvePlayerId(SqliteConnection connection, string name) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM players WHERE last_playername = $name COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        object? result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }

    private static void Say(IServerPlayer caller, string line) {
        Main.API.Event.EnqueueMainThreadTask(
            () => Main.API.SendMessage(caller, GlobalConstants.InfoLogChatGroup, line, EnumChatType.CommandSuccess),
            "GriefWardenRollbackMsg");
    }
}
