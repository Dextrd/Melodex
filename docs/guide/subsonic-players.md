# Subsonic players

Melodex is a music server in its own right: any app that speaks the Subsonic API can browse your library, play it and
keep your playlists. Symfonium, Amperfy, Substreamer, Tempo, Feishin, DSub and many more do, on phones, desktops and
in the browser. You don't need Navidrome for this, though Melodex still works with it if you have it.

## Signing in

Players sign in with an API key, not your password. Under **Settings → API**, make a **New key**, named for the phone
or computer it's for, and tick **Let a Subsonic player sign in with it** (which gives it the **Listen** permission).
Melodex then shows what to type into the player:

- **Server**: the address you open Melodex at, such as `https://music.example.com`. A player on your phone away from
  home needs an address it can reach from there.
- **Username**: yours in Melodex.
- **Password**: the key.

A player that signs in with an API key alone, as OpenSubsonic's newer players can, takes the key without a username,
and any key with **Listen** works for that, ticked or not.

Make one key for each player: if a phone is lost, revoke its key under **Settings → API** and the rest carry on.
Nothing stops one key being used on several, but they'd all be signed out together. Your own password never works in
a player, so it never sits in an app on a phone.

A ticked key is kept encrypted rather than only as a fingerprint, as most players send a scrambled form of their
password that can only be checked against the key itself. Melodex still shows it only once, when it's made.

Anyone can make keys, each able to do only what they may themselves, so you need the **Listen** permission: the admin gives it under **Settings → Users**.

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
