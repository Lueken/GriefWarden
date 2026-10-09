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
/// The reads the mod was missing.
///
/// Everything that existed before was position-first: look at a block, or sweep a radius.
/// That answers "what happened here", which is the second question an admin asks. The first
/// one is always "what did this player do", and there was no command for it at all — so a
/// real dispute on 2026-10-08 was settled by pulling the database off the server and writing
/// SQL by hand for two hours. Anything that needs SQL is not a moderation tool.
///
/// Three things are deliberately different from the original read paths:
///
///   - A player timeline merges the tables into one ordering. Block breaks, container
///     takes, kills and logins interleaved in time is what actually reconstructs an
///     afternoon; separate command outputs do not.
///   - Every read takes a time window. Without one, paging is theatre.
///
/// Query work happens on a worker thread with its own connection; formatting and sending
/// happen on the main thread.
/// </summary>
public class Queries {
    /// <summary>One event from any of the tables, before it has been turned into text.</summary>
    private class Row {
        public long Timestamp;
        public string Source = "";      // block | container | entity | session | chat
        public string Action = "";
        public string Label = "";       // block code, entity name, or chat text
        public string? Item;
        public int Quantity;
        public bool HasPosition;
        public int X, Y, Z;             // spawn-relative, as stored
        public string? ContainerId;     // absolute coords live in here instead
        public string? Actor;
        public string? ActorUid;
    }

    private readonly string dbPath;

    public Queries(string dbPath) {
        this.dbPath = dbPath;
    }

    // ----------------------------------------------------------------- player timeline

    /// <summary>
    /// Everything one player did inside a window, newest page first, oldest line first
    /// within the page so it reads downward like a story.
    /// </summary>
    public void PlayerLog(IServerPlayer caller, int groupId, string targetName, long sinceUnix, int pageNum, bool summary, int[]? actionFilter = null) {
        Run(caller, groupId, () => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();

            Who? who = ResolvePlayer(connection, targetName);
            if (who == null) return NoSuchPlayer(connection, targetName);

            return summary
                ? SummariseForPlayer(connection, who.Value, sinceUnix, actionFilter)
                : TimelineForPlayer(connection, who.Value, sinceUnix, pageNum, actionFilter);
        });
    }

    /// <summary>
    /// The union is spelled out rather than built in a loop because every branch has to
    /// contribute the same column list in the same order, and a mismatch there reads as
    /// data rather than failing.
    /// </summary>
    private const string TimelineUnion = @"
        SELECT timestamp_utc t, 'block' src, actiontype a, block label, itemstack_data d, itemstack_encoding e,
               x, y, z, 1 haspos, NULL cid, 0 qty
          FROM blocklogs WHERE player_id = $pid AND timestamp_utc >= $since
        UNION ALL
        SELECT timestamp_utc, 'container', actiontype, NULL, itemstack_data, itemstack_encoding,
               0, 0, 0, 0, containerid, quantity
          FROM containerlogs WHERE player_id = $pid AND timestamp_utc >= $since
        UNION ALL
        SELECT timestamp_utc, 'entity', actiontype, entityname, itemstack_data, itemstack_encoding,
               x, y, z, 1, NULL, 0
          FROM entitylogs WHERE player_id = $pid AND timestamp_utc >= $since
        UNION ALL
        SELECT timestamp_utc, 'session', actiontype, NULL, NULL, 0,
               0, 0, 0, 0, NULL, 0
          FROM sessions WHERE player_id = $pid AND timestamp_utc >= $since
