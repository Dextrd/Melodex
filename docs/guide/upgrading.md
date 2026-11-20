# Upgrading

## To a new version

The compose file runs the release named by `MELODEX_VERSION` (`latest` by default). To upgrade:

```bash
docker compose pull
docker compose up -d
```

The database is migrated when the core starts. Back up the `config` volume first if you want a way back: an older
version can't open a database a newer one has migrated. What changed in each release is in the
[changelog](https://github.com/dextrd/Melodex/blob/main/CHANGELOG.md).

## From 1.0.2 or earlier: the images' new home

Melodex moved to the [dextrd](https://github.com/dextrd) organisation after 1.0.2. Releases since are published as
`ghcr.io/dextrd/melodex…`, where they were `ghcr.io/joeatethebeans/melodex…`. The old images stay where they are, but
get no new releases, so an install still naming them stays on 1.0.2.

To move over, change `joeatethebeans` to `dextrd` in each `image:` line of your compose file (in a dashboard such as
OpenMediaVault's, edit the stack's compose there), or take the new
[`docker-compose.yml`](https://github.com/dextrd/Melodex/blob/main/docker-compose.yml) and keep your `.env`. Then pull
and start it as above. Nothing else changes: your settings, library and data carry on.

## To 2.0.0: retrieval plugins on their own

The retrieval plugins have moved to repositories of their own (see [plugins](plugins.md#retrieval-plugins)), and
Melodex's compose file no longer has them. Their images keep their names, so a compose file from before carries on
as it is. Two things change:

- **Versions.** Each plugin now has releases of its own, starting at 2.0.0. If your plugin services use
  `${MELODEX_VERSION}`, it works for 2.0.0, but later Melodex versions won't have plugin images of the same number:
  give each plugin `latest`, or a version of its own such as `2`, instead.
- **A new compose file.** If you take the new `docker-compose.yml`, it has only the core and the beets worker, and
  `COMPOSE_PROFILES` does nothing. Paste back the plugins you use from their READMEs, where the lines are the same as
  before. Your plugins' settings are kept in Melodex, so nothing needs adding again under **Settings → Plugins**.

## To 2.0.0: discovery

Melodex sets up the new ListenBrainz discovery plugin once, as it did LRCLIB for lyrics, so artists' and songs' pages
show similar music. It sends ListenBrainz the MusicBrainz IDs of the artists and songs whose pages you open. To turn it
off, remove it under **Settings → Plugins → Discovery**; it isn't set up again.

## To 1.7.0: scheduled tasks

Scans, catalogue updates, lossless checks, lyrics, the visualiser's bars, failed downloads, wishlists and upgrades can
run on a schedule,
under the new **Settings → Scheduled tasks**: see [Scheduled tasks](configuration.md#scheduled-tasks). Your upgrade
schedule moves there as it was: its times and time zone, on the daily schedule if it ran every day, or else the weekly
one on the first of its days. Nothing else is put on a schedule, so everything runs as it did until you tick it.

If the visualiser's bars are kept beside your songs (**When matching** under **Settings → Playback**), their `.bars`
files are made again in a fuller format the next time the visualiser's bars are made, after a scan or on a schedule,
and take about a third more room. Until then, a song's are made as it plays.

Melodex can be played from [Subsonic players](subsonic-players.md) now. Nothing changes until you make an API key for
one under **Settings → API**. The player's queue moves from each browser to the server the first time it's opened
after upgrading, so it then follows you to other browsers and players. Plays are kept from now on, in each person's
[history](library.md#history), and can be sent to ListenBrainz or Last.fm; nothing played before upgrading is in it. The next scan reads every song's genres from its tags, once, so it takes a little longer than
usual.

## To 1.1.0: upgrades replace files

1.1.0 upgrades songs below your quality cutoff, replacing their files in your music folder with better copies, and a
scheduled look for them is on from the start (Mondays, 01:00 to 06:00). If you'd rather look before anything is
replaced, untick **Look for upgrades** under **Settings → Scheduled tasks** once it's running (before 1.7.0, **Look
through the whole library for upgrades on a schedule** under **Settings → Quality → Upgrades**); **Look for upgrades**
on a song, album or artist still works by hand. A replaced file is
held for 14 days, and can be put back under **Review → Upgraded** until then.

Your player keeps a song's stars, plays and playlists through an upgrade with Navidrome 0.55 or later; Melodex warns if
yours is older. The beets worker does the replacing, so pull both images, as `docker compose pull` does.

## From a source build

If you ran Melodex from a checkout of the repository with `docker-compose.build.yml`, switch to the released images:

- **To keep your data**, stay in the checkout's folder: `git pull`, then `docker compose pull` and
  `docker compose up -d`, leaving out `-f docker-compose.build.yml` and `--build` from now on. Set `MELODEX_VERSION`
  to the release, as the images you built locally were also called `latest`.
- **To start afresh**, stop the old stack and remove it with its volumes (`docker compose down -v` in its folder),
  then set Melodex up anew, from a folder or a compose dashboard, as in [installing](installing.md). Setup runs again,
  and the first scan matches your music again. Your music folder isn't touched either way.

## From Navidex

Melodex was called Navidex. An install from then upgrades in place, with nothing to do by hand:

- The database file, `navidex.db`, is renamed `melodex.db` on first start.
- Plugins carry on: their IDs, once `navidex.*`, are rewritten.
- API keys starting `ndx_` keep working. New keys start `mdx_`.
- `Navidex__…` environment variables are still read.
- Everyone signs in once more, as the session cookie has a new name.
- Webhook deliveries now carry `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery` headers; update any
  receiver that checks the old `Navidex-` names.

To move your compose setup over:

1. Stop the old stack: `docker compose down` (this keeps its volumes).
2. Replace `docker-compose.yml` with the new one, **in the same folder**. Compose names volumes after the folder, so
   keeping it keeps your `config` volume and its database. If you must use another folder, set
   `COMPOSE_PROJECT_NAME` to the old folder's name.
3. Start it: `docker compose up -d`.

The core's log says *Moved the database from Navidex* on that first start.
