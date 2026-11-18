# Developing Melodex

## Layout

| Path | What |
| --- | --- |
| `src/Melodex.Contracts` | Plugin contracts: index, artwork, lyrics and media-server plugins, and the retrieval HTTP wire types |
| `src/Melodex.Core` | The core service: public API at `/api/v1`, data model, library scan and matching, task queue |
| `src/Melodex.Plugins.MusicBrainz` | Built-in catalogue plugins: MusicBrainz releases, at its one request a second, and Cover Art Archive covers |
| `src/Melodex.Plugins.Navidrome` | The built-in media-server connector for Navidrome, over the Subsonic API |
| `src/Melodex.Plugins.Wikimedia` | Built-in artwork plugin: artist photos from Wikimedia Commons, by MusicBrainz ID through Wikidata |
| `src/Melodex.Plugins.Deezer` | Built-in artwork plugin: artist pictures from Deezer's public catalogue, by name |
| `src/Melodex.Plugins.FanartTv` | Built-in artwork plugin: artist pictures from fanart.tv, with your own API key |
| `src/Melodex.Plugins.Lrclib` | Built-in lyrics plugin: synced and plain lyrics from LRCLIB |
| `src/Melodex.Client` | Typed client and models for the public API |
| `src/Melodex.Web` | The web app: its pages, components and scripts, talking only to the public API, shared by the two below |
| `src/Melodex.Web.Browser` | The web app in the browser (Blazor WebAssembly), served by the core |
| `src/Melodex.App` | The Melodex app for Android and iOS (.NET MAUI Blazor Hybrid), not in `Melodex.sln`; see below |
| `tests/Melodex.Core.Tests` | Tests, including the gate test against the test library |
| `beets-worker` | The import engine: files staged downloads into the library with beets ([docs](import-worker.md)) |
| `plugins/retriever-ytdlp` | Retrieval plugin: YouTube Music through yt-dlp |
| `plugins/retriever-inbox` | Retrieval plugin: a folder you drop files into |
| `tests/fixtures/musicbrainz` | Recorded MusicBrainz responses the gate test replays |
| `docs/guide` | The user guide, also shown in the web app under Settings → Help |
| `docs/contracts` | The plugin contracts, written down, including the retrieval OpenAPI spec |
| `tools/testlib` | Generates a test music library from real MusicBrainz data, with an answer key |
| `sandbox` | A throwaway stack (Navidrome, Melodex, the beets worker and both plugins) over a copy of the test library |

## Building and testing

With the .NET 10 SDK:

```bash
python3 tools/testlib/generate.py   # once: the gate test needs the test library, and is skipped without it
dotnet test
```

To try things out without a real library, see [testing.md](testing.md):

```bash
sandbox/sandbox.sh up
```

To build and run the whole stack from source with Docker:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up --build
```

Schema changes need a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Melodex.Core -o Data/Migrations
```

## The app for phones

`src/Melodex.App` is the web app (`src/Melodex.Web`) in a .NET MAUI BlazorWebView. It isn't in `Melodex.sln`, so the
rest builds without the MAUI workloads; open the project on its own. Android builds on Linux, macOS or Windows:

```bash
dotnet workload install maui-android
dotnet build src/Melodex.App -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True   # once
dotnet build src/Melodex.App -f net10.0-android -t:Run   # on a connected phone or a running emulator
```

With the debug build, `chrome://inspect` (or the DevTools protocol through `adb forward`) reaches the page in the web
view. If the emulator crashes as it starts on Linux, its software graphics are the likely cause: start it with
`-gpu host`.

iOS needs a Mac with Xcode and `dotnet workload install maui-ios`; the project only targets it there. CI builds both,
iOS for the simulator only, as it needs no signing.

How it fits together:

- **Signing in** goes through `AppHost`. In a browser it's the session cookie; in the app, `DeviceHost` signs in with
  `POST /api/v1/auth/devices` and keeps the device's key for that server in secure storage. The app's `HttpClient`
  sends the key and keeps no cookies.
- **What the page asks for itself**, covers, songs, bars, events and exports, is on the app's own origin
  (`https://0.0.0.1/api/...`). `ServerProxy` answers those from the server in use, with the key, passing on `Range`
  so songs can be played from anywhere. Links to the API's own pages, such as the reference, open in the browser.
- **Choosing a server** is the app's own page (`Components/Connect.razor`). Changing server, like a factory reset, goes through
  `AppHost.Restart`: the app swaps in a new web view rather than reloading the page, which the web view wouldn't start
  Blazor on again, so every scoped service starts afresh on the new one.
- **Local storage** is the web view's, as a browser's would be: view preferences and the theme last used.
- An app connects only to a server that has `POST /api/v1/auth/devices`, which it checks when connecting.

Release APKs are signed with the key in the repository's `ANDROID_KEYSTORE` secret (base64), with
`ANDROID_KEYSTORE_PASSWORD` and `ANDROID_KEY_ALIAS`. Without them, each release is signed with a throwaway key, and
won't install over the last one.

## Releases

Versions follow [semantic versioning](https://semver.org). The version lives in `Directory.Build.props`. To release:

1. Move the *Unreleased* section of `CHANGELOG.md` under a heading for the new version, such as
   `## [1.0.0] - 2026-11-20`, and set `<Version>` in `Directory.Build.props` to match.
2. Tag the commit `v<version>` and push the tag.

The release workflow builds the images, publishes them to the GitHub Container Registry as `<version>`,
`<major>.<minor>` and `latest`, and makes a GitHub release with the changelog's notes.
