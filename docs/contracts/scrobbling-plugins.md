# Scrobbling plugins (contract v1.0)

Scrobbling plugins send what people play to a listening service, such as ListenBrainz or Last.fm, on accounts each
person links themselves. They sit under Scrobbling in Settings → Plugins. Code: `Melodex.Contracts.Plugins.Scrobbling`.

## How the core uses them

1. The admin adds a scrobbling plugin, with whatever settings it needs, such as Last.fm's API key and shared secret.
   ListenBrainz is set up from the start, as it needs nothing.
2. Each person links their own account under **Scrobbling** on their account page. A plugin links one of two ways,
   which its capabilities say: a token the person pastes (`link-token`), or the service's own page, which sends them
   back with one (`link-web`). Either way the core passes the token to `LinkAsync`, and keeps what comes back: the
   account's name to show, and the secret to send plays with, encrypted.
3. Every play someone records, in the web player or a Subsonic player, is queued for each account they've linked. The
   core sends what's waiting oldest first, up to 50 plays at a time (`ScrobblingLimits.Batch`), with the account's
   secret. A play that fails is tried again later, waiting longer each time, up to six hours, for as long as it
   takes. Plays wait while the plugin is off, and go once it's on again; taking a plugin away takes the links made with
   it, and their waiting plays.
4. What someone starts playing is passed to `NowPlayingAsync`, for plugins with `now-playing`. That's only a nicety:
   failures are passed over.
5. The person's linked accounts are also handed to [discovery plugins](discovery-plugins.md) as the listener's
   `accounts`, by the last part of the scrobbling plugin's ID: `melodex.scrobbling.listenbrainz` gives
   `listenbrainz`.

## `IScrobblingPlugin`

| Method | Purpose | Notes |
| --- | --- | --- |
| `LinkPageAsync(callback)` | Where to send someone to allow Melodex | Only with `link-web`. The service sends them back to `callback` with `token` in the address. |
| `LinkAsync(token)` | The account a token is for: its `Name` and the `Secret` to send plays with | Throw `ScrobbleLinkException` with the reason, in words for the person, when the service doesn't take it. |
| `ScrobbleAsync(secret, listens)` | Sends plays, oldest first | At most 50 at a time. |
| `NowPlayingAsync(secret, listen)` | Tells the service what's playing now | Only with `now-playing`. Defaults to nothing. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button: check the plugin's own settings, such as an API key. |

From `ScrobbleAsync`, throw:

- `ScrobbleLinkRefusedException` when the service no longer takes the account's link, such as a token that was
  revoked: nothing more is sent to that account until the person links it again, and its message is shown to them.
- `ScrobbleRejectedException` when the service turns the plays themselves down: they aren't sent again.
- anything else, such as an `HttpRequestException` while the service is down, to have them tried again later.

A `Listen` has the song's title, artist credit, when it started, and where known its album, length, MusicBrainz
recording and release IDs, and the player it was played in: `Melodex` for the web player, or a Subsonic player's name.

## Capabilities

| Capability | Meaning |
| --- | --- |
| `link-token` | Linked with a token the person pastes, such as a ListenBrainz user token. |
| `link-web` | Linked on the service's own page, through `LinkPageAsync`. |
| `now-playing` | Tells the service what's playing now. |

## The plugins that come with Melodex

| Plugin | Notes |
| --- | --- |
| ListenBrainz | Set up by default; nothing to set. People link it with the user token from their [ListenBrainz settings](https://listenbrainz.org/settings/). Sends `single` listens, or `import` for several, and `playing_now`. |
| Last.fm | Needs an API account of your own, free from [last.fm/api/account/create](https://www.last.fm/api/account/create): its API key and shared secret, which signs every call. People link it on Last.fm's page, which sends them back with a token that becomes a session key. Songs of 30 seconds or less aren't sent, as Last.fm doesn't take them. |

A plugin at an address uses the [remote contract](remote-plugins.md#scrobbling).
