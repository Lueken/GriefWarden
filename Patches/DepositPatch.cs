using HarmonyLib;
using Vintagestory.API.Common;

namespace GriefWarden.Patches;

/// <summary>
/// The deposit that nothing logged: a stack shift-clicked out of a player's own inventory
/// and into a container.
///
/// Confirmed in engine source rather than guessed. InventoryBase.ActivateSlot does this when
/// the shift key is down:
///
///     sourceSlot = this[slotId];
///     op.ActingPlayer.InventoryManager.TryTransferAway(sourceSlot, ref op, false, ...)
///
/// The inventory whose ActivateSlot runs is the one that was *clicked*. Shift-click a chest
/// slot and the chest's ActivateSlot runs, which InventoryBasePatch sees and logs as TAKEN.
/// Shift-click your own inventory to send a stack into that chest and the *player*
/// inventory's ActivateSlot runs, which InventoryBasePatch skips outright at its first
/// condition — so the stack arrives in the chest with nothing written anywhere.
///
/// The consequence showed up in a real dispute on 2026-10-08. A chest its owner considered
/// full had not a single container row in its entire recorded life, and three decorative
/// ovens were taken out of a trunk that no log ever recorded anything going into. Who put
/// them there could only be guessed at, which is the one thing a forensics log exists to
/// prevent.
///
/// Why this seam: ItemSlot.TryPutInto is the funnel every merge and fill passes through, and
/// it hands over both endpoints plus the genuine moved quantity as its return value. No
/// inferring from before-and-after stack sizes, which is what every other container patch
/// here has to do.
///
/// Why it is restricted to shift-held, player-source, container-sink. That triple is exactly
/// and only the missing case:
///
///   - without the ShiftDown test, an ordinary cursor-click deposit would be logged here AND
///     by InventoryBasePatch, doubling every PLACED row the mod already produces
///   - without the player-source test, a shift-click *out of* a chest would be logged here
///     as well as there, doubling TAKEN
///   - without the container-sink test, moving a stack from backpack to hotbar would be
///     recorded as though it had gone into storage
///
/// ShiftDown is also the cheapest available first guard, which matters, because this method
/// sits on the path of every hopper and chute in the world. Those carry no modifiers and no
/// acting player, so they fall out on the first comparison.
/// </summary>
/// <remarks>
/// TryPutInto is overloaded, so the signature has to be spelled out, and the second
/// parameter is by reference. An argument-type array alone does not match a ref parameter:
/// Harmony resolves the target by exact parameter type, ItemStackMoveOperation&amp; is not
/// ItemStackMoveOperation, and a target that fails to resolve throws out of PatchAll and
/// takes the entire mod down at boot rather than quietly skipping one patch. The variations
/// array is how that is declared, because MakeByRefType cannot appear in an attribute.
/// </remarks>
[HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.TryPutInto),
    new[] { typeof(ItemSlot), typeof(ItemStackMoveOperation) },
    new[] { ArgumentType.Normal, ArgumentType.Ref })]
public class ItemSlotTryPutIntoPatch {
    [HarmonyPrefix]
    public static void Prefix(ItemSlot __instance, ItemSlot sinkSlot, ref ItemStackMoveOperation op, out PutIntoState __state) {
        __state = new PutIntoState();

        if (op == null || !op.ShiftDown || op.ActingPlayer == null) return;
        if (__instance?.Itemstack == null) return;

        // Source must be the acting player's own inventory; sink must be something else.
        if (__instance.Inventory is not InventoryBasePlayer) return;

        IInventory? sinkInventory = sinkSlot?.Inventory;
        if (sinkInventory == null || sinkInventory is InventoryBasePlayer) return;
        if (string.IsNullOrEmpty(sinkInventory.InventoryID)) return;

        // Read here rather than in the postfix: TryPutInto replaces op with a merge
        // operation partway through, and the source slot can be empty by the time it
        // returns, so neither the item name nor the acting player is reliable afterwards.
        __state.containerId = sinkInventory.InventoryID;
        __state.itemName = __instance.Itemstack.GetName();
        __state.playerName = op.ActingPlayer.PlayerName;
        __state.playerUid = op.ActingPlayer.PlayerUID;
        __state.log = true;
    }

    [HarmonyPostfix]
    public static void Postfix(int __result, PutIntoState __state) {
        if (!__state.log || __result <= 0) return;

        // __result is what the engine actually moved. A transfer spread across several
        // containers calls this once per container, so every row carries the real amount
        // that landed in that one rather than the total that left the player.
        Main.Database.AddContainerLog(__state.playerName, __state.playerUid, "PLACED",
                                      __state.containerId, __state.itemName, __result);
    }

    public class PutIntoState {
        public bool log;
        public string containerId = "";
        public string itemName = "";
        public string playerName = "";
        public string playerUid = "";
    }
}
