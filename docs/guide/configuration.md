# Configuration

Most settings are in the web app, under **Settings**. The rest are environment variables on the core container, written
`Melodex__Name`. The compose file sets the paths; you rarely need to change them.

| Variable | What | In the container |
| --- | --- | --- |
| `Melodex__DataPath` | The database, artwork cache, session keys and logs | `/config` |
| `Melodex__LibraryPath` | Your music, read-only | `/music` |
| `Melodex__StagingPath` | Downloads waiting to be filed, and files upgrades replaced while they're held | `/staging` |
| `Melodex__ListenNowPath` | Songs fetched to listen to | `/listen-now` |
| `Melodex__PluginsPath` | Extra in-process plugins (catalogue and media-server) | `/plugins` |
| `Melodex__ImportWorker__Url` | The beets worker | `http://beets-worker:8080` |
| `Melodex__ResetLogin` | `true` clears the admin's password at start: see [Users](users-and-permissions.md#a-lost-admin-password) | |

Variables named `Navidex__…`, from before the rename, still work; a `Melodex__` one wins where both are set.

## In the web app

| Settings section | Holds |
| --- | --- |
| **General** | Songs on more than one release, the missing view (each kind of release shown, hidden, or for new songs only), matching, file metadata, lyrics, the folder layout, and fetching |
| **Quality** | The [quality profile](requests-and-listening.md#quality) and [upgrades](requests-and-listening.md#upgrades) |
| **Scheduled tasks** | What runs on its own, and when: see [Scheduled tasks](#scheduled-tasks) |
| **Playback** | Which songs play [without a gap](requests-and-listening.md#gapless-playback), when [the visualiser](requests-and-listening.md#the-visualiser)'s bars are made, if at all, how long listen-now songs are kept, how much of a [radio](requests-and-listening.md#radio) is songs you don't have, and whether radios carry on when a queue runs out |
| **Log** | Recent activity, and how long logs are kept |
| **Plugins** | See [Plugins](plugins.md) |
| **Users**, **API**, **Webhooks** | See [Users and permissions](users-and-permissions.md) and the [client guide](../client-guide.md) |
| **Appearance** | See [Appearance](appearance.md) |
| **Help** | This guide |
| **About** | The version, links to GitHub, and who made Melodex |

## Scheduled tasks

**Settings → Scheduled tasks** has three schedules, **Daily**, **Weekly** and **Monthly**, each a window between two
times, and below each the tasks it runs: tick a task to put it on that schedule. A task is on one schedule at most, so
ticking it on one takes it off another; one on none only runs when you start it.

- **Scan the library**: read new and changed files and match them, then whatever **Settings → General** says follows a scan.
- **Update the catalogue**: look up every library artist's discography.
- **Look for missing lyrics**: look again for songs without lyrics.
- **Retry failed downloads**: ask again for downloads that failed. Any not started when the window closes go back to
  failed, for the next window.
- **Get what's on wishlists**: ask for what's on everyone's wishlist, each in the name of whoever wished for it, when a
  retriever is on to fetch it. Any not started when the window closes are cancelled, and stay on the wishlist.
- **Look for upgrades**: look through the whole library for [upgrades](requests-and-listening.md#upgrades).

Checking lossless files and making the visualiser's bars aren't scheduled: new music gets them as it comes, so they're
needed once, for a library brought in, and are on [Tools](tools.md#bringing-in-a-library).

To start with, the daily window is 01:00 to 06:00 and scans and updates the catalogue; the weekly one is Mondays at the same times, for lyrics, failed downloads, wishlists and upgrades; nothing is monthly.

- **Each window has its own times.** One that ends earlier than it starts runs past midnight: 22:00 to 04:00 ends the
  next morning. One that ends when it starts lasts a whole day.
- **The weekly one runs on a day you pick, the monthly one on a day of the month, 1 to 31.** In a month shorter than
  that, it runs on the month's last day: 31 is the last day of every month.
- **All three are in one time zone**, the server's UTC to start with: **Use this browser's** sets yours.
- **A window runs its tasks once, in order**: scan, catalogue, lyrics, then failed downloads, wishlists and upgrades.
  The first three are tasks on **Activity**, one after another; the rest wait
  for them, as nothing can be asked for while a scan reads the library.
- **Downloads go a few at a time.** Retries and wishlists keep **Downloads at a time** going (5 to start with), and
  upgrades their own **Upgrades at a time**: as each finishes, the next is asked for in its place, so the queue never
  fills with a window's worth at once. Each waits for its downloads to finish before the next starts, and upgrades go
  last, filling what's left of the window. When the window closes, nothing more is asked for; those not started wait
  for the next window, and those under way carry on.
- **The windows take turns.** When two open on the same day, daily goes first, then weekly, then monthly, as long as
  its window is still open.

Below each schedule are when it next runs, and how its last run went, a line for each task, with a link to its task on
Activity. Every task can still be run straight away from its own place, which says which schedule it's on and links
here: **Scan** and **Update catalogue** in the Library page's Scan menu, **Look for missing lyrics now** under **Settings →
General → Lyrics**, **Look for upgrades** on Tools, **Retry
failed** on **Activity → Downloads**, and **Get them all** on the wishlist. Those last two ask for everything at once.

**Sync playlists every** sets how often playlists sync with the media server for changes made there, from 1 minute to
a day (10 minutes to start with).

Next: [Upgrading](upgrading.md).
