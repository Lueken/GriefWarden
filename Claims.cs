using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace GriefWarden;

/// <summary>
/// Whose land this happened on, and whether the actor had any business being there.
///
/// This is the single most decision-relevant fact in a grief report and the log never
/// carried it. A row saying DrWrights broke a chest at these coordinates is almost useless
/// on its own; the same row saying he did it inside Barrowmere, where he is on the roster
/// with build rights, settles most disputes on sight.
///
/// Geometry comes from the engine's own lookup rather than a hand-rolled box test, and that
/// is deliberate. Claim areas are NOT stored normalised: real claims on The Quire have x1
/// greater than x2 and y1 greater than y2. Any naive "x1 &lt;= x &lt;= x2" test silently
/// reports unclaimed for exactly the claims it is asked about most, which is how an earlier
/// pass over this data concluded the town square belonged to nobody.
///
/// Permission is read off the claim roster rather than through ILandClaimAPI.TestAccess,
/// because an investigation is usually about somebody who is not logged in, and TestAccess
/// needs a live IPlayer. The roster answers the same question for an absent player.
///
/// One honest limitation: claims carry no history. This describes the roster as it stands
/// now, not as it stood when the logged event happened. Someone removed from a town
/// yesterday will read as "not permitted" on a break from last month that was entirely
/// legitimate at the time.
/// </summary>
public static class Claims {
    /// <summary>
    /// A one-line claim verdict for an absolute block position, or null when the position is
    /// unclaimed and there is therefore nothing to say.
    /// </summary>
    public static string? Describe(BlockPos absolutePos, string? actorUid) {
        if (absolutePos == null) return null;

        LandClaim[]? claims = Main.API.World.Claims?.Get(absolutePos);
        if (claims == null || claims.Length == 0) return null;

        var parts = new List<string>();
        foreach (LandClaim claim in claims) {
            string owner = OwnerName(claim);
            string label = string.IsNullOrWhiteSpace(claim.Description)
                ? $"{owner}'s claim"
                : $"{claim.Description} ({owner})";
            parts.Add($"{label}, {Standing(claim, actorUid)}");
        }
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Where the actor stood with the claim owner. The phrasing is chosen so the one case
    /// that should stop an admin reading — an outsider acting inside a closed claim — does
    /// not look like the other three.
    /// </summary>
    private static string Standing(LandClaim claim, string? actorUid) {
        if (string.IsNullOrEmpty(actorUid)) return "actor unknown";
        if (actorUid == claim.OwnedByPlayerUid) return "by the owner";

        if (claim.PermittedPlayerUids != null
            && claim.PermittedPlayerUids.TryGetValue(actorUid, out EnumBlockAccessFlags access)) {
            return $"actor permitted ({access})";
        }

        // The owner opened the place up on purpose. Without these two an open storage barn
        // reports every visitor as an intruder.
        if (claim.AllowUseEveryone) return "actor not on roster, claim open to all";
        if (claim.AllowTraverseEveryone) return "actor not on roster, claim open to traverse";

        return "ACTOR NOT PERMITTED";
    }

    private static string OwnerName(LandClaim claim) {
        if (!string.IsNullOrWhiteSpace(claim.LastKnownOwnerName)) {
            // The engine stores this as "Player Someone" on player-owned claims, which reads
            // badly in a sentence that already says whose claim it is.
            string name = claim.LastKnownOwnerName;
            return name.StartsWith("Player ") ? name.Substring("Player ".Length) : name;
        }
        if (claim.OwnedByPlayerGroupUid != 0) return "group " + claim.OwnedByPlayerGroupUid;
        return "unknown owner";
    }

    /// <summary>
    /// Converts a stored spawn-relative position back to the absolute one the claim API
    /// wants.
    ///
    /// blocklogs and entitylogs store x and z relative to world spawn (y absolute), while
    /// claims are absolute. Mixing the two produces plausible wrong answers rather than
    /// errors, which is the worst failure mode available, so every caller goes through here.
    /// </summary>
    public static BlockPos ToAbsolute(int relX, int y, int relZ) {
        return new BlockPos(
            relX + (int)Main.API.World.DefaultSpawnPosition.X,
            y,
            relZ + (int)Main.API.World.DefaultSpawnPosition.Z
        );
    }

    /// <summary>
    /// Pulls an absolute position out of a container inventory id.
    ///
    /// Container ids embed ABSOLUTE coordinates, unlike the event tables, and in two
    /// different shapes depending on which block entity built the id: "chest-512642, 168,
    /// 512564" from the generic containers and "smelting-512658/164/512542" from others.
    /// Returns null for ids with no position in them at all, such as
    /// "mountedbaginv-6-14073" on a pack animal.
    /// </summary>
    public static BlockPos? PositionFromContainerId(string? containerId) {
        if (string.IsNullOrEmpty(containerId)) return null;

        int dash = containerId.IndexOf('-');
        if (dash < 0 || dash + 1 >= containerId.Length) return null;

        string tail = containerId.Substring(dash + 1);
        string[] bits = tail.Split(new[] { ',', '/' });
        if (bits.Length < 3) return null;

        if (int.TryParse(bits[0].Trim(), out int x)
            && int.TryParse(bits[1].Trim(), out int y)
            && int.TryParse(bits[2].Trim(), out int z)) {
            return new BlockPos(x, y, z);
        }
        return null;
    }
}
