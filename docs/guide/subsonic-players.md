# Subsonic players

Melodex is a music server in its own right: any app that speaks the Subsonic API can browse your library, play it and
keep your playlists. Symfonium, Amperfy, Substreamer, Tempo, Feishin, DSub and many more do, on phones, desktops and
in the browser. You don't need Navidrome for this, though Melodex still works with it if you have it.

## Signing in

Under **Your account**, in the account menu, **Subsonic players** has what to type into a player:

- **Server**: the address you open Melodex at, such as `https://music.example.com`. A player on your phone away from
  home needs an address it can reach from there.
- **Username**: yours in Melodex.
- **App password**: **Make an app password** gives you one, kept apart from your own password. Players sign in with
  it, so your real password never sits in an app on a phone. You can see it again there whenever you need it, and
  **Replace** it if a phone is lost: players still using the old one have to sign in again. **Remove** takes it away.

Players that know OpenSubsonic's API-key sign-in can use an API key instead, with the **Listen** permission: make one
under **Settings → API**. Revoking the key signs out just that player.

You need the **Listen** permission to use a player at all: the admin gives it under **Settings → Users**.

## What players see

- **Your library**, as the Library page shows it: your artists, their albums and the songs you have, with their
  covers, years, genres and lengths. A player that browses by folders sees one folder, Music, with your artists in it,
  their albums in each, and the songs in each album.
- **Search** across artists, albums and songs, every word you type matched, accents or not.
- **Album lists**: newest, random, by name, by artist, by year and by genre. Most played, recently played, highest
  rated and starred are empty for now.
- **Genres**, read from your songs' tags. Files scanned before this version have theirs read on the next scan.
- **Lyrics**, from the song's `.lrc` file or tags, synced line by line for players that show them so.
- **Your playlists**, the same as in Melodex: a playlist made or changed in a player is changed in Melodex, and the
  other way round. Folders show as part of the name. Songs in a playlist that aren't in your library yet keep their
  places, but players don't list them.

Each person sees their own playlists. Everyone sees the whole library.

## Playing

A player gets each song as it is, with seeking, unless it asks for something smaller: if it asks for MP3, Opus or AAC
at most at some bitrate, Melodex turns the song into that as it plays, which suits slow connections and mobile data.
A song already small enough in that format is sent as it is. Downloading for offline listening always gives the file
as it is in your library.

Stars, ratings, the play queue shared between players, and plays counted (scrobbling) aren't there yet.
