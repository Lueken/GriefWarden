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
    private int logLimit = 4;

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
        DESPAWNED = 9
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
        { "DESPAWNED", (int)ActionType.DESPAWNED }
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
        { (int)ActionType.DESPAWNED, "DESPAWNED" }
    };

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

    public void RollbackBreaks(IServerPlayer player, int groupId, int x, int y, int z, int radius, string playername) {
        // Read on a separate thread/connection to not block main thread
        System.Threading.Tasks.Task.Run(() => {
            using var connection = new SqliteConnection("Data Source=" + dbPath);
            connection.Open();

            using var cmd = connection.CreateCommand();

            long playerid = -1;
            cmd.CommandText = @"SELECT id FROM players
            WHERE last_playername = $playername";
            cmd.Parameters.AddWithValue("$playername", playername);
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    while (reader.Read()) {
                        playerid = reader.GetInt64(0);
                        break;
                    }
                }
                else {
                    Main.API.Event.EnqueueMainThreadTask(() => {
                        Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, "No player logged with that username.", EnumChatType.CommandSuccess);
                    }, "SendRollbackFail");
                    return;
                }
            }

            cmd.CommandText = @"SELECT * FROM blocklogs
            WHERE player_id = $playerid
            AND actiontype = 0
            AND x BETWEEN $x - $radius AND $x + $radius
            AND y BETWEEN $y - $radius AND $y + $radius
            AND z BETWEEN $z - $radius AND $z + $radius";

            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$radius", radius);
            cmd.Parameters.AddWithValue("$playerid", playerid);

            Dictionary<string, (BlockPos, long, int)> rollbackToSet = new();

            var logs = new List<string>();
            using (var reader = cmd.ExecuteReader()) {
                if (reader.HasRows) {
                    while (reader.Read()) {
                        long tsSeconds = reader.GetInt64(1);

                        string block = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        if (block.Contains("chiseled"))
                            continue;

                        int indexOfIDChar = block.IndexOf('/');
                        if (indexOfIDChar == -1)
                            continue;
                        string blockIDStr = block.Substring(indexOfIDChar + 1, block.Length - indexOfIDChar - 1);

                        int blockID = Convert.ToInt32(blockIDStr);
                        if (blockID == 0)
                            continue;

                        int logX = reader.GetInt32(7);
                        int logY = reader.GetInt32(8);
                        int logZ = reader.GetInt32(9);

                        string currentRollbackValueKey = logX + "|" + logY + "|" + logZ;
                        try {
                            (BlockPos, long, int) currentRollbackValue = rollbackToSet[currentRollbackValueKey];
                            if (currentRollbackValue.Item2 > tsSeconds) {
                                BlockPos savedBlockPos = rollbackToSet[currentRollbackValueKey].Item1;
                                rollbackToSet[currentRollbackValueKey] = (savedBlockPos, tsSeconds, blockID);
                            }
                        }
                        catch (KeyNotFoundException) {
                            BlockPos blockPos = new BlockPos(logX + (int)Main.API.World.DefaultSpawnPosition.X, logY, logZ + (int)Main.API.World.DefaultSpawnPosition.Z);
                            rollbackToSet[currentRollbackValueKey] = (blockPos, tsSeconds, blockID);
                        }
                    }
                    logs.Add("Rolled back blocks broken by " + playername + " in a radius of " + radius + ".");
                }
                else {
                    logs.Add("Nothing to rollback.");
                }
            }

            Main.API.Event.EnqueueMainThreadTask(() => {
                foreach (KeyValuePair<string, (BlockPos, long, int)> entry in rollbackToSet) {
                    Main.API.World.BlockAccessor.SetBlock(entry.Value.Item3, entry.Value.Item1);
                }

                foreach (var log in logs) {
                    Main.API.SendMessage(player, GlobalConstants.InfoLogChatGroup, log, EnumChatType.CommandSuccess);
                }
            }, "SendRollback");
        });
    }

    public void CheckBlockLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
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
                        string timestamp = DateTimeOffset.FromUnixTimeSeconds(tsSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

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

    public void CheckEntityLog(int pageNum, IServerPlayer player, int groupId, int x, int y, int z, int radius) {
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
                        string timestamp = DateTimeOffset.FromUnixTimeSeconds(tsSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

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
                        string timestamp = DateTimeOffset.FromUnixTimeSeconds(tsSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

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

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, string containerid) {
        CheckContainerLog(pageNum, player, groupId, new List<string> { containerid });
    }

    public void CheckContainerLog(int pageNum, IServerPlayer player, int groupId, List<string> containerids) {
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
            cmd.CommandText = "SELECT c.id, c.timestamp_utc, p.last_playername, p.playeruid, c.containerid, c.itemstack_data, c.itemstack_encoding, c.quantity, c.actiontype FROM (SELECT * FROM containerlogs WHERE containerid = " + containerIDsQueryStr +
                " ORDER BY id DESC LIMIT $loglimit OFFSET $skiplognum) c LEFT JOIN players p ON c.player_id = p.id ORDER BY c.id ASC";

            cmd.Parameters.AddWithValue("$loglimit", logLimit);
            cmd.Parameters.AddWithValue("$skiplognum", skipLogsNum);

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
                        string timestamp = DateTimeOffset.FromUnixTimeSeconds(tsSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

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

    public void Dispose() {
        cancellationTokenSource.Cancel();
        workerThread.Join(5000); // Wait up to 5 seconds for worker to finish
        cancellationTokenSource.Dispose();
    }
}