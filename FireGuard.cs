using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace GriefWarden;

/// <summary>
/// Closes the one hole in vanilla's fire protection: fire with no attribution.
///
/// Vanilla already does most of what a server owner wants here, and it does it well:
///
///   - BEBehaviorBurning stores startedByPlayerUid on the fire, persists it to tree
///     attributes so it survives a restart, and passes it to every fire it spreads to. A
///     fire twenty blocks downwind still knows who lit it.
///   - TrySpreadTo refuses to spread when that player lacks BuildOrBreak on either the
///     target or its fuel, so a player-lit fire stops at the edge of a claim they cannot
///     build in.
///   - ItemFirestarter refuses to ignite at all without Use on the target.
///   - PlayerByUid resolves offline players server-side, so lighting a fire and logging out
///     does not launder it into an unattributed one.
///
/// The gap is what happens when startedByPlayerUid is null. Vanilla reads that as "not a
/// player, so not a grief" and skips the claim test entirely, which is correct for lightning
/// and wrong for everything else. Any mod that creates fire without setting attribution
/// produces a fire that is indistinguishable from lightning and ignores claims completely.
/// On a 200-mod pack that is not hypothetical.
///
/// So: watch lightning strikes directly, through the public event the weather system already
/// exposes, and treat unattributed fire near a recent strike as natural. Everything else
/// unattributed is an unknown source and gets the protection vanilla skips.
///
/// This is deliberately additive. It never blocks anything vanilla would allow for an
/// attributed fire, and it never touches the lightning path itself.
/// </summary>
public class FireGuard {
    private readonly GriefWardenConfig config;

    // Recent lightning impacts, used to recognise natural fire. Kept small and pruned on
    // insert: strikes are rare and only the last few seconds ever matter.
    private readonly List<(Vec3d Pos, long AtMs)> strikes = new();
    private readonly object strikeLock = new();

    private static FireGuard instance;

    public int ObservedUnattributed { get; private set; }
    public int ObservedLightning { get; private set; }
    public int BlockedSpreads { get; private set; }

    public FireGuard(GriefWardenConfig config, Harmony harmony) {
        this.config = config;
        instance = this;

        if (!config.FireGuardEnabled) {
            Main.API.Logger.Notification("GriefWarden: fire guard disabled by config.");
            return;
        }

        // Watch strikes through the public event rather than patching the lightning mod.
        // This works the same whether the lightningFires world setting is on or off: when it
        // is off no strike ever creates fire, so no unattributed fire is ever excused, and
        // the guard tightens automatically without being told.
        try {
            var weather = Main.API.ModLoader.GetModSystem<WeatherSystemServer>();
            if (weather != null) {
                weather.OnLightningImpactEnd += OnLightning;
            }
            else {
                Main.API.Logger.Warning(
                    "GriefWarden: no weather system found, so lightning cannot be recognised. " +
                    "Natural fire would be treated as an unknown source; fire guard is staying in observe mode."
                );
                this.config.FireGuardEnforce = false;
            }
        }
        catch (Exception ex) {
            Main.API.Logger.Error("GriefWarden: could not hook lightning, staying in observe mode: " + ex);
            this.config.FireGuardEnforce = false;
        }

        bool lightningFires = Main.API.World.Config.GetBool("lightningFires", false);
        Main.API.Logger.Notification(
            $"GriefWarden: fire guard on in {(config.FireGuardEnforce ? "ENFORCE" : "observe")} mode. " +
            $"lightningFires is {(lightningFires ? "on" : "off")}, grace {config.LightningGraceSeconds}s within {config.LightningGraceRadius} blocks."
        );
    }

    private void OnLightning(ref Vec3d impactPos, ref EnumHandling handling) {
        // Purely an observer. Handling is left exactly as found so this cannot change
        // whether lightning does anything.
        long now = Main.API.World.ElapsedMilliseconds;
        long cutoff = now - config.LightningGraceSeconds * 1000L;
        lock (strikeLock) {
            strikes.RemoveAll(s => s.AtMs < cutoff);
            strikes.Add((impactPos.Clone(), now));
        }
    }

