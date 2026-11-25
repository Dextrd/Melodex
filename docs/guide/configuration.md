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

Settings is in sections, each page shown to whoever may use it. **Your account** comes first, with what's only yours:
a large button with your initial and name for your account itself (your login, media-server logins and
[scrobbling](library.md#history)), **Appearance** (see [Appearance](appearance.md)) and **API keys** (see the [client
guide](../client-guide.md)). Then:

| Section | Page | Holds |
| --- | --- | --- |
| Library | **Library** | Songs on more than one release, the missing view (each kind of release shown, hidden, or for new songs only), the library format (the folder layout, for new albums and formatting), file metadata, and lyrics |
| | **Quality** | The [quality profile](requests-and-listening.md#quality) and [upgrades](requests-and-listening.md#upgrades) |
| | **Playback** | Which songs play [without a gap](requests-and-listening.md#gapless-playback), when [the visualiser](requests-and-listening.md#the-visualiser)'s bars are made, if at all, how much of a [radio](requests-and-listening.md#radio) is songs you don't have, and whether radios carry on when a queue runs out |
| Downloads | **Fetching** | How many fetches run at once, on the schedule or not, how close a file must be to the song's length, listen now's (how long its songs are kept, and how far ahead they're fetched), and matching |
| | **Scheduled tasks** | What runs on its own, and when: see [Scheduled tasks](#scheduled-tasks) |
| Server | **Plugins** | See [Plugins](plugins.md) |
| | **Users**, **Webhooks** | See [Users and permissions](users-and-permissions.md) and the [client guide](../client-guide.md) |
| | **Log** | Recent activity, and how long logs are kept |
| Melodex | **Help** | This guide |
| | **About** | The version, links to GitHub, who made Melodex, and for the admin, resetting it |

## Scheduled tasks

**Settings → Scheduled tasks** has three schedules, **Daily**, **Weekly** and **Monthly**, each a window between two
times, and below each the tasks it runs: tick a task to put it on that schedule. A task is on one schedule at most, so
ticking it on one takes it off another; one on none only runs when you start it.

- **Scan the library**: read new and changed files and match them, then whatever **Settings → Library** says follows a scan.
- **Update the catalogue**: look up every library artist's discography.
- **Check lossless files**: look for lossy music passed off as lossless in files not checked yet.
- **Look for missing lyrics**: look again for songs without lyrics.
- **Make the visualiser's bars**: for songs without them. Only while **Settings → Playback** keeps bars beside songs:
  made as songs play, or with the visualiser off, it's greyed out, saying why, and one ticked already is left out of
  runs until they're kept beside songs again.
- **Retry failed downloads**: ask again for downloads that failed. Any not started when the window closes go back to
  failed, for the next window.
- **Get what's on wishlists**: ask for what's on everyone's wishlist, each in the name of whoever wished for it, when a
  retriever is on to fetch it. Any not started when the window closes are cancelled, and stay on the wishlist.
- **Look for upgrades**: look through the whole library for [upgrades](requests-and-listening.md#upgrades).

To start with, the daily window is 01:00 to 06:00 and scans, updates the catalogue, checks lossless files and makes
the visualiser's bars; the
weekly one is Mondays at the same times, for lyrics, failed downloads, wishlists and upgrades; nothing is monthly.

- **Each window has its own times.** One that ends earlier than it starts runs past midnight: 22:00 to 04:00 ends the
  next morning. One that ends when it starts lasts a whole day.
- **The weekly one runs on a day you pick, the monthly one on a day of the month, 1 to 31.** In a month shorter than
  that, it runs on the month's last day: 31 is the last day of every month.
- **All three are in one time zone**, the server's UTC to start with: **Use this browser's** sets yours.
- **A window runs its tasks once, in order**: scan, catalogue, lossless check, lyrics, the visualiser's bars, then
  failed downloads, wishlists and upgrades. The first five are tasks on **Activity**, one after another; the rest wait
  for them, as nothing can be asked for while a scan reads the library.
- **Fetches go a few at a time.** Retries and wishlists keep a few fetches going, as many as **Settings → Fetching →
  Scheduled fetches** says (5 to start with), and
  upgrades their own **Upgrades at a time**: as each finishes, the next is asked for in its place, so the queue never
  fills with a window's worth at once. Each waits for its downloads to finish before the next starts, and upgrades go
  last, filling what's left of the window. When the window closes, nothing more is asked for; those not started wait
  for the next window, and those under way carry on.
- **The windows take turns.** When two open on the same day, daily goes first, then weekly, then monthly, as long as
  its window is still open.

Below each schedule are when it next runs, and how its last run went, a line for each task, with a link to its task on
Activity. Every task can still be run straight away from its own place, which says which schedule it's on and links
here: **Scan** and **Update catalogue** in the Library page's Scan menu, **Look for missing lyrics now** under **Settings →
General → Lyrics**, **Make missing bars now** under **Settings → Playback**, **Check the rest** of the lossless files and **Look for upgrades now** under **Settings → Quality**, **Retry
failed** on **Activity → Downloads**, and **Get them all** on the wishlist. Those last two ask for everything at once.

**Sync playlists every** sets how often playlists sync with the media server for changes made there, from 1 minute to
a day (10 minutes to start with).

Next: [Upgrading](upgrading.md).
