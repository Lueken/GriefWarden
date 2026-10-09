using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace GriefWarden;

public class Commands {
    public Commands() {
        Main.API.Permissions.RegisterPrivilege("griefwarden", "Use GriefWarden commands.", true);

        //Main.API.ChatCommands.Create("blocklog").WithDescription("Inspect block logs at block looked at.").RequiresPrivilege("griefwarden").HandleWith(new CommandDel(this.OnBlockLogCommand));
        Main.API.RegisterCommand("rollbackbreaks", "Preview, then optionally revert, BROKE changes by one player in a radius. Previews by default; --apply commits and cannot be undone.", "-p USERNAME [-r 5] [-t 24h] [--apply]", new ServerChatCommandDelegate(this.OnRollbackBreaksCommand), "griefwarden");
        Main.API.RegisterCommand("blocklog", "Inspect block logs at block looked at if no radius is specified, or around the player if radius is.", "-r # -p # [-t 6h]", new ServerChatCommandDelegate(this.OnBlockLogCommand), "griefwarden");
        Main.API.RegisterCommand("entitylog", "Inspect entity logs in radius around you.", "(-r # OR -e ENTITYID) -p # [-t 6h]", new ServerChatCommandDelegate(this.OnEntityLogCommand), "griefwarden");
        Main.API.RegisterCommand("containerlog", "Inspect container logs at container looked at. Resolves both halves of a double chest.", "-p # [-t 6h]", new ServerChatCommandDelegate(this.OnContainerLogCommand), "griefwarden");
        Main.API.RegisterCommand("tpboatid", "Performs a sequence of events to teleport a boat to you.", "-e ENTITYID", new ServerChatCommandDelegate(this.OnTPBoatID), "griefwarden");
        Main.API.RegisterCommand("griefwarden", "GriefWarden status, retention control and consistent snapshots.", "status | prune | snapshot | vacuum | fire", new ServerChatCommandDelegate(this.OnGriefWardenCommand), "griefwarden");

        // The question every dispute actually opens with, which had no command at all.
        Main.API.RegisterCommand("playerlog", "Everything one player did, newest first. Merges blocks, containers, kills and logins into one timeline.", "USERNAME [-t 6h] [-n page] [-sum] [-a BROKE,TAKEN]", new ServerChatCommandDelegate(this.OnPlayerLogCommand), "griefwarden");
        Main.API.RegisterCommand("itemlog", "Find an item by name across every container transaction in the window.", "ITEM NAME [-t 7d] [-n page]", new ServerChatCommandDelegate(this.OnItemLogCommand), "griefwarden");
        Main.API.RegisterCommand("wasonline", "Whether a player was connected at a given moment. Answers from the session log.", "USERNAME WHEN   (WHEN is 90m, 6h, 14:35, or 2026-10-08 14:35)", new ServerChatCommandDelegate(this.OnWasOnlineCommand), "griefwarden");
    }

    /// <summary>
    /// Shared window parsing.
    ///
    /// -t takes 90m, 6h, 3d, 2w, or a bare number meaning hours. Returns the unix second to
    /// search back to. A window is applied whether or not one was asked for, because an
    /// unbounded default is how these commands became unusable: the alternative is paging
    /// through three months of history to reach this morning.
    /// </summary>
    private long WindowStart(string? spec, IServerPlayer player, int groupId, out bool valid) {
        valid = true;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (string.IsNullOrWhiteSpace(spec)) {
            return now - (long)Main.Config.DefaultWindowHours * 3600;
        }

        long? seconds = Util.ParseDuration(spec);
        if (seconds == null) {
            Main.API.SendMessage(player, groupId,
                $"\"{spec}\" is not a window. Use 90m, 6h, 3d or 2w.", EnumChatType.CommandError);
            valid = false;
            return now;
        }
        return now - seconds.Value;
    }

    private void OnPlayerLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string? target = null;
        string? window = null;
        int pageNum = 1;
        bool summary = false;
        string? actions = null;

