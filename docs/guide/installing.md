# Installing

Melodex runs as a few containers: the core (the web app and API), the beets worker (the only thing that writes your
music folder), and one container per retrieval plugin. Navidrome isn't part of the stack: Melodex joins the one you
have.

## With Docker Compose

Everything is in one file, [`docker-compose.yml`](https://github.com/dextrd/Melodex/blob/main/docker-compose.yml),
which pulls the released images. Save it into a folder of its own, beside a `.env` file with your settings:

```bash
MUSIC_PATH=/path/to/your/music
PUID=1000
PGID=1000
```

Then start it:

```bash
docker compose up -d
```

and open `http://<your server>:8787`. Setup walks you through the rest: the admin account, Navidrome, plugins and
the first scan.

| Setting | What | Default |
| --- | --- | --- |
| `MUSIC_PATH` | Your music folder, the same one Navidrome reads. Needed. | |
| `PUID`, `PGID` | The user and group that own the music folder, as `id` shows them. The beets worker and plugins run as them. | `1000` |
| `MELODEX_PORT` | The port the web app listens on | `8787` |
| `MELODEX_VERSION` | Which release to run, such as `1.0.0`; `latest` follows each release | `latest` |
| `INBOX_PATH` | The folder the inbox plugin watches for files you drop in | `./inbox` |
| `COMPOSE_PROFILES` | More retrieval plugins to run, comma-separated: `archive`, `prowlarr`, `qobuz`, `tidal`, `bandcamp`, `soulseek` | none |
| `DOWNLOADS_PATH` | qBittorrent's downloads folder, for the `prowlarr` plugin | `./downloads` |
| `SLSKD_DOWNLOADS_PATH` | slskd's downloads folder, for the `soulseek` plugin, which moves what it downloads into staging | `./slskd-downloads` |

### In a compose dashboard

Server dashboards that run compose files, such as OpenMediaVault's (**Services → Compose → Files**), Portainer's
stacks or Dockge, take the same file. Paste `docker-compose.yml` in as a new file or stack, put the settings above in
its environment box (one `NAME=value` a line), and start it. Use full paths for `MUSIC_PATH`, `INBOX_PATH` and
`DOWNLOADS_PATH` and `SLSKD_DOWNLOADS_PATH`, as the dashboard's working folder may not be where you expect.

### Retrieval plugins

yt-dlp (YouTube Music) and the inbox folder always run. The others start only when named in `COMPOSE_PROFILES`, so
the install stays small until you want them:

| Profile | Plugin | Needs |
| --- | --- | --- |
| `archive` | Internet Archive: concerts from the Live Music Archive and netlabel releases | Nothing |
| `prowlarr` | Prowlarr and qBittorrent | Your own Prowlarr and qBittorrent, and `DOWNLOADS_PATH` set to qBittorrent's downloads folder |
| `qobuz` | Qobuz | Your own subscription |
| `tidal` | Tidal | Your own subscription |
| `bandcamp` | Albums and songs you've bought on Bandcamp | Your Bandcamp account |
| `soulseek` | What Soulseek's users share, through slskd | Your own [slskd](https://github.com/slskd/slskd), and `SLSKD_DOWNLOADS_PATH` set to its downloads folder |

For example `COMPOSE_PROFILES=archive,bandcamp`. Once it's running, add it under **Settings → Plugins**, where it's
among the templates under **Add**: see [plugins](plugins.md).

### Prowlarr's save folder, from both sides

qBittorrent and the Prowlarr plugin each see qBittorrent's downloads at their own path, and the plugin's settings take
both. Say qBittorrent's container mounts your pool at `/data` (as the TRaSH guides lay it out), and Melodex's
`DOWNLOADS_PATH` is that pool's torrents folder, mounted in the plugin at `/downloads`:

| Setting | Is | As seen by |
| --- | --- | --- |
| **Save folder** | `/data/torrents/melodex` | qBittorrent |
| **Save folder for this plugin** | `/downloads/melodex` | the plugin |

If **Save folder** isn't somewhere qBittorrent has, it can't write the files: every fetch fails with qBittorrent's
"Permission denied". The plugin's health check warns when the save folder isn't under qBittorrent's own default save
path, and names that path; a failed fetch says what to change.

### Soulseek, through slskd

Soulseek is a network of people sharing their music with each other, and slskd is a client for it you run yourself.
The plugin asks your slskd to search and download, then moves what it downloaded into Melodex's staging, so both
must see slskd's downloads folder: set `SLSKD_DOWNLOADS_PATH` to that folder as your server sees it, writable. Give
the plugin one of the API keys in slskd's settings (`web.authentication.api_keys`).

slskd expects you to share too, and some people won't send to those who share nothing; the plugin's health says when
you don't. Sharing is up to you: slskd's `shares.directories` setting can point at your music folder, read-only. What
people share is theirs to share, or not, so the plugin is off until you add it, and is best placed late in the order,
after the retrievers you'd rather use.

## The folders Melodex keeps

| Volume | Holds |
| --- | --- |
| `config` | The database, cached artwork, the keys that sign sessions, and the logs. Back this up. |
| `staging` | Downloads waiting to be checked and filed. Shared by the core, beets and the plugins. |
| `listen-now` | Songs fetched to listen to without adding them. Each is deleted after a while. |

The music folder is mounted read-only in the core: only the beets worker writes it, so run that as the folder's owner.

## SELinux and podman

On hosts with SELinux, files carry the label of the container that wrote them, and downloads pass from a plugin through
beets to the core and Navidrome. Give every Melodex container, and Navidrome, the same
`security_opt: ["label=level:s0:c100,c200"]` so they can read each other's files.

## From source

With the .NET 10 SDK:

```bash
git clone https://github.com/dextrd/Melodex.git
cd Melodex
dotnet run --project src/Melodex.Core
```

This runs the core and web app only. Fetching needs the beets worker and a retrieval plugin; `docker compose -f
docker-compose.yml -f docker-compose.build.yml up --build` builds and runs them all from the source.

Next: [Getting started](getting-started.md).
