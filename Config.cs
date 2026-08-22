using System;

namespace GriefWarden;

/// <summary>
/// GriefWarden had no configuration of any kind: no ModConfig, no LoadModConfig, nothing.
/// Every value was a literal. This is the file that makes retention a policy a server owner
/// sets rather than a number in the source.
///
/// Written to ModConfig/griefwarden.json on first run.
/// </summary>
public class GriefWardenConfig {
    /// <summary>Master switch. False keeps the original behaviour: log forever, delete nothing.</summary>
    public bool RetentionEnabled = true;

    /// <summary>
    /// How long to keep BROKE, KILLED and TAKEN. These are the evidence.
    ///
    /// 90 days because griefs are found late. Someone comes back from a month away to a
    /// missing base, and the window has to still cover the day it happened.
    /// </summary>
    public int DestructiveRetentionDays = 90;

    /// <summary>
    /// How long to keep everything else: PLACED, USED, INTERACTED, SWAP, SAME_ITEM,
    /// SPAWNED, DESPAWNED.
    ///
    /// 21 days because these are context, not evidence. They answer "what else was going on
    /// around this", which matters for a fresh report and almost never decides an old one.
    /// On a real week of Quire data they were roughly half of all rows.
    /// </summary>
    public int ContextRetentionDays = 21;

    /// <summary>
    /// Backstop only. If the file is still over this after the age-based pass, the oldest
    /// rows go regardless of class until it fits.
    ///
    /// This firing means the age policy is wrong for this server, not that the disk is
    /// small, so it logs at warning level rather than quietly doing its job.
    /// </summary>
    public int MaxDatabaseMb = 1024;

    /// <summary>How often to prune. Also runs once shortly after startup.</summary>
    public int PruneIntervalHours = 24;

    /// <summary>
    /// Rows deleted per statement. A single unbounded DELETE over a large table holds the
    /// write lock long enough to stall logging on a busy server; batching keeps each
    /// statement short so inserts interleave.
    /// </summary>
    public int PruneBatchSize = 5000;

    /// <summary>
    /// SQLite does not hand freed pages back to the operating system, so after a prune the
    /// file stops growing but does not shrink. That is usually the right trade: the free
    /// pages get reused and nothing has to lock. VACUUM reclaims the space but rewrites the
    /// whole database and needs roughly double the size in scratch, which on a live server
    /// is a visible hitch. Off by default; /griefwarden vacuum runs it on demand.
    /// </summary>
    public bool VacuumAfterPrune = false;

    public void Clamp() {
        // A zero or negative retention would delete everything on the next tick. Refuse
        // rather than obey: this is the one setting where a typo is unrecoverable.
        if (DestructiveRetentionDays < 1) DestructiveRetentionDays = 1;
        if (ContextRetentionDays < 1) ContextRetentionDays = 1;
        if (PruneIntervalHours < 1) PruneIntervalHours = 1;
        if (PruneBatchSize < 100) PruneBatchSize = 100;
        if (MaxDatabaseMb < 16) MaxDatabaseMb = 16;

        // Keeping context longer than evidence is almost certainly a mistake, and it would
        // leave the log full of block placements with the breaks pruned out from under them.
        if (ContextRetentionDays > DestructiveRetentionDays) {
            ContextRetentionDays = DestructiveRetentionDays;
        }
    }
}
