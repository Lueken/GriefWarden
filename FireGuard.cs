using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
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
/// So: watch lightning strikes through the public event the weather system already exposes,
/// and STAMP the fire they create with a sentinel uid. Vanilla then treats it exactly as it
/// treats null, because the sentinel resolves to no player, while we can still tell the two
/// apart. Anything unattributed that carries no sentinel is an unknown source and gets the
/// protection vanilla skips.
///
/// Stamping rather than remembering where lightning struck is the important choice. Natural
/// fire burns and spreads for minutes and travels far past any sane radius; a proximity
/// window would stop recognising it partway through and start treating it as a mod fire.
/// The sentinel rides along instead, because vanilla already propagates that field to every
/// fire it spreads to and already persists it, so it survives distance and restarts for free.
///
/// This is deliberately additive. It never blocks anything vanilla would allow for an
/// attributed fire, and it never changes what the lightning path does.
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

    /// <summary>
    /// The sentinel written into startedByPlayerUid on lightning fire.
    ///
    /// This is the whole trick. Vanilla treats a uid that resolves to no player exactly as
    /// it treats null: PlayerByUid returns null, so TrySpreadTo's claim test is skipped and
    /// the fire spreads naturally. But unlike null, a value is CARRIED. Vanilla already
    /// passes startedByPlayerUid to every fire it spreads to and already persists it to tree
    /// attributes, so a lightning fire stays recognisable as lightning fifty blocks and one
    /// restart later, with no bookkeeping on our side.
    ///
    /// A proximity-and-time window cannot do this. Natural fire burns and spreads for
    /// minutes and travels far past any sane radius, and the moment it left that window it
    /// would stop looking like lightning and start looking like an unknown source.
    ///
    /// The colon guarantees it can never collide with a real player uid.
    /// </summary>
    public const string LightningUid = "griefwarden:lightning";

    private static readonly FieldInfo StartedByField =
        AccessTools.Field(typeof(BEBehaviorBurning), "startedByPlayerUid");

    private void OnLightning(ref Vec3d impactPos, ref EnumHandling handling) {
        // Purely an observer. Handling is left exactly as found, so this cannot change
        // whether lightning does anything at all.
        long now = Main.API.World.ElapsedMilliseconds;
        long cutoff = now - config.LightningGraceSeconds * 1000L;
        var at = impactPos.Clone();
        lock (strikeLock) {
            strikes.RemoveAll(s => s.AtMs < cutoff);
            strikes.Add((at, now));
        }

        // Vanilla creates its fire during this same event, and whether our handler runs
        // before or after it depends on subscription order we do not control. Stamping on
        // the next tick sidesteps that entirely: by then the fire exists either way.
        Main.API.Event.RegisterCallback(_ => StampLightningFires(at.AsBlockPos), 50);
    }

    /// <summary>
    /// Mark fresh unattributed fire around a strike as lightning-born.
    ///
    /// Vanilla ignites within one block of the impact and then from the faces of that block,
    /// so everything it lights is within two. Three gives margin without reaching far enough
    /// to adopt a player's fire that happened to be burning nearby.
    /// </summary>
    private void StampLightningFires(BlockPos impact) {
        try {
            int r = 3;
            var pos = new BlockPos(impact.dimension);
            int stamped = 0;

            for (int dx = -r; dx <= r; dx++) {
                for (int dy = -r; dy <= r; dy++) {
                    for (int dz = -r; dz <= r; dz++) {
                        pos.Set(impact.X + dx, impact.Y + dy, impact.Z + dz);
                        var be = Main.API.World.BlockAccessor.GetBlockEntity(pos);
                        var burning = be?.GetBehavior<BEBehaviorBurning>();
                        if (burning == null) continue;

                        // Only claim fire that nobody owns. A player's fire near a strike
                        // keeps its attribution and stays subject to the claim test.
                        string existing = StartedByField?.GetValue(burning) as string;
                        if (!string.IsNullOrEmpty(existing)) continue;

                        StartedByField?.SetValue(burning, LightningUid);
                        be.MarkDirty(false);
                        stamped++;
                    }
                }
            }

            if (stamped > 0) {
                ObservedLightning += stamped;
                Main.API.Logger.Notification(
                    $"GriefWarden: marked {stamped} lightning fire(s) at {impact} as natural. They will spread without claim checks."
                );
            }
        }
        catch (Exception ex) {
            // Failing here means natural fire looks like an unknown source. In observe mode
            // that is a log line; in enforce mode it would wrongly stop a natural fire, so
            // it is worth shouting about.
            Main.API.Logger.Error("GriefWarden: could not mark lightning fire, it may be treated as an unknown source: " + ex);
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
        // Belt and braces for the one tick between a strike and the stamp landing. After
        // that the sentinel on the fire itself is what identifies natural fire, and this
        // window stops mattering.
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

            // Lightning, carried on the fire itself rather than inferred from where and when
            // it started. Natural fire spreads without a claim check no matter how far it has
            // travelled or how long it has been burning.
            if (___startedByPlayerUid == LightningUid) return true;

            // Attributed to a real player: vanilla's own claim test handles it, and handles
            // it correctly, including letting an owner's fire spread into their own claim.
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
