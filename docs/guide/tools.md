# Tools

The **Tools** page holds things you run now and then: importing lists of songs, and exporting your playlists and
library. Importing needs the **Request** permission; what it does shows in **Activity** as it goes.

## Importing from a list

**Import from a list** turns a list of songs into a playlist, and asks for the missing ones. It reads:

- **M3U and M3U8 playlists**, from another player or media server. Each song is read from its `#EXTINF` line, or worked
  out from its file's path: the file's name for the title, its folders for the album and artist.
- **CSV files**, from a spreadsheet or another service's export, separated by commas, semicolons or tabs. Melodex guesses
  what each column holds from its name, or from what's in it; check its guesses, and say which column is the title,
  artist, album, length, ISRC or MusicBrainz ID. Columns you don't need are left out.
- **Lines you paste**, one song to a line, as "Artist – Title". Numbering such as "1." is left off, and lines starting
  with `#` are skipped.

Choose **Match**, and each song is looked for: first in your library, by its file's path, its IDs or its name and
length; then in the catalogue, by its IDs, its ISRC, or a search. A long list takes a while, about a second for each song
you don't have, and goes on if you leave the page.

Each song then shows what it was matched to:

- **Owned**: a song you have.
- **Missing**: found in the catalogue, but not in your library.
- **Check it**: something like it was found, but Melodex isn't sure it's the same song.
- **Couldn't match**: nothing like it was found.

Check the matches before going on. The magnifier beside a song searches for the one it really is, and the cross leaves
it out (the arrow puts it back).

Then say what to do with them:

- **Make a playlist of them**, named as you like (a folder too, such as `Imported/Evening`), or **add them to the end
  of a playlist** you have, or make **no playlist**. Songs go in the list's order. Making a playlist needs the
  **Listen** permission too.
- **Ask for the missing songs**, as requests, to add to your library. In the playlist, missing songs hold their places,
  greyed out, until they arrive: "missing", or "on its way" once they're asked for. A playlist with missing songs has a
  **Get** button, to ask for them all later, and each has **Get it into your library** in its menu.
- **Send the songs that couldn't be matched to Review**, under **Imports**. There, **Choose** searches for the song it is,
  and it goes into the playlist where it was in the list, asked for too if the import asked for the rest. **Dismiss**
  takes one off the list.

Each import shows in **Activity** as it's matched, and opens from there. The bottom of an import's page deletes it, or
once it's applied forgets it, leaving its playlist and requests as they are.

## Importing from Spotify

**Import from Spotify** reads the copy of your data Spotify gives you, so your playlists, liked songs and saved albums
come across. It needs no Spotify account linked to Melodex.

To get the copy, sign in on Spotify's website, open **Account → Privacy settings**, tick **Account data** (the extended
streaming history isn't needed) and ask for it. Spotify emails a link to a zip, usually within a few days. Choose the zip,
or the JSON files from it (`YourLibrary.json` and the `Playlist` files).

Melodex lists your **liked songs**, **saved albums** and each **playlist**, with how many songs each has. Followed artists
aren't imported. Untick any you don't want, and choose the folder the playlists go in (Spotify to start with) and what
liked songs' playlist is called. Then see [checking and applying](#checking-and-applying-the-lists).

## Importing from YouTube Music

**Import from YouTube Music** reads your Google Takeout, so your library songs and playlists come across. It needs no
Google account linked to Melodex.

To get the copy, at [Google Takeout](https://takeout.google.com) choose **Deselect all**, tick **YouTube and YouTube
Music**, and under **All YouTube data included** keep **music library songs** and **playlists** (the rest isn't
needed). It usually comes within the hour. Choose the zip, or the CSV files from its `playlists` and
`music (library and uploads)` folders.

Melodex lists the **songs in your library** and each **playlist**. Takeout doesn't include liked songs, and **Watch
later** is left out. YouTube's other playlists are listed too, music or not, so untick any of videos, and choose the
folder the playlists go in (YouTube Music to start with).

YouTube's playlists hold only each video's ID: Melodex asks YouTube for each one's title and channel as it matches them
(a song from YouTube Music's own catalogue has an "Artist - Topic" channel, which names its artist), and a video that's
gone or private is shown as couldn't match.

## Checking and applying the lists

Each list is matched as a list import is, by its songs' titles, artists and albums. Neither service's copy has lengths
or ISRCs, so it's worth checking the matches. Open a list on the import's page to watch its songs matched as it goes;
**Check its matches** opens it to choose another song or leave one out.

Then **Apply** them all: each playlist (and Spotify's liked songs, and YouTube Music's library songs) becomes a
playlist, and Spotify's saved albums can be asked for. **Ask for the missing songs and albums** asks for every song found
in the catalogue that you don't own, as well as the albums; missing songs hold their places in the playlists either way,
and songs that couldn't be matched can wait in Review, as with a list import.

**Importing again updates.** Melodex knows each playlist by its name on the service, and each song by the service's ID
for it, so a later copy brings only what's new since: new songs go on the end of the playlist made the first time, and a
list with nothing new is shown as such and left out. Songs taken off a playlist on the service stay in Melodex's.

## Exporting

**Export** gets your playlists and library out of Melodex, as files to save.

**Playlists**, for other players, other services' importers, or keeping (this needs the **Listen** permission). Choose
which; all are chosen to start with, or only the one whose page you came from (by the export button on a playlist's
page). One downloads as a file, and several as a zip of them, in their folders. Each can be:

- **M3U8**, for other players and media servers, with each song's length, artist, title and album, and its file. Its
  path is in the music folder (such as `Artist/Album/01 Song.flac`), so the playlist works from there; or say where
  the music folder is on the machine that will open it (such as `/mnt/music`), and paths start there.
- **CSV**, for other services' importers and spreadsheets: each song's artist, title, album, length, ISRC and
  MusicBrainz ID. It's the shape **Import from a list** reads back.

Songs you don't have are left out, unless you keep them by name only: an M3U8 then lists them with no file, for
importers that match by name.

**The library**, as a CSV, with a row for each song you have (matched, or your own), or each album you have some of,
and the columns you choose: artist, album, title, year, disc and track number, length, path, format, quality, ISRC and
MusicBrainz ID for songs; artist, album, year, how much of it you have, format, quality and MusicBrainz ID for albums.
