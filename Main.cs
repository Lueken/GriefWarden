using GriefWarden.Hooks;
using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Main : ModSystem {
    private Harmony harmony = null;
    public static ICoreServerAPI API { get; private set; }
    public static Database Database { get; private set; }
    public static Dictionary<string, string> CachedPlayerUsernames { get; private set; } = new();

    public override bool ShouldLoad(EnumAppSide forSide) {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        API = api;
        Database = new Database();

        new BlockHooks();
        new EntityHooks();

        new Commands();

        // Quire-local: snapshots land claims beside the database so off-server analysis can
        // tell a break on unclaimed fringe from a break in the middle of nowhere. Not part
        // of the retention work meant for upstream.
        new ClaimsExport();

        API.Event.PlayerJoin += OnPlayerJoin;

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void Dispose() {
        Database.Dispose();

        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    private void OnPlayerJoin(IServerPlayer player) {
        CachedPlayerUsernames[player.PlayerUID] = player.PlayerName;
    }
}
