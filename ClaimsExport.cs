using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace GriefWarden;

/// <summary>
/// Writes a snapshot of every land claim to claims.json beside the database, so an
/// off-server tool can tell whether a logged event happened somewhere it had any business
/// happening.
///
/// Why this exists at all: the game already stops a player breaking blocks inside a claim
/// they lack permission for, so "who broke something inside someone else's claim" is close
/// to an empty question by construction. The two questions that are NOT empty need this
/// data:
///
///   1. What happened just OUTSIDE a claim edge. Claims are boxes, builds are not, and the
///      approach, the outlying farm and the unclaimed chest by the gate are all fair game
///      to the engine. That is where grief actually lands, and no permission check sees it.
///   2. What a PERMITTED player did inside a claim. Entirely legal as far as the server is
///      concerned, which is exactly why it needs a human looking at it.
///
/// Both need claim geometry plus who owns it plus who was allowed in, so all three are in
/// the snapshot. This file is Quire-local and is deliberately not part of the retention
/// work intended for upstream.
/// </summary>
public class ClaimsExport {
    // Claims change when a player buys, sells or edits one, which is rare. Five minutes is
    // far more often than the data moves and still cheap: the whole claim list is a few
    // hundred small objects even on a busy server.
    private const int IntervalMs = 5 * 60 * 1000;

    private readonly string path;
    private string lastPayloadHash = null;

    public ClaimsExport() {
        path = Path.Combine(Main.API.GetOrCreateDataPath("GriefWarden"), "claims.json");

        // Write once immediately so a tool reading this never has to wait out the first
        // interval to find out whether the export works at all.
        Main.API.Event.RegisterCallback(_ => Write("startup"), 5000);
        Main.API.Event.RegisterGameTickListener(_ => Write("timer"), IntervalMs);
    }

    private void Write(string trigger) {
        try {
            List<LandClaim> claims = Main.API.World.Claims?.All;
            if (claims == null) {
                Main.API.Logger.Warning("GriefWarden: claim list unavailable, skipping claims.json");
                return;
            }

            var payload = new {
                generated_utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                trigger,
                // Every coordinate in this file is ABSOLUTE. The event tables in the
                // database are spawn-relative, so a consumer must offset one or the other
                // before comparing them. Stated here because getting it wrong produces
                // plausible distances rather than an error.
                coordinate_space = "absolute",
                spawn = new {
                    x = (int)Main.API.World.DefaultSpawnPosition.X,
                    z = (int)Main.API.World.DefaultSpawnPosition.Z
                },
                claims = BuildClaims(claims)
            };

            string json = JsonConvert.SerializeObject(payload, Formatting.Indented);

            // The timestamp changes every run, so compare only the meaningful part. Claims
            // move rarely and this file is pulled over SFTP; rewriting an identical file
            // every five minutes would make it look changed on every single sync.
            string body = json.Substring(json.IndexOf("\"claims\"", StringComparison.Ordinal));
            string hash = body.GetHashCode().ToString();
            if (hash == lastPayloadHash && File.Exists(path)) return;
            lastPayloadHash = hash;

            // Write and swap, so a reader can never catch a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            Main.API.Logger.Notification($"GriefWarden: wrote claims.json ({claims.Count} claims, {trigger})");
        }
        catch (Exception ex) {
            // A failed export must never take the logging mod down with it.
            Main.API.Logger.Error("GriefWarden: claims export failed: " + ex);
        }
    }

    private static List<object> BuildClaims(List<LandClaim> claims) {
        var list = new List<object>();
        foreach (LandClaim claim in claims) {
            var areas = new List<object>();
            foreach (var a in claim.Areas) {
                areas.Add(new { x1 = a.X1, y1 = a.Y1, z1 = a.Z1, x2 = a.X2, y2 = a.Y2, z2 = a.Z2 });
            }

            var permitted = new List<object>();
            if (claim.PermittedPlayerUids != null) {
                foreach (var kv in claim.PermittedPlayerUids) {
                    string name = null;
                    claim.PermittedPlayerLastKnownPlayerName?.TryGetValue(kv.Key, out name);
                    permitted.Add(new { uid = kv.Key, name, access = kv.Value.ToString() });
                }
            }

            var permittedGroups = new List<object>();
            if (claim.PermittedPlayerGroupIds != null) {
                foreach (var kv in claim.PermittedPlayerGroupIds) {
                    permittedGroups.Add(new { groupid = kv.Key, access = kv.Value.ToString() });
                }
            }

            list.Add(new {
                owner_uid = claim.OwnedByPlayerUid,
                owner_name = claim.LastKnownOwnerName,
                owner_group_uid = claim.OwnedByPlayerGroupUid,
                owner_entity_id = claim.OwnedByEntityId,
                description = claim.Description,
                protection_level = claim.ProtectionLevel,
                // These two turn an "outsider acted inside a claim" flag into a false
                // positive: the owner opened the place up on purpose. A consumer that
                // ignores them will keep reporting the public storage barn as a theft.
                allow_use_everyone = claim.AllowUseEveryone,
                allow_traverse_everyone = claim.AllowTraverseEveryone,
                areas,
                permitted_players = permitted,
                permitted_groups = permittedGroups
            });
        }
        return list;
    }
}
