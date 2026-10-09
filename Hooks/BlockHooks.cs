using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace GriefWarden.Hooks;

public class BlockHooks {
    public BlockHooks() {
        // BreakBlock fires while the block entity is still there, which is the only moment
        // a container's contents can still be read. DidBreakBlock is too late: by then the
        // inventory is gone and the stacks are loose items on the floor.
        Main.API.Event.BreakBlock += this.OnBreakBlock;

        Main.API.Event.DidBreakBlock += this.OnDidBlockBreak;
        Main.API.Event.DidPlaceBlock += this.OnDidBlockPlace;
        Main.API.Event.DidUseBlock += this.OnDidBlockUse;
    }

    /// <summary>
    /// Observes an imminent break and takes a contents snapshot, held in memory until the
    /// break is confirmed.
    ///
    /// Nothing here is assigned. dropQuantityMultiplier and handling arrive by reference
    /// because this event exists for mods that want to change the outcome; a log that
    /// changed either one would be altering the world it claims to be recording.
    /// </summary>
    private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling) {
        if (blockSel?.Position == null) return;
        ContainerSnapshot.Capture(blockSel.Position);
    }

    private void OnDidBlockBreak(IServerPlayer player, int oldBlockID, BlockSelection blockSel) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(oldBlockID);

        string? playerName = null;
        string? playerUID = null;
        string? itemstack = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;

            itemstack = Util.GetPlayerCurrentItemstackName(player);
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);

        Main.Database.AddBlockLog(playerName, playerUID, "BROKE", block.ToString(), itemstack, blockPosition.X, blockPosition.Y, blockPosition.Z, oldBlockID);

        // Only now is the break real. A manifest written any earlier would survive a
        // cancelled break and read as a destroyed chest for the next ninety days.
        ContainerSnapshot.Commit(blockSel.Position, playerName, playerUID, blockPosition.X, blockPosition.Y, blockPosition.Z);
    }

    private void OnDidBlockPlace(IServerPlayer player, int oldBlockID, BlockSelection blockSel, ItemStack withItemStack) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(blockSel.Position);

        string? playerName = null;
        string? playerUID = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
        Main.Database.AddBlockLog(playerName, playerUID, "PLACED", block.ToString(), null, blockPosition.X, blockPosition.Y, blockPosition.Z, oldBlockID);
    }

    private void OnDidBlockUse(IServerPlayer player, BlockSelection blockSel) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(blockSel.Position);
        if (block == null)
            return;

        // Check if translocator
        if (!block.ToString().Contains("statictranslocator"))
            return;

        string? playerName = null;
        string? playerUID = null;
        string? itemstack = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;

            itemstack = Util.GetPlayerCurrentItemstackName(player);
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
        Main.Database.AddBlockLog(playerName, playerUID, "USED", block.ToString(), itemstack, blockPosition.X, blockPosition.Y, blockPosition.Z, null);
    }
}
