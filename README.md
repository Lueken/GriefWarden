# GriefWarden
A Vintage Story server side mod that logs interactions in the game world with SQLite.

In-Game Commands:

/playerlog USERNAME [-t 6h] [-n page] [-sum] [-a BROKE,TAKEN]
Everything one player did, newest first: blocks, containers, kills, logins and chat
merged into one timeline. -sum prints totals instead of rows; -a filters actions.

/itemlog ITEM NAME [-t 7d] [-n page]
Every container transaction naming the item. Partial names match.

/wasonline USERNAME WHEN
Whether a player was connected at a moment. WHEN is 90m, 6h, 14:35, or 2026-10-08 14:35.

/blocklog [-r 5] [-p page] [-t 6h]
Block history at the block looked at, or around you with -r.

/entitylog [-r 5 | -e ENTITYID] [-p page] [-t 6h]
Entity history in a radius, or one entity by id.

/containerlog [-p page] [-t 6h]
Container history at the container looked at. Both halves of a double chest resolve.

/rollbackbreaks -p USERNAME [-r 5] [-t 24h] [--apply]
PREVIEWS by default: what would be restored, what is refused and why, and whether any
destroyed containers held items (the container returns empty either way). --apply
commits; it never writes over a block somebody has placed since. There is no undo.

/griefwarden status | prune | snapshot | vacuum | fire
Status and retention control. snapshot writes a consistent copy (snapshot.db) beside
the database via VACUUM INTO — pull that off-server, never the live database.db.

/tpboatid -e ENTITYID
Teleports the boat with that id to you, force-loading its chunk if needed.

Every command renders timestamps with their timezone (UTC by default;
DisplayUtcOffsetMinutes in ModConfig/griefwarden.json shifts the display). Sessions,
chat and break manifests are captured from 1.1.0 onward; config can turn each off.