    private bool NearRecentStrike(BlockPos pos) {
        long now = Main.API.World.ElapsedMilliseconds;
        long cutoff = now - config.LightningGraceSeconds * 1000L;
        double r = config.LightningGraceRadius;
        lock (strikeLock) {
            foreach (var s in strikes) {
                if (s.AtMs < cutoff) continue;
                if (Math.Abs(s.Pos.X - pos.X) <= r && Math.Abs(s.Pos.Y - pos.Y) <= r && Math.Abs(s.Pos.Z - pos.Z) <= r) {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Decide whether an unattributed fire may spread to a position. Returns true to allow.
    /// </summary>
    private bool AllowUnattributedSpread(BlockPos pos) {
        if (NearRecentStrike(pos)) {
            ObservedLightning++;
            return true;
        }

        // Unclaimed ground is not this system's business. Fire in the wild burns.
        LandClaim[] claimsHere = Main.API.World.Claims.Get(pos);
        if (claimsHere == null || claimsHere.Length == 0) return true;

        ObservedUnattributed++;

        string owner = claimsHere[0].LastKnownOwnerName ?? claimsHere[0].OwnedByPlayerUid ?? "unknown";
        string verdict = config.FireGuardEnforce ? "BLOCKED" : "would block (observe mode)";

        // Logged either way. Observe mode exists so a server owner learns what actually
        // produces unattributed fire on their pack before switching on something that can
        // stop a legitimate mechanic, and the log line is the whole point of that mode.
        Main.API.Logger.Notification(
            $"GriefWarden: {verdict} unattributed fire spreading into {owner}'s claim at {pos}. " +
            "No player on the fire and no recent lightning, so the source is a mod."
        );

        if (!config.FireGuardEnforce) return true;

        BlockedSpreads++;
        return false;
    }

    public string Describe() {
        if (!config.FireGuardEnabled) return "Fire guard is disabled.";
        bool lightningFires = Main.API.World.Config.GetBool("lightningFires", false);
        return $"Fire guard {(config.FireGuardEnforce ? "ENFORCING" : "observing")}. "
             + $"lightningFires is {(lightningFires ? "on" : "off")}. "
             + $"Since restart: {ObservedUnattributed} unattributed spreads into claims, "
             + $"{ObservedLightning} excused as lightning, {BlockedSpreads} blocked.";
    }

    // ------------------------------------------------------------------ patch
    /// <summary>
    /// Prefix on BEBehaviorBurning.TrySpreadTo.
    ///
    /// Only unattributed fire is considered. Anything with a player uid falls straight
    /// through to vanilla, which already tests the claim correctly, including the case that
    /// matters most to an owner: their own fire, started outside their own claim, is allowed
    /// to spread into it, because TestAccess is per player and per position rather than a
    /// blanket "claims block fire".
    ///
    /// Watersheds also patches this method on The Quire. Two prefixes coexist fine: if
    /// either returns false the spread is refused, which is the conservative direction for
    /// both of us.
    /// </summary>
    [HarmonyPatch(typeof(BEBehaviorBurning), nameof(BEBehaviorBurning.TrySpreadTo))]
    public static class TrySpreadToPatch {
        public static bool Prefix(BlockPos pos, string ___startedByPlayerUid, ref bool __result) {
            var guard = instance;
            if (guard == null || !guard.config.FireGuardEnabled) return true;

            // Attributed: vanilla's own claim test handles it, and handles it correctly.
            if (!string.IsNullOrEmpty(___startedByPlayerUid)) return true;

            try {
                if (guard.AllowUnattributedSpread(pos)) return true;
                __result = false;
                return false; // skip the original: this spread does not happen
            }
            catch (Exception ex) {
                // A fault in the guard must never stop fire behaving. Fail open, loudly.
                Main.API.Logger.Error("GriefWarden: fire guard errored, allowing spread: " + ex);
                return true;
            }
        }
    }
}
