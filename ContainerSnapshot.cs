using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace GriefWarden;

/// <summary>
/// What was inside a container at the moment it was destroyed.
///
/// This is the fact the mod could not produce. Breaking a chest logs the chest and the tool
/// and nothing about the contents, so a report of "two full chests taken" has no manifest
/// behind it and never can afterwards: the block entity is gone, the stacks are loose items
/// on the ground, and within minutes they have despawned or been collected. The log can say
/// a container died and say nothing about what died with it.
///
/// Capture happens on Event.BreakBlock, which fires while the block entity is still there,
/// and the row is only written from DidBreakBlock, which fires only if the break actually
/// went through. Splitting it that way matters: BreakBlock can be cancelled by any other
/// handler, and a manifest for a chest that is still standing is worse than no manifest,
/// because it reads as a destroyed chest forever.
/// </summary>
public static class ContainerSnapshot {
    /// <summary>
    /// Captured-but-not-yet-committed manifests, keyed by position.
    ///
    /// Entries live for the few milliseconds between BreakBlock and DidBreakBlock. Anything
    /// older than the grace below belongs to a break that was cancelled, so it is swept
    /// rather than left to accumulate for the life of the server.
    /// </summary>
    private static readonly Dictionary<BlockPos, Pending> pending = new();

    private const int StaleAfterSeconds = 10;

    public class Pending {
        public long CapturedAt;
        public string Block = "";
        public string? ContainerId;
        public int SlotCount;
        public int TotalItems;
        public string Manifest = "";
    }

    /// <summary>
    /// Reads the inventory of whatever is at this position, if it has one, and holds the
    /// result until the break is confirmed. Returns true if anything was worth recording.
    ///
    /// Empty containers are skipped on purpose. An empty chest being removed is a
    /// renovation; every row written for one is a row an investigator has to read past.
    /// </summary>
    public static bool Capture(BlockPos pos) {
        if (pos == null) return false;

        try {
            IInventory? inventory = InventoryAt(pos);
            if (inventory == null || inventory.Count == 0) return false;

            var sb = new StringBuilder();
            int slots = 0;
            int items = 0;

            for (int i = 0; i < inventory.Count; i++) {
                ItemStack? stack = inventory[i]?.Itemstack;
                if (stack == null) continue;

                slots++;
                items += stack.StackSize;

                // Tab separated because item names carry commas, slashes, brackets and
                // parentheses, and the panel has to split this without a parser.
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(i).Append('\t').Append(SafeName(stack)).Append('\t').Append(stack.StackSize);
            }

            if (slots == 0) return false;

            Block block = Main.API.World.BlockAccessor.GetBlock(pos);

            SweepStale();
            pending[pos.Copy()] = new Pending {
                CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Block = block?.ToString() ?? "unknown",
                ContainerId = inventory.InventoryID,
                SlotCount = slots,
                TotalItems = items,
                Manifest = sb.ToString(),
            };
            return true;
        }
        catch (Exception ex) {
            // A container whose inventory throws must not stop the block from breaking or
            // take the logging thread with it.
            Main.API.Logger.Warning("GriefWarden: could not snapshot container at " + pos + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes the held manifest for a break that actually happened, and forgets it. Safe to
    /// call for any break: a position with nothing captured simply does nothing.
    /// </summary>
    public static void Commit(BlockPos pos, string? playername, string? playeruid, int x, int y, int z) {
        if (pos == null) return;
        if (!pending.TryGetValue(pos, out Pending? held)) return;
        pending.Remove(pos);

        Main.Database.AddContainerSnapshot(
            playername, playeruid, held.Block, held.ContainerId,
            x, y, z, held.SlotCount, held.TotalItems, held.Manifest
        );
    }

    /// <summary>Drops a capture without writing it, for a break that was cancelled.</summary>
    public static void Discard(BlockPos pos) {
        if (pos != null) pending.Remove(pos);
    }

    /// <summary>
    /// Toolracks are the one stock container that does not implement
    /// IBlockEntityContainer, which is why the container log command has a special case for
    /// them too. Handle both rather than silently missing a rack of bronze tools.
    /// </summary>
    private static IInventory? InventoryAt(BlockPos pos) {
        BlockEntity blockEnt = Main.API.World.BlockAccessor.GetBlockEntity(pos);
        if (blockEnt == null) return null;

        if (blockEnt is IBlockEntityContainer container) return container.Inventory;
        if (blockEnt is BlockEntityToolrack rack) return rack.inventory;
        return null;
    }

    /// <summary>
    /// GetName can throw on a stack whose item was removed with the mod that defined it,
    /// which is exactly the sort of stack a disaster investigation is looking at.
    /// </summary>
    private static string SafeName(ItemStack stack) {
        try {
            string? name = stack.GetName();
            if (!string.IsNullOrWhiteSpace(name)) return name.Replace('\t', ' ').Replace('\n', ' ');
        }
        catch { }
        return stack.Collectible?.Code?.ToString() ?? "unknown item";
    }

    private static void SweepStale() {
        if (pending.Count == 0) return;

        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - StaleAfterSeconds;
        List<BlockPos>? drop = null;
        foreach (var kv in pending) {
            if (kv.Value.CapturedAt < cutoff) (drop ??= new List<BlockPos>()).Add(kv.Key);
        }
        if (drop == null) return;
        foreach (BlockPos pos in drop) pending.Remove(pos);
    }
}
