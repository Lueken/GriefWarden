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

    // ----------------------------------------------------------------- rendering
    /// <summary>
    /// Minutes to shift timestamps by when printing them. Zero prints UTC and says so.
    ///
    /// The timestamps themselves are always stored as true UTC; this only changes what an
    /// admin reads. Set it to the timezone your staff actually live in: -420 for UTC-7,
    /// 60 for UTC+1. Every rendered line carries its offset either way, because an
    /// unlabelled time is the one thing in a grief log that can be confidently misread.
    /// </summary>
    public int DisplayUtcOffsetMinutes = 0;

    /// <summary>
    /// Rows per page in the log commands.
    ///
    /// The original was four, hardcoded. Four is not a page, it is a glimpse: a single
    /// chest in an active town can carry a hundred rows in a morning, and paging through
    /// that four at a time is why investigations ended up being done in SQL instead. Twelve
    /// fits a chat window without scrolling the conversation away.
    /// </summary>
    public int LogPageSize = 12;

    /// <summary>
    /// Default window for the commands that take -t, when it is not given.
    ///
    /// Not unbounded, on purpose. "Everything ever" is almost never the question, and
    /// answering it by default is how a command becomes something people stop running.
    /// </summary>
    public int DefaultWindowHours = 24;

    /// <summary>
    /// Hard ceiling on rows scanned by the item search, which has to decompress each
    /// candidate to match it and so cannot push the filter into SQL.
    ///
    /// When the cap is hit the command says so. A truncated search that reports itself is
    /// usable; one that quietly stops reads as proof an item was never touched.
    /// </summary>
    public int ItemSearchScanLimit = 250000;

    // ----------------------------------------------------------------- presence and chat
    /// <summary>
    /// Record join and disconnect times.
    ///
    /// "Was he online when it was said in chat" decides whether someone ignored an
    /// instruction or never saw it, and without this table the only way to answer it is to
    /// count their world actions around that minute and reason from the gaps. Two rows per
    /// login; the table is the cheapest thing in the database.
    /// </summary>
    public bool LogSessions = true;


    // ----------------------------------------------------------------- fire guard
    /// <summary>
    /// Watch fire that has no player attached to it.
    ///
    /// Vanilla already stops a player-lit fire crossing into a claim they cannot build in,
    /// and already lets lightning burn freely. The gap is fire from a mod, which arrives
    /// unattributed and is therefore treated as lightning and skips the claim test.
    /// </summary>
    public bool FireGuardEnabled = true;

    /// <summary>
    /// False logs what it would have blocked and blocks nothing. True actually refuses the
    /// spread.
    ///
    /// Default false on purpose. A pack this size will have legitimate mod fire, and finding
    /// out which by breaking it is the wrong order. Run in observe for a few weeks, read the
    /// log lines, then decide.
    /// </summary>
    public bool FireGuardEnforce = false;

    /// <summary>
    /// How long after a lightning strike unattributed fire near it still counts as natural.
    /// The vanilla lightning system creates its fire in the same tick as the impact, so this
    /// only needs to cover the fire then spreading outward.
    /// </summary>
    public int LightningGraceSeconds = 30;

    /// <summary>
    /// How far from a strike unattributed fire still counts as natural. Vanilla ignites
    /// within one block of the impact and the fire spreads from there, so this is a spread
    /// allowance rather than a strike radius.
    /// </summary>
    public int LightningGraceRadius = 12;

    public void Clamp() {
        // A zero or negative retention would delete everything on the next tick. Refuse
        // rather than obey: this is the one setting where a typo is unrecoverable.
        if (DestructiveRetentionDays < 1) DestructiveRetentionDays = 1;
        if (ContextRetentionDays < 1) ContextRetentionDays = 1;
        if (LogPageSize < 1) LogPageSize = 1;
        if (LogPageSize > 50) LogPageSize = 50;   // beyond this the client drops chat lines
        if (DefaultWindowHours < 1) DefaultWindowHours = 1;
        if (ItemSearchScanLimit < 1000) ItemSearchScanLimit = 1000;

        // Real offsets run UTC-12 to UTC+14. Anything outside that is a typo, and a typo
        // here silently relabels every timestamp an admin reads.
        if (DisplayUtcOffsetMinutes < -720) DisplayUtcOffsetMinutes = -720;
        if (DisplayUtcOffsetMinutes > 840) DisplayUtcOffsetMinutes = 840;
        if (PruneIntervalHours < 1) PruneIntervalHours = 1;
        if (PruneBatchSize < 100) PruneBatchSize = 100;
        if (MaxDatabaseMb < 16) MaxDatabaseMb = 16;
        if (LightningGraceSeconds < 1) LightningGraceSeconds = 1;
        if (LightningGraceRadius < 1) LightningGraceRadius = 1;

        // Keeping context longer than evidence is almost certainly a mistake, and it would
        // leave the log full of block placements with the breaks pruned out from under them.
        if (ContextRetentionDays > DestructiveRetentionDays) {
            ContextRetentionDays = DestructiveRetentionDays;
        }
    }
}
