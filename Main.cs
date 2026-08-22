using System;
using GriefWarden.Hooks;
using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Main : ModSystem {
    private const string ConfigName = "griefwarden.json";

    private Harmony harmony = null;
    public static ICoreServerAPI API { get; private set; }
    public static Database Database { get; private set; }
    public static Dictionary<string, string> CachedPlayerUsernames { get; private set; } = new();
    public static GriefWardenConfig Config { get; private set; }
    public static Retention Retention { get; private set; }

    public override bool ShouldLoad(EnumAppSide forSide) {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        API = api;

        // Config before anything else: retention needs it, and a missing file has to be
        // written before the first prune can decide what to keep.
        Config = LoadConfig(api);

        Database = new Database();

        new BlockHooks();
        new EntityHooks();

        new Commands();

        // Quire-local: snapshots land claims beside the database so off-server analysis can
        // tell a break on unclaimed fringe from a break in the middle of nowhere. Not part
        // of the retention work meant for upstream.
        new ClaimsExport();

        // Age-based pruning. Without it the four insert-only tables grow forever, which on a
        // busy server is measured in gigabytes a year.
        Retention = new Retention(Config);

        API.Event.PlayerJoin += OnPlayerJoin;

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void Dispose() {
        Database.Dispose();

        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    /// <summary>
    /// Reads ModConfig/griefwarden.json, writing a default file when it is missing or
    /// unreadable. A corrupt config must not stop the mod loading: losing the grief log
    /// because someone left a trailing comma would be a far worse outcome than ignoring
    /// their edits and saying so.
    /// </summary>
    private static GriefWardenConfig LoadConfig(ICoreServerAPI api) {
        GriefWardenConfig config = null;
        try {
            config = api.LoadModConfig<GriefWardenConfig>(ConfigName);
        }
        catch (Exception ex) {
            api.Logger.Error("GriefWarden: " + ConfigName + " could not be read, using defaults: " + ex.Message);
        }

        if (config == null) {
            config = new GriefWardenConfig();
            api.Logger.Notification("GriefWarden: wrote default " + ConfigName);
        }

        config.Clamp();
        api.StoreModConfig(config, ConfigName);
        return config;
    }

    private void OnPlayerJoin(IServerPlayer player) {
        CachedPlayerUsernames[player.PlayerUID] = player.PlayerName;
    }
}
