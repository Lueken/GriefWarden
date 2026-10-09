using Vintagestory.API.Server;

namespace GriefWarden.Hooks;

/// <summary>
/// Presence: who was connected, and when.
///
/// A dispute is almost never settled by the block log alone: whether the player was
/// connected when an instruction was given in chat decides between ignoring it and never
/// having seen it, and presence used to be recorded nowhere at all. Optional in config.
/// </summary>
public class PlayerHooks {
    public PlayerHooks() {
        if (Main.Config.LogSessions) {
            Main.API.Event.PlayerJoin += this.OnPlayerJoin;

            // Disconnect rather than PlayerLeave. Leave fires on the way out of the world
            // and does not fire for a dropped connection, which is exactly the departure
            // most worth having a row for.
            Main.API.Event.PlayerDisconnect += this.OnPlayerDisconnect;
        }

    }

    private void OnPlayerJoin(IServerPlayer player) {
        if (player == null) return;
        Main.Database.AddSessionLog(player.PlayerName, player.PlayerUID, "JOINED");
    }

    private void OnPlayerDisconnect(IServerPlayer player) {
        if (player == null) return;
        Main.Database.AddSessionLog(player.PlayerName, player.PlayerUID, "LEFT");
    }
}
