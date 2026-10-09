using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Database : IDisposable {
    private string dbPath;
    // Page size is a config value now. It was 4, hardcoded, which is not a page: a single
    // chest in an active town carries a hundred rows in a morning.
    private int logLimit => Main.Config?.LogPageSize ?? 12;

    private Thread workerThread;
    private CancellationTokenSource cancellationTokenSource;
    private ConcurrentQueue<Action<SqliteConnection>> databaseTasks;
    private ConcurrentDictionary<string, (int Id, string? LastPlayerName)> playerCache = new ConcurrentDictionary<string, (int Id, string? LastPlayerName)>();

    private string createPlayersTable = @"CREATE TABLE IF NOT EXISTS players (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        playeruid TEXT UNIQUE,
        last_playername TEXT
    )";
    private string createBlockLogsTable = @"CREATE TABLE IF NOT EXISTS blocklogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp_utc INTEGER,
        player_id INTEGER NULL,
        actiontype INTEGER,
        block TEXT,
        itemstack_data BLOB NULL,
        itemstack_encoding INTEGER NOT NULL DEFAULT 0,
        x INTEGER,
        y INTEGER,
        z INTEGER,
        oldblockid INTEGER
    )";
    private string createEntityLogsTable = @"CREATE TABLE IF NOT EXISTS entitylogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp_utc INTEGER,
        player_id INTEGER NULL,
        actiontype INTEGER,
        entityname TEXT,
        entityid TEXT,
        itemstack_data BLOB NULL,
        itemstack_encoding INTEGER NOT NULL DEFAULT 0,
        x INTEGER,
        y INTEGER,
        z INTEGER
    )";
    private string createContainerLogsTable = @"CREATE TABLE IF NOT EXISTS containerlogs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp_utc INTEGER,
        player_id INTEGER NULL,
        containerid TEXT,
        itemstack_data BLOB NULL,
        itemstack_encoding INTEGER NOT NULL DEFAULT 0,
        quantity INTEGER,
        actiontype INTEGER
    )";
    /// <summary>
    /// Who was connected, and when.
    ///
    /// Presence is evidence on its own. Whether a player was online at the minute an
    /// instruction was given in chat is the difference between ignoring it and never having
    /// seen it, and until this table existed the only way to answer it was to count that
    /// player's world actions around the timestamp and infer from the density.
    ///
    /// No IP address, on purpose. It would help with alt accounts and it is the one field
    /// here that turns a grief log into something that matters if the file leaks.
    /// </summary>
    private string createSessionsTable = @"CREATE TABLE IF NOT EXISTS sessions (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp_utc INTEGER,
        player_id INTEGER NULL,
        actiontype INTEGER
    )";
    /// <summary>
    /// What was inside a container when it was destroyed.
    ///
    /// A separate table rather than extra columns on blocklogs, for two reasons. The reader
    /// in RollbackBreaks does positional reads off SELECT *, so widening that table invites
    /// a silent column shift. And the panel has its own fixed expectations about blocklogs'
    /// shape. A new table is additive for every existing consumer.
    ///
    /// containerid is the useful part: it is the same inventory id containerlogs uses, so a
    /// manifest joins straight onto the history of who had been putting things in and
    /// taking things out of that exact chest.
    ///
    /// x/y/z are spawn-relative, matching blocklogs rather than containerlogs' absolute
    /// inventory ids, so a snapshot pairs with the BROKE row beside it without an offset.
    /// </summary>
    private string createContainerSnapshotsTable = @"CREATE TABLE IF NOT EXISTS containersnapshots (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp_utc INTEGER,
        player_id INTEGER NULL,
        actiontype INTEGER,
        block TEXT,
        containerid TEXT NULL,
        x INTEGER,
        y INTEGER,
        z INTEGER,
        slot_count INTEGER,
        total_items INTEGER,
        manifest_data BLOB NULL,
        manifest_encoding INTEGER NOT NULL DEFAULT 0
    )";

    public enum ActionType {
        BROKE = 0,
        PLACED = 1,
        USED = 2,
        INTERACTED = 3,
        KILLED = 4,
        TAKEN = 5,
        SWAP = 6,
        SAME_ITEM = 7,
        SPAWNED = 8,
        DESPAWNED = 9,
        JOINED = 10,
        LEFT = 11
    }

    private static readonly Dictionary<string, int> ActionTypeMap = new Dictionary<string, int> {
        { "BROKE", (int)ActionType.BROKE },
        { "PLACED", (int)ActionType.PLACED },
        { "USED", (int)ActionType.USED },
        { "INTERACTED", (int)ActionType.INTERACTED },
        { "KILLED", (int)ActionType.KILLED },
        { "TAKEN", (int)ActionType.TAKEN },
        { "SWAP", (int)ActionType.SWAP },
        { "SAME_ITEM", (int)ActionType.SAME_ITEM },
        { "SPAWNED", (int)ActionType.SPAWNED },
        { "DESPAWNED", (int)ActionType.DESPAWNED },
        { "JOINED", (int)ActionType.JOINED },
        { "LEFT", (int)ActionType.LEFT }
    };

    private static readonly Dictionary<int, string> ReverseActionTypeMap = new Dictionary<int, string> {
        { (int)ActionType.BROKE, "BROKE" },
        { (int)ActionType.PLACED, "PLACED" },
        { (int)ActionType.USED, "USED" },
        { (int)ActionType.INTERACTED, "INTERACTED" },
        { (int)ActionType.KILLED, "KILLED" },
        { (int)ActionType.TAKEN, "TAKEN" },
        { (int)ActionType.SWAP, "SWAP" },
        { (int)ActionType.SAME_ITEM, "SAME_ITEM" },
        { (int)ActionType.SPAWNED, "SPAWNED" },
        { (int)ActionType.DESPAWNED, "DESPAWNED" },
        { (int)ActionType.JOINED, "JOINED" },
        { (int)ActionType.LEFT, "LEFT" }
    };

    /// <summary>
    /// Where the database lives, for the read-only query paths that open their own
    /// connection rather than queueing onto the single writer thread.
    /// </summary>
    public string DbPath => dbPath;

    /// <summary>Label for a stored action id, or the raw number if it is one we do not know.</summary>
    public static string NameForAction(int action) {
        return ReverseActionTypeMap.TryGetValue(action, out string name) ? name : "ACTION_" + action;
    }

    public Database() {
        dbPath = Path.GetFullPath(Path.Combine(Main.API.GetOrCreateDataPath("GriefWarden"), "database.db"));

        // Initialize schema and indices synchronously
        using (var connection = new SqliteConnection("Data Source=" + dbPath)) {
            connection.Open();

            // Enable WAL mode for performance
            using (var pragmaCmd = connection.CreateCommand()) {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
                pragmaCmd.ExecuteNonQuery();
                pragmaCmd.CommandText = "PRAGMA synchronous=NORMAL;";
                pragmaCmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = createPlayersTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createBlockLogsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createEntityLogsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createContainerLogsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createSessionsTable;
                cmd.ExecuteNonQuery();
                cmd.CommandText = createContainerSnapshotsTable;
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_blocklogs_coords ON blocklogs(x, y, z);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entitylogs_coords ON entitylogs(x, y, z);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_containerlogs_id ON containerlogs(containerid);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_blocklogs_ts ON blocklogs(timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_blocklogs_pid_ts ON blocklogs(player_id, timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_blocklogs_act_ts ON blocklogs(actiontype, timestamp_utc);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entitylogs_ts ON entitylogs(timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entitylogs_pid_ts ON entitylogs(player_id, timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entitylogs_act_ts ON entitylogs(actiontype, timestamp_utc);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_containerlogs_ts ON containerlogs(timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_containerlogs_pid_ts ON containerlogs(player_id, timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_containerlogs_act_ts ON containerlogs(actiontype, timestamp_utc);";
                cmd.ExecuteNonQuery();

                // Presence is always asked about as "this player, around this
                // time", so both indices lead on the pair rather than on either alone.
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_sessions_ts ON sessions(timestamp_utc);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_sessions_pid_ts ON sessions(player_id, timestamp_utc);";
                cmd.ExecuteNonQuery();

                // Snapshots are reached two ways: from the position of a break being
                // investigated, and from the container id when following one chest's whole
                // history.
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshots_coords ON containersnapshots(x, y, z);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshots_cid ON containersnapshots(containerid);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshots_ts ON containersnapshots(timestamp_utc);";
                cmd.ExecuteNonQuery();
            }
        }

        databaseTasks = new ConcurrentQueue<Action<SqliteConnection>>();
        cancellationTokenSource = new CancellationTokenSource();
        workerThread = new Thread(WorkerLoop);
        workerThread.IsBackground = true;
        workerThread.Start();
    }

    private void WorkerLoop() {
        using var connection = new SqliteConnection("Data Source=" + dbPath);
        connection.Open();

        while (!cancellationTokenSource.IsCancellationRequested) {
            if (databaseTasks.TryDequeue(out var task)) {
                try {
                    task(connection);
                }
                catch (Exception ex) {
                    Main.API.Logger.Error("GriefWarden: Database worker encountered an error: " + ex);
                }
            }
            else {
                Thread.Sleep(10); // Sleep briefly if queue is empty
            }
        }

        // Process remaining tasks before exiting
        while (databaseTasks.TryDequeue(out var task)) {
            try {
                task(connection);
            }
            catch (Exception ex) {
                Main.API.Logger.Error("GriefWarden: Database worker encountered an error during shutdown: " + ex);
            }
        }
    }

    public (byte[]? data, int encoding) CompressText(string? value) {
        if (value == null) return (null, 0);

        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(value);
        using var outputStream = new MemoryStream();
        using (var brotliStream = new System.IO.Compression.BrotliStream(outputStream, System.IO.Compression.CompressionLevel.Optimal)) {
            brotliStream.Write(utf8Bytes, 0, utf8Bytes.Length);
        }

        byte[] compressedBytes = outputStream.ToArray();
        if (compressedBytes.Length < utf8Bytes.Length) {
            return (compressedBytes, 2);
        }
        return (utf8Bytes, 1);
    }

    public string? DecompressText(byte[]? data, int encoding) {
        if (data == null || encoding == 0) return null;
        if (encoding == 1) return System.Text.Encoding.UTF8.GetString(data);
        if (encoding == 2) {
            using var inputStream = new MemoryStream(data);
            using var brotliStream = new System.IO.Compression.BrotliStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            brotliStream.CopyTo(outputStream);
            return System.Text.Encoding.UTF8.GetString(outputStream.ToArray());
        }
        return null;
    }

    private int GetOrInsertPlayer(SqliteConnection connection, string? playername, string? playeruid) {
        if (playername == null && playeruid == null) return -1;

        string key = playeruid ?? ("name:" + playername);

        if (playerCache.TryGetValue(key, out var cached)) {
            if (playername != null && cached.LastPlayerName != playername) {
                using var updateCmd = connection.CreateCommand();
                if (playeruid != null) {
                    updateCmd.CommandText = "UPDATE players SET last_playername = $name WHERE playeruid = $uid;";
                    updateCmd.Parameters.AddWithValue("$uid", playeruid);
                }
                else {
                    updateCmd.CommandText = "UPDATE players SET last_playername = $name WHERE id = $id;";
                    updateCmd.Parameters.AddWithValue("$id", cached.Id);
                }
                updateCmd.Parameters.AddWithValue("$name", playername);
                updateCmd.ExecuteNonQuery();
                playerCache[key] = (cached.Id, playername);
            }
            return cached.Id;
        }

        using var cmd = connection.CreateCommand();
        if (playeruid != null) {
            cmd.CommandText = "INSERT OR IGNORE INTO players (playeruid, last_playername) VALUES ($uid, $name);";
            cmd.Parameters.AddWithValue("$uid", playeruid);
            cmd.Parameters.AddWithValue("$name", playername ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "UPDATE players SET last_playername = $name WHERE playeruid = $uid;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT id FROM players WHERE playeruid = $uid;";
            int id = Convert.ToInt32(cmd.ExecuteScalar());
            playerCache[key] = (id, playername);
            return id;
        }
        else {
            cmd.CommandText = "INSERT INTO players (last_playername) VALUES ($name); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$name", playername);
            int id = Convert.ToInt32(cmd.ExecuteScalar());
            playerCache[key] = (id, playername);
            return id;
        }
    }

    public void AddBlockLog(string? playername, string? playeruid, string actiontype, string block, string? itemstack, int x, int y, int z, int? oldblockid) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        databaseTasks.Enqueue((connection) => {
            int playerId = GetOrInsertPlayer(connection, playername, playeruid);
            var compressed = CompressText(itemstack);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO blocklogs (timestamp_utc, player_id, actiontype, block, itemstack_data, itemstack_encoding, x, y, z, oldblockid)
            VALUES ($timestamp, $player_id, $actiontype, $block, $itemstack_data, $itemstack_encoding, $x, $y, $z, $oldblockid)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$player_id", playerId == -1 ? DBNull.Value : playerId);
            cmd.Parameters.AddWithValue("$actiontype", ActionTypeMap.TryGetValue(actiontype, out int val) ? val : 0);
            cmd.Parameters.AddWithValue("$block", block);
            cmd.Parameters.AddWithValue("$itemstack_data", compressed.data ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$itemstack_encoding", compressed.encoding);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$oldblockid", oldblockid ?? (object)DBNull.Value);

            cmd.ExecuteNonQuery();
        });
    }

    // RollbackBreaks moved to Rollback.cs, where it gained a time window, a dry run, a
    // refusal report and a check that it is not about to overwrite somebody's rebuild. The
    // old one had none of those and restored by numeric block id.

    public void CheckBlockLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius, long sinceUnix = 0) {
        // Read on a separate thread/connection to not block main thread
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT b.id, b.timestamp_utc, p.last_playername, p.playeruid, b.actiontype, b.block, b.itemstack_data, b.itemstack_encoding, b.x, b.y, b.z FROM (
            SELECT * FROM blocklogs
            WHERE x BETWEEN $x - $radius AND $x + $radius
            AND y BETWEEN $y - $radius AND $y + $radius
            AND z BETWEEN $z - $radius AND $z + $radius
            AND timestamp_utc >= $since
            ORDER BY id DESC
            LIMIT $loglimit
            OFFSET $skiplognum) b
            LEFT JOIN players p ON b.player_id = p.id
            ORDER BY b.id ASC";

            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$radius", radius);
            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);
            cmd.Parameters.AddWithValue("$since", sinceUnix);

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    int backPageNum = pageNum > 1 ? pageNum - 1 : 1;
                    int forwardPageNum = pageNum + 1;
                    string pageCmdStr = "/blocklog -r " + radius + " -p ";
                    string backPageCmdStr = pageCmdStr + backPageNum;
                    string forwardPageCmdStr = pageCmdStr + forwardPageNum;

                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
                    logs.Add("<strong><font color=\"white\">              <a href=\"chattype://" + backPageCmdStr + "\">←←←</a> | <a href=\"chattype://" + forwardPageCmdStr + "\">→→→</a></font></strong>");
                    while (reader.Read()) {
                        long tsSeconds = reader.GetInt64(1);
                        string timestamp = Util.FormatTimestamp(tsSeconds);

                        string? playername = reader.IsDBNull(2) ? null : reader.GetString(2);
                        string? playeruid = reader.IsDBNull(3) ? null : reader.GetString(3);

                        int actiontypeInt = reader.GetInt32(4);
                        string actiontype = ReverseActionTypeMap.TryGetValue(actiontypeInt, out string aType) ? aType : "UNKNOWN";

                        string block = reader.IsDBNull(5) ? "" : reader.GetString(5);

                        byte[]? itemstackData = reader.IsDBNull(6) ? null : (byte[])reader[6];
                        int itemstackEncoding = reader.GetInt32(7);
                        string? itemstack = DecompressText(itemstackData, itemstackEncoding);

                        int logX = reader.GetInt32(8);
                        int logY = reader.GetInt32(9);
                        int logZ = reader.GetInt32(10);

                        string playerStr = playername == null ? "" : "<strong>{1}</strong>({2}) ";
                        string itemstackStr = itemstack == null ? "" : "with {5} ";
                        string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | " + playerStr + "{3} {4} " + itemstackStr + "@ <strong><font color=\"#9BD1EC\">{6}, {7}, {8}</font></strong>", timestamp, playername, playeruid, actiontype, block, itemstack, logX, logY, logZ);
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No block logs found here.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendBlockLog");
        });
    }

    public void AddEntityLog(string? playername, string? playeruid, string actiontype, string entityname, string entityid, string? itemstack, int x, int y, int z) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        databaseTasks.Enqueue((connection) => {
            int playerId = GetOrInsertPlayer(connection, playername, playeruid);
            var compressed = CompressText(itemstack);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO entitylogs (timestamp_utc, player_id, actiontype, entityname, entityid, itemstack_data, itemstack_encoding, x, y, z)
            VALUES ($timestamp, $player_id, $actiontype, $entityname, $entityid, $itemstack_data, $itemstack_encoding, $x, $y, $z)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$player_id", playerId == -1 ? DBNull.Value : playerId);
            cmd.Parameters.AddWithValue("$actiontype", ActionTypeMap.TryGetValue(actiontype, out int val) ? val : 3);
            cmd.Parameters.AddWithValue("$entityname", entityname);
            cmd.Parameters.AddWithValue("$entityid", entityid);
            cmd.Parameters.AddWithValue("$itemstack_data", compressed.data ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$itemstack_encoding", compressed.encoding);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);

            cmd.ExecuteNonQuery();
        });
    }

    public void CheckEntityLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius, long sinceUnix = 0) {
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT e.id, e.timestamp_utc, p.last_playername, p.playeruid, e.actiontype, e.entityname, e.entityid, e.itemstack_data, e.itemstack_encoding, e.x, e.y, e.z FROM (
            SELECT * FROM entitylogs
            WHERE x BETWEEN $x - $radius AND $x + $radius
            AND y BETWEEN $y - $radius AND $y + $radius
            AND z BETWEEN $z - $radius AND $z + $radius
            AND timestamp_utc >= $since
            ORDER BY id DESC
            LIMIT $loglimit
            OFFSET $skiplognum) e
            LEFT JOIN players p ON e.player_id = p.id
            ORDER BY e.id ASC";

            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$radius", radius);
            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);
            cmd.Parameters.AddWithValue("$since", sinceUnix);

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    int backPageNum = pageNum > 1 ? pageNum - 1 : 1;
                    int forwardPageNum = pageNum + 1;
                    string pageCmdStr = "/entitylog -r " + radius + " -p ";
                    string backPageCmdStr = pageCmdStr + backPageNum;
                    string forwardPageCmdStr = pageCmdStr + forwardPageNum;

                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
                    logs.Add("<strong><font color=\"white\">              <a href=\"chattype://" + backPageCmdStr + "\">←←←</a> | <a href=\"chattype://" + forwardPageCmdStr + "\">→→→</a></font></strong>");
                    while (reader.Read()) {
                        long tsSeconds = reader.GetInt64(1);
                        string timestamp = Util.FormatTimestamp(tsSeconds);

                        string? playername = reader.IsDBNull(2) ? null : reader.GetString(2);
                        string? playeruid = reader.IsDBNull(3) ? null : reader.GetString(3);

                        int actiontypeInt = reader.GetInt32(4);
                        string actiontype = ReverseActionTypeMap.TryGetValue(actiontypeInt, out string aType) ? aType : "UNKNOWN";

                        string entityname = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        string entityid = reader.IsDBNull(6) ? "" : reader.GetString(6);

                        byte[]? itemstackData = reader.IsDBNull(7) ? null : (byte[])reader[7];
                        int itemstackEncoding = reader.GetInt32(8);
                        string? itemstack = DecompressText(itemstackData, itemstackEncoding);

                        int logX = reader.GetInt32(9);
                        int logY = reader.GetInt32(10);
                        int logZ = reader.GetInt32(11);

                        string playerStr = playername == null ? "" : "<strong>{1}</strong>({2}) ";
                        string itemstackStr = itemstack == null ? "" : "with {6} ";
                        string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | " + playerStr + "{3} {4}({5}) " + itemstackStr + "@ <strong><font color=\"#9BD1EC\">{7}, {8}, {9}</font></strong>", timestamp, playername, playeruid, actiontype, entityname, entityid, itemstack, logX, logY, logZ);
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No entity logs found.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendEntityLog");
        });
    }

    public void CheckEntityLogWithEntityID(int pageNum, IServerPlayer player, int groupId, string entityID) {
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT e.id, e.timestamp_utc, p.last_playername, p.playeruid, e.actiontype, e.entityname, e.entityid, e.itemstack_data, e.itemstack_encoding, e.x, e.y, e.z FROM (
            SELECT * FROM entitylogs
            WHERE entityid = $entityid
            ORDER BY id DESC
            LIMIT $loglimit
            OFFSET $skiplognum) e
            LEFT JOIN players p ON e.player_id = p.id
            ORDER BY e.id ASC";

            cmd.Parameters.AddWithValue("$entityid", entityID);
            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    int backPageNum = pageNum > 1 ? pageNum - 1 : 1;
                    int forwardPageNum = pageNum + 1;
                    string pageCmdStr = "/entitylog -e " + entityID + " -p ";
                    string backPageCmdStr = pageCmdStr + backPageNum;
                    string forwardPageCmdStr = pageCmdStr + forwardPageNum;

                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
                    logs.Add("<strong><font color=\"white\">              <a href=\"chattype://" + backPageCmdStr + "\">←←←</a> | <a href=\"chattype://" + forwardPageCmdStr + "\">→→→</a></font></strong>");
                    while (reader.Read()) {
                        long tsSeconds = reader.GetInt64(1);
                        string timestamp = Util.FormatTimestamp(tsSeconds);

                        string? playername = reader.IsDBNull(2) ? null : reader.GetString(2);
                        string? playeruid = reader.IsDBNull(3) ? null : reader.GetString(3);

                        int actiontypeInt = reader.GetInt32(4);
                        string actiontype = ReverseActionTypeMap.TryGetValue(actiontypeInt, out string aType) ? aType : "UNKNOWN";

                        string entityname = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        string entityid = reader.IsDBNull(6) ? "" : reader.GetString(6);

                        byte[]? itemstackData = reader.IsDBNull(7) ? null : (byte[])reader[7];
                        int itemstackEncoding = reader.GetInt32(8);
                        string? itemstack = DecompressText(itemstackData, itemstackEncoding);

                        int logX = reader.GetInt32(9);
                        int logY = reader.GetInt32(10);
                        int logZ = reader.GetInt32(11);

                        string playerStr = playername == null ? "" : "<strong>{1}</strong>({2}) ";
                        string itemstackStr = itemstack == null ? "" : "with {6} ";
                        string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | " + playerStr + "{3} {4}({5}) " + itemstackStr + "@ <strong><font color=\"#9BD1EC\">{7}, {8}, {9}</font></strong>", timestamp, playername, playeruid, actiontype, entityname, entityid, itemstack, logX, logY, logZ);
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No entity logs found.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendEntityLogWithEntityID");
        });
    }

    public (int, int, int)? GetLastEntityCoordsLog(string entityID) {
        using var connection = new SqliteConnection("Data Source=" + dbPath);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT x, y, z
        FROM entitylogs
        WHERE entityid = $entityid
        ORDER BY id DESC
        LIMIT 1";

        cmd.Parameters.AddWithValue("$entityid", entityID);

        using var reader = cmd.ExecuteReader();

        if (reader.Read()) {
            int x = reader.GetInt32(0);
            int y = reader.GetInt32(1);
            int z = reader.GetInt32(2);

            return (x, y, z);
        }

        return null;
    }

    public void AddContainerLog(string playername, string playeruid, string actiontype, string containerid, string itemstack, int quantity) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        databaseTasks.Enqueue((connection) => {
            int playerId = GetOrInsertPlayer(connection, playername, playeruid);
            var compressed = CompressText(itemstack);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO containerlogs (timestamp_utc, player_id, containerid, itemstack_data, itemstack_encoding, quantity, actiontype)
            VALUES ($timestamp, $player_id, $containerid, $itemstack_data, $itemstack_encoding, $quantity, $actiontype)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$player_id", playerId == -1 ? DBNull.Value : playerId);
            cmd.Parameters.AddWithValue("$actiontype", ActionTypeMap.TryGetValue(actiontype, out int val) ? val : 5);
            cmd.Parameters.AddWithValue("$containerid", containerid);
            cmd.Parameters.AddWithValue("$itemstack_data", compressed.data ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$itemstack_encoding", compressed.encoding);
            cmd.Parameters.AddWithValue("$quantity", quantity);

            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Records the contents a container had when it was destroyed. Always a BROKE: a
    /// snapshot only exists because something stopped existing.
    /// </summary>
    public void AddContainerSnapshot(string? playername, string? playeruid, string block, string? containerid,
                                     int x, int y, int z, int slotCount, int totalItems, string manifest) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        databaseTasks.Enqueue((connection) => {
            int playerId = GetOrInsertPlayer(connection, playername, playeruid);
            var compressed = CompressText(manifest);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO containersnapshots (timestamp_utc, player_id, actiontype, block, containerid, x, y, z, slot_count, total_items, manifest_data, manifest_encoding)
            VALUES ($timestamp, $player_id, $actiontype, $block, $containerid, $x, $y, $z, $slot_count, $total_items, $manifest_data, $manifest_encoding)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$player_id", playerId == -1 ? DBNull.Value : playerId);
            cmd.Parameters.AddWithValue("$actiontype", (int)ActionType.BROKE);
            cmd.Parameters.AddWithValue("$block", block);
            cmd.Parameters.AddWithValue("$containerid", containerid ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$slot_count", slotCount);
            cmd.Parameters.AddWithValue("$total_items", totalItems);
            cmd.Parameters.AddWithValue("$manifest_data", compressed.data ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$manifest_encoding", compressed.encoding);

            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Records a join or a disconnect. actiontype is JOINED or LEFT.</summary>
    public void AddSessionLog(string? playername, string? playeruid, string actiontype) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        databaseTasks.Enqueue((connection) => {
            int playerId = GetOrInsertPlayer(connection, playername, playeruid);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO sessions (timestamp_utc, player_id, actiontype)
            VALUES ($timestamp, $player_id, $actiontype)";

            cmd.Parameters.AddWithValue("$timestamp", timestamp);
            cmd.Parameters.AddWithValue("$player_id", playerId == -1 ? DBNull.Value : playerId);
            cmd.Parameters.AddWithValue("$actiontype", ActionTypeMap.TryGetValue(actiontype, out int val) ? val : (int)ActionType.JOINED);

            cmd.ExecuteNonQuery();
        });
    }


    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, string containerid, long sinceUnix = 0) {
        CheckContainerLog(pageNum, player, groupId, new List<string> { containerid }, sinceUnix);
    }

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, List<string> containerids, long sinceUnix = 0) {
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();
            int skipLogsNum = logLimit * (pageNum - 1);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < containerids.Count; i++) {
                sb.Append("'").Append(containerids[i].Replace("'", "''")).Append("'");
                if (i < containerids.Count - 1)
                    sb.Append(" OR containerid = ");
            }
            string containerIDsQueryStr = sb.ToString();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT c.id, c.timestamp_utc, p.last_playername, p.playeruid, c.containerid, c.itemstack_data, c.itemstack_encoding, c.quantity, c.actiontype FROM (SELECT * FROM containerlogs WHERE (containerid = " + containerIDsQueryStr +
                ") AND timestamp_utc >= $since ORDER BY id DESC LIMIT $loglimit OFFSET $skiplognum) c LEFT JOIN players p ON c.player_id = p.id ORDER BY c.id ASC";

            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);
            cmd.Parameters.AddWithValue("$since", sinceUnix);

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    int backPageNum = pageNum > 1 ? pageNum - 1 : 1;
                    int forwardPageNum = pageNum + 1;
                    string pageCmdStr = "/containerlog -p ";
                    string backPageCmdStr = pageCmdStr + backPageNum;
                    string forwardPageCmdStr = pageCmdStr + forwardPageNum;

                    logs.Add("<strong><font color=\"white\">---------- PAGE " + pageNum + " ----------</font></strong>");
                    logs.Add("<strong><font color=\"white\">              <a href=\"chattype://" + backPageCmdStr + "\">←←←</a> | <a href=\"chattype://" + forwardPageCmdStr + "\">→→→</a></font></strong>");
                    while (reader.Read()) {
                        long tsSeconds = reader.GetInt64(1);
                        string timestamp = Util.FormatTimestamp(tsSeconds);

                        string logPlayername = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2);
                        string logPlayeruid = reader.IsDBNull(3) ? "Unknown" : reader.GetString(3);

                        string logContainerid = reader.IsDBNull(4) ? "" : reader.GetString(4);

                        byte[]? itemstackData = reader.IsDBNull(5) ? null : (byte[])reader[5];
                        int itemstackEncoding = reader.GetInt32(6);
                        string itemstack = DecompressText(itemstackData, itemstackEncoding) ?? "";

                        int quantity = reader.GetInt32(7);

                        int actiontypeInt = reader.IsDBNull(8) ? 5 : reader.GetInt32(8); // 5 is TAKEN
                        string actiontype = ReverseActionTypeMap.TryGetValue(actiontypeInt, out string aType) ? aType : "TAKEN";

                        string logString = String.Format("<strong><font color=\"#6F88DB\">{0}</font></strong> | <strong>{1}</strong>({2}) {6} {5}x{4} in <strong><font color=\"#9BD1EC\">{3}</font></strong>", timestamp, logPlayername, logPlayeruid, logContainerid, itemstack, quantity, actiontype);
                        logs.Add(logString);
                    }
                }
                else {
                    logs.Add("No container logs found.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendContainerLog");
        });
    }

    /// <summary>
    /// Queue maintenance work onto the existing writer thread.
    ///
    /// Retention has to run on this connection rather than opening its own. The worker is
    /// the only writer, so anything queued here is serialised against every insert for free;
    /// a second write connection would instead contend with it and produce SQLITE_BUSY under
    /// exactly the load that makes pruning worth doing.
    /// </summary>
    public void EnqueueMaintenance(Action<SqliteConnection> task) {
        databaseTasks.Enqueue(task);
    }

    /// <summary>
    /// Writes a consistent copy of the whole database to snapshot.db beside it, for anything
    /// that wants to read the log off-server.
    ///
    /// Copying database.db while the server runs does not work and does not fail cleanly.
    /// WAL mode means the newest events live in the -wal sidecar, so a copy of the main file
    /// alone is quietly hours stale; copying both catches them at different instants, and a
    /// copy taken mid-checkpoint has a header page count lower than the file's real length,
    /// which presents as "database disk image is malformed" on whichever table happened to
    /// be growing. That exact failure cost an investigation on 2026-10-08 a hand-patched
    /// header before the container log would open at all.
    ///
    /// VACUUM INTO is the supported answer: one statement, one consistent file, the wal
    /// already folded in. It runs on the writer thread, so it is serialised against every
    /// insert for free.
    ///
    /// Written to a temporary name and moved into place, so a puller can never catch a
    /// half-written snapshot. VACUUM INTO refuses to write to a file that already exists,
    /// which the temp name also sidesteps.
    /// </summary>
    public void Snapshot(Action<string> report) {
        string target = Path.Combine(Path.GetDirectoryName(dbPath)!, "snapshot.db");
        string temp = target + ".tmp";

        EnqueueMaintenance(connection => {
            try {
                if (File.Exists(temp)) File.Delete(temp);

                using (var cmd = connection.CreateCommand()) {
                    // Parameters are not allowed in VACUUM INTO, so the path is quoted by
                    // doubling any single quote. It is a server-local path built here, never
                    // anything a player supplied.
                    cmd.CommandText = "VACUUM INTO '" + temp.Replace("'", "''") + "'";
                    cmd.ExecuteNonQuery();
                }

                File.Move(temp, target, true);
                long bytes = new FileInfo(target).Length;
                report($"Snapshot written: {target} ({bytes / 1048576.0:0.0} MB). Pull this rather than database.db.");
                Main.API.Logger.Notification($"GriefWarden: snapshot written ({bytes} bytes)");
            }
            catch (Exception ex) {
                // VACUUM INTO needs SQLite 3.27 or newer. If the bundled provider is older
                // this is where that shows up, and saying so beats a silent absence.
                Main.API.Logger.Error("GriefWarden: snapshot failed: " + ex);
                report("Snapshot failed: " + ex.Message);
            }
        });
    }

    /// <summary>Current file size on disk, for the retention size backstop.</summary>
    public long FileSizeBytes() {
        try {
            return new FileInfo(dbPath).Length;
        }
        catch {
            return 0;
        }
    }

    public void Dispose() {
        cancellationTokenSource.Cancel();
        workerThread.Join(5000); // Wait up to 5 seconds for worker to finish
        cancellationTokenSource.Dispose();
    }
}