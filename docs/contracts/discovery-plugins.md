# Discovery plugins (contract v1.1)

Discovery plugins find music related to what someone plays: artists like an artist, songs like a song, and songs to
recommend to a listener, and an artist's genres (since 1.1), each with a score. They sit under Discovery in Settings → Plugins. Code:
`Melodex.Contracts.Plugins.Discovery`.

## How the core uses them

1. An artist's page shows **Similar artists**, and a song's page **Similar songs**. `GET /discovery/recommendations`
   lists songs recommended for the signed-in person.
2. The core asks every enabled discovery plugin that declares the capability, at once, and keeps each one's answer: a
   day for similar artists and songs, six hours for recommendations. New settings are asked afresh.
3. Each plugin's scores are scaled to its own best, so they needn't share a scale, and weighed by the plugin's place in
   the user's order: the first counts fully, each after it a quarter less, down to a quarter. What several plugins find
   is the same when it shares a MusicBrainz ID or ISRC, or a name (title and artist, for songs), and its scores are
   added, so what they agree on comes first. The combined score is scaled back to between 0 and 1.
4. What they find is matched to the catalogue: by an ID it already has; for an artist with a MusicBrainz ID while a
   catalogue uses them, by adding them to the catalogue as a search would; else by name, only when one artist the
   catalogue holds has it (or one in the library does). A song is matched by its IDs, else by its title among its
   artist's. Artists that aren't matched are still listed, by name, and searched for when opened; songs that aren't
   are left out, so plugins are asked for more than are listed.
5. A plugin that throws is left out of that answer, logged, and asked again next time.

## `IDiscoveryPlugin`

| Method | Purpose | Notes |
| --- | --- | --- |
| `SimilarArtistsAsync(artist, count)` | Artists like this one, closest first | Only with `similar-artists`. Defaults to none. |
| `SimilarRecordingsAsync(recording, count)` | Songs like this one, closest first | Only with `similar-recordings`. Defaults to none. |
| `RecommendAsync(listener, count)` | Songs to recommend to a listener, best first | Only with `recommendations`. Defaults to none. |
| `ArtistGenresAsync(artist, count)` | An artist's genres, the most fitting first | Only with `artist-genres` (1.1). Defaults to none. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button. |

Return an empty list when the plugin knows nothing for the query; throw for failures, such as the service being down.
Leave out what was asked about itself; the core leaves it out too.

`DiscoveryArtist` is a name and the artist's IDs as `ProviderId`s, such as their MusicBrainz artist ID.
`DiscoveryRecording` has the song's title, artist credit, IDs (its MusicBrainz recording ID and ISRCs), the credited
artists with their IDs, and its length.

`DiscoveryListener` has the person's `accounts` on services they've linked to Melodex with a
[scrobbling plugin](scrobbling-plugins.md), by the last part of its ID (`listenbrainz` or `lastfm` for the ones that
come with Melodex), and what they've played most over the last 90 days: up to ten `topArtists` and ten
`topRecordings`, most first. A plugin that recommends from what its service knows of someone answers only when they've
linked it; one that recommends from what's played can use the rest.

Answers are `DiscoveredArtist` (name, IDs, score, and a disambiguation if the plugin has one) and `DiscoveredRecording`
(title, artist credit, IDs, credited artists with their IDs, score, and an album and length if the plugin knows them).
Higher scores are better, in the plugin's own terms. Give MusicBrainz IDs where you have them: they're what matches
best. `DiscoveredGenre` is a genre's name, in lower case as MusicBrainz names genres (`trip hop`), and a score, such as
how many people tagged the artist with it. Give genres only, not other tags such as places or decades. The core names
daily mixes from them, and keeps each plugin's answer for a week.

## Capabilities

| Capability | Meaning |
| --- | --- |
| `similar-artists` | Artists like an artist |
| `similar-recordings` | Songs like a song |
| `recommendations` | Songs to recommend to a listener |
| `artist-genres` | An artist's genres (1.1) |

## Built-in plugins

| Plugin | Notes |
| --- | --- |
| ListenBrainz | Set up by default; no account. Similar artists and songs from [ListenBrainz](https://listenbrainz.org)'s labs API, worked out from which ones its listeners play in the same sessions; it knows them by MusicBrainz ID. It keeps a song's listens on one of its recordings, so when the song's own recording has nothing, it looks up the one it uses for that title and artist and asks about that instead. Recommendations are its collaborative filtering's songs for the person's linked ListenBrainz account, made from the listens sent to it, once ListenBrainz has made them. Genres are the tags MusicBrainz's users gave the artist that are genres, by how many gave each. Kept to one call a second, as ListenBrainz asks. |
| Last.fm | Needs an API key of your own, free from [last.fm/api/account/create](https://www.last.fm/api/account/create); it can be the one for scrobbling. Similar artists and songs from [Last.fm](https://www.last.fm)'s `artist.getSimilar` and `track.getSimilar`, asked by MusicBrainz ID, then by name if Last.fm doesn't know the ID. |
