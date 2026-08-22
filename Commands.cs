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
        Main.API.RegisterCommand("rollbackbreaks", "Revert BROKE changes made by a specific player in a radius. If no radius is specified, it defaults to 5.", "-p USERNAME -r #", new ServerChatCommandDelegate(this.OnRollbackBreaksCommand), "griefwarden");
        Main.API.RegisterCommand("blocklog", "Inspect block logs at block looked at if no radius is specified, or around the player if radius is.", "-r # -p #", new ServerChatCommandDelegate(this.OnBlockLogCommand), "griefwarden");
        Main.API.RegisterCommand("entitylog", "Inspect entity logs in radius around you.", "(-r # OR -e ENTITYID) -p #", new ServerChatCommandDelegate(this.OnEntityLogCommand), "griefwarden");
        Main.API.RegisterCommand("containerlog", "Inspect container logs at container looked at.", "-p #", new ServerChatCommandDelegate(this.OnContainerLogCommand), "griefwarden");
        Main.API.RegisterCommand("tpboatid", "Performs a sequence of events to teleport a boat to you.", "-e ENTITYID", new ServerChatCommandDelegate(this.OnTPBoatID), "griefwarden");
        Main.API.RegisterCommand("griefwarden", "GriefWarden status and retention control.", "status | prune | vacuum", new ServerChatCommandDelegate(this.OnGriefWardenCommand), "griefwarden");
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
                return;

            case "prune":
                Main.Retention.Run("command");
                Main.API.SendMessage(player, groupId, "Retention pass queued. server-main.log records what it removed.", EnumChatType.CommandSuccess);
                return;

            case "vacuum":
                Main.API.SendMessage(player, groupId, "Vacuum queued. The server may hitch while the database is rewritten.", EnumChatType.CommandSuccess);
                Main.Retention.RunVacuum("command");
                return;

            default:
                Main.API.SendMessage(player, groupId, "Usage: /griefwarden status | prune | vacuum", EnumChatType.CommandError);
                return;
        }
    }

    private void OnRollbackBreaksCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string? playerName = null;
        int radiusToUse = 5;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    playerName = args.PopWord();
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
            }
        }
        if (playerName == null) {
            Main.API.SendMessage(player, groupId, "You need to specify a player's username with \"-p USERNAME\".", EnumChatType.CommandError);
            return;
        }

        Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

        Main.Database.RollbackBreaks(player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse, playerName);
    }

    private void OnBlockLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        //int radiusToUse = (int)args.PopInt(0);
        int pageNum = 1;
        int radiusToUse = 0;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(0);
                    break;
            }
        }

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

        Main.Database.CheckBlockLog(pageNum, player, groupId, positionToUse.X, positionToUse.Y, positionToUse.Z, radiusToUse);
    }

    private void OnEntityLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        int radiusToUse = 5;
        string? entityID = null;
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
            }
        }

        if (entityID == null) {
            Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

            Main.Database.CheckEntityLog(pageNum, player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse);
        }
        else {
            Main.Database.CheckEntityLogWithEntityID(pageNum, player, groupId, entityID);
        }
    }

    private void OnContainerLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
            }
        }

        BlockSelection blockSel = player.CurrentBlockSelection;
        if (blockSel != null) {
            BlockEntity blockEnt = Main.API.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEnt is IBlockEntityContainer container) {
                IInventory inventory = container.Inventory;
                Main.Database.CheckContainerLog(pageNum, player, groupId, inventory.InventoryID);
                return;
            }
            // Special case for toolracks
            if (blockEnt is BlockEntityToolrack toolRack) {
                IInventory inventory = toolRack.inventory;
                Main.Database.CheckContainerLog(pageNum, player, groupId, inventory.InventoryID);
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
                Main.Database.CheckContainerLog(pageNum, player, groupId, containerids);
                return;
            }
        }

        Main.API.SendMessage(player, groupId, "Look at a container, or an entity that can have a container, first. If you're looking at a double chest/trunk, try the other block.", EnumChatType.CommandError);
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
