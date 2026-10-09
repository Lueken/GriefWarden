using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;

namespace GriefWarden;

public static class Util {
    public static string? GetPlayerCurrentItemstackName(IPlayer player) {
        IPlayerInventoryManager invManager = player.InventoryManager;
        if (invManager != null) {
            ItemSlot activeSlot = invManager.ActiveHotbarSlot;
            if (activeSlot != null && activeSlot.Itemstack != null)
                return activeSlot.Itemstack.GetName();
        }
        return null;
    }

    /// <summary>
    /// Renders a stored timestamp with the timezone it is in, which every log line used to
    /// omit.
    ///
    /// Stored timestamps are true UTC and always were. The bug was the rendering: a bare
    /// "2026-10-08 17:54:22" reads as the viewer's own clock, so an admin seven hours behind
    /// UTC places every event seven hours later than it happened. On a real dispute that is
    /// the difference between "he was told first" and "he was told afterwards".
    ///
    /// DisplayUtcOffsetMinutes shifts the rendering for a server whose players all share a
    /// timezone. The label moves with it, so the number is never unlabelled either way.
    /// </summary>
    public static string FormatTimestamp(long unixSeconds) {
        int offsetMinutes = Main.Config?.DisplayUtcOffsetMinutes ?? 0;
        DateTimeOffset moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        if (offsetMinutes == 0)
            return moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

        TimeSpan offset = TimeSpan.FromMinutes(offsetMinutes);
        DateTimeOffset shifted = moment.ToOffset(offset);
        string sign = offsetMinutes < 0 ? "-" : "+";
        TimeSpan abs = offset.Duration();
        return shifted.ToString("yyyy-MM-dd HH:mm:ss") + $" {sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    /// <summary>
    /// Parses a window like "6h", "90m", "3d" into seconds. Returns null for anything it
    /// does not understand, so a caller can tell a typo from an omission rather than
    /// silently searching the wrong span of history.
    /// </summary>
    public static long? ParseDuration(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim().ToLowerInvariant();
        char unit = text[text.Length - 1];
        string numberPart = char.IsDigit(unit) ? text : text.Substring(0, text.Length - 1);

        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            return null;
        if (value <= 0) return null;

        double multiplier = unit switch {
            'm' => 60,
            'h' => 3600,
            'd' => 86400,
            'w' => 604800,
            _ => char.IsDigit(unit) ? 3600 : -1, // a bare number means hours
        };
        if (multiplier < 0) return null;

        return (long)(value * multiplier);
    }
}