";

    private List<Row> TimelineForPlayer(SqliteConnection connection, Who who, long sinceUnix, int pageNum, int[]? actionFilter) {
        int pageSize = Main.Config.LogPageSize;
        int offset = pageSize * (pageNum - 1);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"SELECT * FROM ({TimelineUnion}) {ActionClause(actionFilter)} ORDER BY t DESC, src LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$pid", who.Id);
        cmd.Parameters.AddWithValue("$since", sinceUnix);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", offset);

        var rows = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            var row = new Row {
                Timestamp = reader.GetInt64(0),
                Source = reader.GetString(1),
                Action = ActionName(reader.IsDBNull(2) ? -1 : reader.GetInt32(2)),
                Label = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Item = Main.Database.DecompressText(reader.IsDBNull(4) ? null : (byte[])reader[4], reader.GetInt32(5)),
                HasPosition = reader.GetInt32(9) == 1,
                ContainerId = reader.IsDBNull(10) ? null : reader.GetString(10),
                Quantity = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                Actor = who.Name,
                ActorUid = who.Uid,
            };
            if (row.HasPosition) {
                row.X = reader.GetInt32(6);
                row.Y = reader.GetInt32(7);
                row.Z = reader.GetInt32(8);
            }
            rows.Add(row);
        }

        // Newest page, read oldest-first inside it.
        rows.Reverse();
        return rows;
    }

    private List<Row> SummariseForPlayer(SqliteConnection connection, Who who, long sinceUnix, int[]? actionFilter) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"SELECT src, a, COUNT(*) n, SUM(qty) items, MIN(t) first, MAX(t) last
                             FROM ({TimelineUnion}) {ActionClause(actionFilter)} GROUP BY src, a ORDER BY src, a";
        cmd.Parameters.AddWithValue("$pid", who.Id);
        cmd.Parameters.AddWithValue("$since", sinceUnix);

        var rows = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            // Reusing Row for a summary line keeps one formatting path. Quantity carries the
            // count and Label carries the rendered span.
            long first = reader.GetInt64(4);
            long last = reader.GetInt64(5);
            rows.Add(new Row {
                Timestamp = last,
                Source = "summary",
                Action = reader.GetString(0) + " " + ActionName(reader.IsDBNull(1) ? -1 : reader.GetInt32(1)),
                Quantity = reader.GetInt32(2),
                Item = reader.IsDBNull(3) ? null : reader.GetInt32(3).ToString(),
                Label = Util.FormatTimestamp(first) + " to " + Util.FormatTimestamp(last),
                Actor = who.Name,
                ActorUid = who.Uid,
            });
        }
        return rows;
    }

    /// <summary>
    /// Optional action-type restriction on the merged timeline.
    ///
    /// Earned its place on first contact with real data: a cook's page one was twelve
    /// consecutive water-bucket place-and-break rows, which pushed the three decorative
    /// ovens that the dispute was actually about onto page four. Being able to say
    /// "-a BROKE,TAKEN" is the difference between a timeline and a transcript of fetching
    /// water.
    ///
    /// Values are action ids resolved from names before they get here, never raw user text,
    /// so this interpolation cannot carry anything but integers.
    /// </summary>
    private static string ActionClause(int[]? actions) {
        if (actions == null || actions.Length == 0) return "";
        return "WHERE a IN (" + string.Join(",", actions) + ")";
    }

    // ----------------------------------------------------------------- item search

    /// <summary>
    /// Finds an item by name across every container transaction in the window.
    ///
    /// The filter cannot go into SQL: item names are stored brotli-compressed, so matching
    /// means decompressing candidates in process. That is why this one has a scan ceiling
    /// and reports when it hits it — a search that silently stopped early would read as
    /// proof that nobody ever touched the thing.
    /// </summary>
    public void ItemLog(IServerPlayer caller, int groupId, string needle, long sinceUnix, int pageNum) {
        Run(caller, groupId, () => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();

            int pageSize = Main.Config.LogPageSize;
            int wanted = pageSize * pageNum;
            int scanned = 0;
            var hits = new List<Row>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT c.timestamp_utc, p.last_playername, p.playeruid, c.containerid,
                                       c.itemstack_data, c.itemstack_encoding, c.quantity, c.actiontype
                                  FROM containerlogs c LEFT JOIN players p ON c.player_id = p.id
                                 WHERE c.timestamp_utc >= $since
                                 ORDER BY c.timestamp_utc DESC";
            cmd.Parameters.AddWithValue("$since", sinceUnix);

            using (var reader = cmd.ExecuteReader()) {
                while (reader.Read()) {
                    if (++scanned > Main.Config.ItemSearchScanLimit) break;

                    string? item = Main.Database.DecompressText(
                        reader.IsDBNull(4) ? null : (byte[])reader[4], reader.GetInt32(5));
                    if (item == null || item.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    hits.Add(new Row {
                        Timestamp = reader.GetInt64(0),
                        Source = "container",
                        Action = ActionName(reader.IsDBNull(7) ? -1 : reader.GetInt32(7)),
                        Item = item,
                        Quantity = reader.GetInt32(6),
                        ContainerId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Actor = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
                        ActorUid = reader.IsDBNull(2) ? null : reader.GetString(2),
                    });

                    if (hits.Count >= wanted) break;
                }
            }

            var page = new List<Row>();
            for (int i = pageSize * (pageNum - 1); i < hits.Count; i++) page.Add(hits[i]);
            page.Reverse();

            if (scanned > Main.Config.ItemSearchScanLimit) {
                page.Add(new Row {
                    Source = "note",
                    Label = $"scan ceiling of {Main.Config.ItemSearchScanLimit} rows reached; narrow the window with -t to search further back",
                });
            }
            return page;
        });
    }

    // ----------------------------------------------------------------- presence

    /// <summary>
    /// Was this player connected at this moment. Answers from the sessions table rather than
    /// from the density of their world actions, which is how it had to be answered before.
    /// </summary>
    public void PresenceAt(IServerPlayer caller, int groupId, string targetName, long atUnix) {
        Run(caller, groupId, () => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();

            Who? who = ResolvePlayer(connection, targetName);
            if (who == null) return NoSuchPlayer(connection, targetName);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT timestamp_utc, actiontype FROM sessions
                                 WHERE player_id = $pid AND timestamp_utc <= $at
                                 ORDER BY timestamp_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$pid", who.Value.Id);
            cmd.Parameters.AddWithValue("$at", atUnix);

            var rows = new List<Row>();
            using (var reader = cmd.ExecuteReader()) {
                if (!reader.Read()) {
                    rows.Add(new Row {
                        Source = "note",
                        Label = $"no session row for {who.Value.Name} at or before {Util.FormatTimestamp(atUnix)}. "
                              + "Presence logging started when this build was installed, so anything earlier is simply not covered.",
                    });
                    return rows;
                }

                long when = reader.GetInt64(0);
                int action = reader.GetInt32(1);
                bool online = action == (int)Database.ActionType.JOINED;
                rows.Add(new Row {
                    Source = "note",
                    Label = $"{who.Value.Name} was {(online ? "ONLINE" : "offline")} at {Util.FormatTimestamp(atUnix)} "
                          + $"— last {(online ? "joined" : "disconnected")} {Util.FormatTimestamp(when)}",
                });
            }
            return rows;
        });
    }

    // ----------------------------------------------------------------- shared plumbing

    /// <summary>
    /// Runs a read off the main thread, then formats and sends on it.
    /// </summary>
    private void Run(IServerPlayer caller, int groupId, Func<List<Row>> work) {
        System.Threading.Tasks.Task.Run(() => {
            List<Row> rows;
            try {
                rows = work();
            }
            catch (Exception ex) {
                Main.API.Logger.Error("GriefWarden: query failed: " + ex);
                rows = new List<Row> { new Row { Source = "note", Label = "query failed, see server-main.log" } };
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                if (rows.Count == 0) {
                    Send(caller, "No matching records in that window.");
                    return;
                }
                foreach (string line in Format(rows)) Send(caller, line);
            }, "GriefWardenQuery");
        });
    }

    private static void Send(IServerPlayer caller, string line) {
        Main.API.SendMessage(caller, GlobalConstants.InfoLogChatGroup, line, EnumChatType.CommandSuccess);
    }

    private List<string> Format(List<Row> rows) {
        var lines = new List<string>();
        foreach (Row row in rows) {
            switch (row.Source) {
                case "note":
                    lines.Add($"<font color=\"#D9A05B\">{row.Label}</font>");
                    continue;

                case "summary":
                    string items = row.Item == null || row.Item == "0" ? "" : $", {row.Item} items";
                    lines.Add($"<strong>{row.Action}</strong> x{row.Quantity}{items}  <font color=\"#9BD1EC\">{row.Label}</font>");
                    continue;
            }

            var sb = new StringBuilder();
            sb.Append($"<font color=\"#6F88DB\">{Util.FormatTimestamp(row.Timestamp)}</font> ");
            sb.Append($"<strong>{row.Action}</strong> ");

            switch (row.Source) {
                case "block":
                    sb.Append(row.Label);
                    if (!string.IsNullOrEmpty(row.Item)) sb.Append($" (with {row.Item})");
                    sb.Append($" at <font color=\"#9BD1EC\">{row.X}, {row.Y}, {row.Z}</font>");
                    break;

                case "container":
                    sb.Append($"{row.Quantity}x {row.Item} in <font color=\"#9BD1EC\">{row.ContainerId}</font>");
                    break;

                case "entity":
                    sb.Append(row.Label);
                    if (!string.IsNullOrEmpty(row.Item)) sb.Append($" (with {row.Item})");
                    sb.Append($" at <font color=\"#9BD1EC\">{row.X}, {row.Y}, {row.Z}</font>");
                    break;


                case "session":
                    sb.Append(row.Actor);
                    break;
            }

            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary>
    /// Case-insensitive on purpose. The original rollback command matched player names
    /// exactly, which turns a capitalisation slip into "no player logged with that
    /// username" and sends an admin looking for a bug that is not there.
    /// </summary>
    private Who? ResolvePlayer(SqliteConnection connection, string name) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, last_playername, playeruid FROM players WHERE last_playername = $name COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) {
            return new Who {
                Id = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? name : reader.GetString(1),
                Uid = reader.IsDBNull(2) ? null : reader.GetString(2),
            };
        }
        return null;
    }

    /// <summary>
    /// A resolved player. The uid travels with every row so claim rosters, which are keyed
    /// by uid rather than name, can be checked for someone who is not logged in.
    /// </summary>
    private struct Who {
        public int Id;
        public string Name;
        public string? Uid;
    }

    /// <summary>
    /// A miss offers the names that are close, because the usual cause is a half-remembered
    /// spelling and the usual recovery is otherwise asking the player how they spell it.
    /// </summary>
    private List<Row> NoSuchPlayer(SqliteConnection connection, string name) {
        var suggestions = new List<string>();
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = "SELECT last_playername FROM players WHERE last_playername LIKE $like ORDER BY last_playername LIMIT 8";
            cmd.Parameters.AddWithValue("$like", "%" + name + "%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(0)) suggestions.Add(reader.GetString(0));
            }
        }

        string label = suggestions.Count > 0
            ? $"No player called \"{name}\". Close matches: {string.Join(", ", suggestions)}"
            : $"No player called \"{name}\" has ever been logged.";
        return new List<Row> { new Row { Source = "note", Label = label } };
    }

    private static string ActionName(int action) {
        return Database.NameForAction(action);
    }
}
