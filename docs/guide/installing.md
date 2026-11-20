# Installing

Melodex runs as a few containers: the core (the web app and API), the beets worker (the only thing that writes your
music folder), and one container for each retrieval plugin you choose, which fetch music. Navidrome isn't part of the stack: Melodex joins the one you
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

### In a compose dashboard

Server dashboards that run compose files, such as OpenMediaVault's (**Services → Compose → Files**), Portainer's
stacks or Dockge, take the same file. Paste `docker-compose.yml` in as a new file or stack, put the settings above in
its environment box (one `NAME=value` a line), and start it. Use full paths for `MUSIC_PATH` and any folder a plugin
needs, as the dashboard's working folder may not be where you expect.

### Retrieval plugins

The compose file has none: Melodex can't fetch anything until you add one. Each has a repository of its own, whose
README has the lines to paste under `services:` in the same file, so it shares Melodex's `staging` volume:

| Plugin | Fetches | Needs |
| --- | --- | --- |
| [YouTube (yt-dlp)](https://github.com/dextrd/melodex-retriever-ytdlp) | Songs and albums from YouTube Music | Nothing |
| [Inbox folder](https://github.com/dextrd/melodex-retriever-inbox) | Files you drop into a folder, matched to requests by their tags | A folder |
| [Internet Archive](https://github.com/dextrd/melodex-retriever-archive) | Concerts from the Live Music Archive and netlabel releases | Nothing |
| [Prowlarr](https://github.com/dextrd/melodex-retriever-prowlarr) | What your torrent indexers offer, with your qBittorrent | Your own Prowlarr and qBittorrent |
| [Qobuz](https://github.com/dextrd/melodex-retriever-qobuz) | Qobuz, in FLAC | Your own subscription |
| [Tidal](https://github.com/dextrd/melodex-retriever-tidal) | Tidal, in FLAC | Your own subscription |
| [Bandcamp](https://github.com/dextrd/melodex-retriever-bandcamp) | Albums and songs you've bought on Bandcamp | Your Bandcamp account |
| [Soulseek](https://github.com/dextrd/melodex-retriever-soulseek) | What Soulseek's users share, through slskd | Your own [slskd](https://github.com/slskd/slskd) |

Then `docker compose up -d` again, and add it under **Settings → Plugins**, where it's among the templates under
**Add**: see [plugins](plugins.md#retrieval-plugins).

### Prowlarr's save folder, from both sides

qBittorrent and the Prowlarr plugin each see qBittorrent's downloads at their own path, and the plugin's settings take
both. Say qBittorrent's container mounts your pool at `/data` (as the TRaSH guides lay it out), and Melodex's
`DOWNLOADS_PATH` in the plugin's compose lines is that pool's torrents folder, mounted in the plugin at `/downloads`:

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
must see slskd's downloads folder: set `SLSKD_DOWNLOADS_PATH` in the plugin's compose lines to that folder as your server sees it, writable. Give
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