        while (args.Length > 0) {
            string word = args.PopWord();
            switch (word) {
                case "-t": window = args.PopWord(); break;
                case "-n": pageNum = (int)args.PopInt(1); break;
                case "-sum": summary = true; break;
                case "-a": actions = args.PopWord(); break;
                default:
                    // First bare word is the player. A second one is almost always a typo
                    // such as a stray flag, and silently ignoring it would search for the
                    // wrong person.
                    if (target == null) target = word;
                    else {
                        Main.API.SendMessage(player, groupId,
                            $"Unexpected argument \"{word}\". Usage: /playerlog USERNAME [-t 6h] [-n page] [-sum]",
                            EnumChatType.CommandError);
                        return;
                    }
                    break;
            }
        }

        if (target == null) {
            Main.API.SendMessage(player, groupId, "Which player? /playerlog USERNAME [-t 6h] [-n page] [-sum]", EnumChatType.CommandError);
            return;
        }

        long since = WindowStart(window, player, groupId, out bool ok);
        if (!ok) return;
        if (pageNum < 1) pageNum = 1;

        int[]? actionFilter = ParseActions(actions, player, groupId, out bool actionsOk);
        if (!actionsOk) return;

        Main.Queries.PlayerLog(player, groupId, target, since, pageNum, summary, actionFilter);
    }

    /// <summary>
    /// Turns "BROKE,TAKEN" into action ids, refusing names it does not know rather than
    /// quietly dropping them. A silently ignored filter returns a complete timeline that
    /// the admin reads as a filtered one, which is worse than an error.
    /// </summary>
    private int[]? ParseActions(string? spec, IServerPlayer player, int groupId, out bool valid) {
        valid = true;
        if (string.IsNullOrWhiteSpace(spec)) return null;

        var ids = new List<int>();
        foreach (string piece in spec.Split(',')) {
            string name = piece.Trim().ToUpperInvariant();
            if (name.Length == 0) continue;

            if (Enum.TryParse(name, out Database.ActionType parsed)) {
                ids.Add((int)parsed);
                continue;
            }

            Main.API.SendMessage(player, groupId,
                $"\"{piece.Trim()}\" is not an action. Known: {string.Join(", ", Enum.GetNames(typeof(Database.ActionType)))}",
                EnumChatType.CommandError);
            valid = false;
            return null;
        }
        return ids.Count == 0 ? null : ids.ToArray();
    }

    private void OnItemLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        var words = new List<string>();
        string? window = null;
        int pageNum = 1;

        while (args.Length > 0) {
            string word = args.PopWord();
            switch (word) {
                case "-t": window = args.PopWord(); break;
                case "-n": pageNum = (int)args.PopInt(1); break;
                // Item names are several words long far more often than not, so everything
                // that is not a flag is part of the search text.
                default: words.Add(word); break;
            }
        }

        if (words.Count == 0) {
            Main.API.SendMessage(player, groupId, "Which item? /itemlog Runed wand [-t 7d]", EnumChatType.CommandError);
            return;
        }

        long since = WindowStart(window, player, groupId, out bool ok);
        if (!ok) return;
        if (pageNum < 1) pageNum = 1;

        Main.Queries.ItemLog(player, groupId, string.Join(" ", words), since, pageNum);
    }

    private void OnWasOnlineCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string? target = args.PopWord();
        if (target == null) {
            Main.API.SendMessage(player, groupId, "Usage: /wasonline USERNAME WHEN", EnumChatType.CommandError);
            return;
        }

        var rest = new List<string>();
        while (args.Length > 0) rest.Add(args.PopWord());
        if (rest.Count == 0) {
            Main.API.SendMessage(player, groupId,
                "When? /wasonline USERNAME 6h  (or 14:35, or \"2026-10-08 14:35\")", EnumChatType.CommandError);
            return;
        }

        long? at = ParseMoment(string.Join(" ", rest));
        if (at == null) {
            Main.API.SendMessage(player, groupId,
                $"Could not read \"{string.Join(" ", rest)}\" as a time. Use 6h, 90m, 14:35, or 2026-10-08 14:35.",
                EnumChatType.CommandError);
            return;
        }

        Main.Queries.PresenceAt(player, groupId, target, at.Value);
    }

    /// <summary>
    /// Reads a moment three ways, because an admin reaching for this has the time in
    /// whichever form they were just handed: "6h" from their own memory of how long ago,
    /// "14:35" off a chat log, or a full date from a screenshot.
    ///
    /// Clock-time forms are interpreted in the configured display offset, not UTC. Anything
    /// else would silently answer about a different moment than the one the admin typed,
    /// which on this server is seven hours away from the one they meant.
    /// </summary>
    private long? ParseMoment(string text) {
        text = text.Trim();

        long? ago = Util.ParseDuration(text);
        if (ago != null) return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ago.Value;

        TimeSpan offset = TimeSpan.FromMinutes(Main.Config.DisplayUtcOffsetMinutes);

        if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime parsed)) {
            return new DateTimeOffset(parsed, offset).ToUnixTimeSeconds();
        }

        if (DateTime.TryParseExact(text, new[] { "HH:mm", "H:mm", "HH:mm:ss" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime timeOnly)) {
            // A bare clock time means today in the display timezone. If that lands in the
            // future it meant yesterday, which is what "was he on at 23:50" means when
            // asked at breakfast.
            DateTimeOffset nowLocal = DateTimeOffset.UtcNow.ToOffset(offset);
            var candidate = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day,
                timeOnly.Hour, timeOnly.Minute, timeOnly.Second, offset);
            if (candidate > nowLocal) candidate = candidate.AddDays(-1);
            return candidate.ToUnixTimeSeconds();
        }

        return null;
    }

    /// <summary>
    /// Status and manual retention control, so the policy can be inspected and forced without
    /// a restart.
    ///
    /// Vacuum is manual on purpose. It rewrites the whole database and needs roughly double
    /// the file size in scratch, which is a visible hitch on a live server and not something
    /// that belongs on a timer.
    /// </summary>
    private void OnGriefWardenCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string sub = (args.PopWord() ?? "status").ToLowerInvariant();

        switch (sub) {
            case "status":
                Main.API.SendMessage(player, groupId, Main.Retention.Describe(), EnumChatType.CommandSuccess);
                Main.API.SendMessage(player, groupId, Main.FireGuard.Describe(), EnumChatType.CommandSuccess);
                return;

            case "prune":
                Main.Retention.Run("command");
                Main.API.SendMessage(player, groupId, "Retention pass queued. server-main.log records what it removed.", EnumChatType.CommandSuccess);
                return;

            case "fire":
                Main.API.SendMessage(player, groupId, Main.FireGuard.Describe(), EnumChatType.CommandSuccess);
                return;

            case "snapshot":
                Main.API.SendMessage(player, groupId, "Writing a consistent snapshot beside the database...", EnumChatType.CommandSuccess);
                Main.Database.Snapshot(line => Main.API.Event.EnqueueMainThreadTask(
                    () => Main.API.SendMessage(player, groupId, line, EnumChatType.CommandSuccess),
                    "GriefWardenSnapshotMsg"));
                return;

            case "vacuum":
                Main.API.SendMessage(player, groupId, "Vacuum queued. The server may hitch while the database is rewritten.", EnumChatType.CommandSuccess);
                Main.Retention.RunVacuum("command");
                return;

            default:
                Main.API.SendMessage(player, groupId, "Usage: /griefwarden status | prune | snapshot | vacuum | fire", EnumChatType.CommandError);
                return;
        }
    }

    /// <summary>
    /// Previews a rollback unless told to commit.
    ///
    /// The default used to be to act, with no window and no report. Previewing by default
    /// costs a second and is the only reason this is safe to point at a real incident: a
    /// builder demolishing their own wing shows up in the preview as two hundred of their
    /// own blocks, which is the signal to narrow the window rather than to proceed.
    /// </summary>
    private void OnRollbackBreaksCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string? playerName = null;
        string? window = null;
        int radiusToUse = 5;
        bool apply = false;

        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    playerName = args.PopWord();
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
                case "-t":
                    window = args.PopWord();
                    break;
                case "--apply":
                    apply = true;
                    break;
            }
        }

        if (playerName == null) {
            Main.API.SendMessage(player, groupId,
                "You need to specify a player's username with \"-p USERNAME\". Previews by default; add --apply to commit.",
                EnumChatType.CommandError);
            return;
        }

        long since = WindowStart(window, player, groupId, out bool ok);
        if (!ok) return;
        if (radiusToUse < 1) radiusToUse = 1;

        Main.Rollback.Run(player, groupId, playerName, radiusToUse, since, apply);
    }

    private void OnBlockLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        //int radiusToUse = (int)args.PopInt(0);
        int pageNum = 1;
        int radiusToUse = 0;
        string? window = null;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(0);
                    break;
                case "-t":
                    window = args.PopWord();
                    break;
            }
        }

        long since = WindowStart(window, player, groupId, out bool windowOk);
        if (!windowOk) return;

        Vec3i positionToUse;
        if (radiusToUse > 0) {
            positionToUse = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        }
        else {
            BlockSelection blockSel = player.CurrentBlockSelection;
            if (blockSel == null) {
                Main.API.SendMessage(player, groupId, "Look at a block first or specify a radius.", EnumChatType.CommandError);
                return;
            }
            positionToUse = blockSel.Position.ToLocalPosition(Main.API);
        }

        Main.Database.CheckBlockLog(pageNum, player, groupId, positionToUse.X, positionToUse.Y, positionToUse.Z, radiusToUse, since);
    }

    private void OnEntityLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        int radiusToUse = 5;
        string? entityID = null;
        string? window = null;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
                case "-e":
                    entityID = args.PopWord();
                    break;
                case "-t":
                    window = args.PopWord();
                    break;
            }
        }

        long since = WindowStart(window, player, groupId, out bool windowOk);
        if (!windowOk) return;

        if (entityID == null) {
            Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

            Main.Database.CheckEntityLog(pageNum, player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse, since);
        }
        else {
            Main.Database.CheckEntityLogWithEntityID(pageNum, player, groupId, entityID);
        }
    }

    private void OnContainerLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        string? window = null;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-t":
                    window = args.PopWord();
                    break;
            }
        }

        long since = WindowStart(window, player, groupId, out bool windowOk);
        if (!windowOk) return;

        BlockSelection blockSel = player.CurrentBlockSelection;
        if (blockSel != null) {
            List<string> ids = ContainerIdsAt(blockSel.Position, out string? note);
            if (ids.Count > 0) {
                if (note != null) Main.API.SendMessage(player, groupId, note, EnumChatType.CommandSuccess);
                Main.Database.CheckContainerLog(pageNum, player, groupId, ids, since);
                return;
            }
        }

        // mountedbaginv-(slotnum)-(entityID)
        // elks have slot num 6 for saddlebags
        // sailboats have slot nums 5-12 for chests
        // rafts have slot nums 0-1 for chests
        EntitySelection entitySel = player.CurrentEntitySelection;
        if (entitySel != null) {
            var behavior = entitySel.Entity.GetBehavior<EntityBehaviorAttachable>();
            if (behavior != null) {
                InventoryBase inventory = behavior.Inventory;
                List<string> containerids = new();
                for (int i = 0; i < inventory.Count; i++)
                    containerids.Add("mountedbaginv-" + i + "-" + entitySel.Entity.EntityId);
                Main.Database.CheckContainerLog(pageNum, player, groupId, containerids, since);
                return;
            }
        }

        // Both halves of a double chest are resolved now, so that is no longer the
        // likely cause and the old hint pointed admins at a dead end.
        Main.API.SendMessage(player, groupId, "Look at a container, or at an animal or boat that carries one. Nothing at that block has an inventory.", EnumChatType.CommandError);
    }

    /// <summary>
    /// Every container inventory id reachable from the block being looked at, including the
    /// other half of a double chest or trunk.
    ///
    /// The old behaviour was to read the one block and, on a miss, print "if you're looking
    /// at a double chest/trunk, try the other block". That message is an admission: half the
    /// history of a large chest was unreachable unless the admin guessed which half held it,
    /// and the half they are looking at is not the half the inventory lives in.
    ///
    /// Neighbours are matched on identical block code, which can also pick up two single
    /// chests placed side by side. That over-inclusion is announced rather than hidden,
    /// because a merged row that says where it came from costs an admin a second, and a
    /// silently missing half costs them the case.
    /// </summary>
    private List<string> ContainerIdsAt(BlockPos pos, out string? note) {
        note = null;
        var ids = new List<string>();

        string? primary = ContainerIdOf(pos);
        if (primary == null) return ids;
        ids.Add(primary);

        Block block = Main.API.World.BlockAccessor.GetBlock(pos);
        string code = block?.Code?.ToString() ?? "";
        var merged = new List<string>();

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS) {
            BlockPos neighbour = pos.AddCopy(facing);
            Block other = Main.API.World.BlockAccessor.GetBlock(neighbour);
            if (other?.Code?.ToString() != code) continue;

            string? id = ContainerIdOf(neighbour);
            if (id == null || id == primary || ids.Contains(id)) continue;

            ids.Add(id);
            merged.Add($"{id} ({facing.Code})");
        }

        if (merged.Count > 0) {
            note = $"<font color=\"#D9A05B\">Also reading {merged.Count} adjacent {code}: {string.Join(", ", merged)}</font>";
        }
        return ids;
    }

    /// <summary>
    /// Toolracks do not implement IBlockEntityContainer, which is why they need naming
    /// separately here as well as in the snapshot path.
    /// </summary>
    private string? ContainerIdOf(BlockPos pos) {
        BlockEntity blockEnt = Main.API.World.BlockAccessor.GetBlockEntity(pos);
        if (blockEnt == null) return null;

        if (blockEnt is IBlockEntityContainer container) return container.Inventory?.InventoryID;
        if (blockEnt is BlockEntityToolrack rack) return rack.inventory?.InventoryID;
        return null;
    }

    private void tryTPEntityAsBoat(IServerPlayer player, int groupId, Entity entityToTP) {
        if (entityToTP is EntityBoat boatEntity) {
            boatEntity.TeleportTo(player.Entity.Pos.XYZ);
            Main.API.SendMessage(player, groupId, "Teleported boat with ID " + boatEntity.EntityId + " to your position.", EnumChatType.CommandSuccess);
            return;
        }
        Main.API.SendMessage(player, groupId, "That entity is not a boat.", EnumChatType.CommandError);
    }
    private void OnTPBoatID(IServerPlayer player, int groupId, CmdArgs args) {
        long entityID = 0;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-e":
                    entityID = Convert.ToInt64(args.PopWord());
                    break;
            }
        }
        if (entityID == 0) {
            Main.API.SendMessage(player, groupId, "Could not convert to proper entity ID. Proper usage: /tpboatid -e ENTITYID", EnumChatType.CommandError);
            return;
        }

        if (Main.API.World.LoadedEntities.ContainsKey(entityID)) {
            Entity entityToTP = Main.API.World.LoadedEntities[entityID];

            tryTPEntityAsBoat(player, groupId, entityToTP);
        }
        else {
            (int, int, int)? rawEntityPosition = Main.Database.GetLastEntityCoordsLog(entityID.ToString());
            if (rawEntityPosition == null) {
                Main.API.SendMessage(player, groupId, "No entity logs found with that ID. Did you enter the ID in wrong?", EnumChatType.CommandError);
                return;
            }

            Vec3d entityPosition = new(rawEntityPosition.Value.Item1 + Main.API.World.DefaultSpawnPosition.X, rawEntityPosition.Value.Item2, rawEntityPosition.Value.Item3 + Main.API.World.DefaultSpawnPosition.Z);

            Main.API.WorldManager.LoadChunkColumnPriority((int)entityPosition.X / 32, (int)entityPosition.Z / 32, new ChunkLoadOptions {
                OnLoaded = () => {
                    // Check again to see if entity is loaded, just in case
                    if (!Main.API.World.LoadedEntities.ContainsKey(entityID)) {
                        Main.API.SendMessage(player, groupId, "Entity position found, but entity still not loaded in. Something is wrong.", EnumChatType.CommandError);
                        return;
                    }

                    Entity entityToTP = Main.API.World.LoadedEntities[entityID];

                    tryTPEntityAsBoat(player, groupId, entityToTP);
                }
            });
        }
    }
}
