using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace GriefWarden.Hooks;

/// <summary>
/// Presence and speech: the two things every ruling needs and neither of which the mod used
/// to record.
///
/// A dispute is almost never settled by the block log alone. It is settled by whether the
/// player was connected when the instruction was given, and by what the instruction actually
/// said. Both of those used to live outside the database — presence nowhere at all, chat in
/// a separate text file with no shared index — so answering them meant reading two sources
/// and lining them up by eye.
///
/// Both are optional in config. Chat especially: it is the most personal thing here, and a
/// server owner who does not want a searchable transcript of their community should be able
/// to say so without losing the forensics.
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

        if (Main.Config.LogChat) {
            Main.API.Event.PlayerChat += this.OnPlayerChat;
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

    /// <summary>
    /// Observes chat without touching it.
    ///
    /// The signature hands over message and data by reference and a consumed flag, which is
    /// how a chat mod rewrites or swallows a line. A log must do neither: everything here
    /// reads, nothing assigns, and consumed is left exactly as found. If this hook ever
    /// starts modifying chat it has stopped being a log.
    /// </summary>
    private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed) {
        if (player == null || string.IsNullOrEmpty(message)) return;
        Main.Database.AddChatLog(player.PlayerName, player.PlayerUID, channelId, message);
    }
}
