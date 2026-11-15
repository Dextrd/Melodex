# Tools

The **Tools** page holds things you run now and then: importing lists of songs, and exporting your playlists and
library. Importing needs the **Request** permission; what it does shows in **Activity** as it goes.

## Importing from a list

**Import from a list** turns a list of songs into a playlist, and asks for the songs you don't have. It reads:

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

- **In your library**: a song you have.
- **To get**: found in the catalogue, but not in your library.
- **Check it**: something like it was found, but Melodex isn't sure it's the same song.
- **Not found**: nothing like it was found.

Check the matches before going on. The magnifier beside a song searches for the one it really is, and the cross leaves
it out (the arrow puts it back).

Then say what to do with them:

- **Make a playlist of them**, named as you like (a folder too, such as `Imported/Evening`), or **add them to the end
  of a playlist** you have, or make **no playlist**. Songs go in the list's order. Making a playlist needs the
  **Listen** permission too.
- **Ask for the songs you don't have**, as requests, to add to your library. In the playlist, songs you don't have
  hold their places, greyed out, until they arrive: "not in your library yet", or "on its way" once they're asked for.
  A playlist with songs you don't have has a **Get** button, to ask for them all later, and each has **Get it into your
  library** in its menu.
- **Send the songs not found to Review**, under **Couldn't match**. There, **Choose** searches for the song it is,
  and it goes into the playlist where it was in the list, asked for too if the import asked for the rest. **Dismiss**
  takes one off the list.

Your imports are listed on the Tools page, newest first. Deleting one leaves its playlist and requests as they are.

## Importing from a music service

**Import from Spotify or YouTube Music** reads the copy of your data a music service gives you, so your playlists, liked
songs and saved albums come across. It needs no account of theirs linked to Melodex.

### Getting the copy

- **Spotify**: sign in on Spotify's website, open **Account → Privacy settings**, tick **Account data** (the extended
  streaming history isn't needed) and ask for it. Spotify emails a link to a zip, usually within a few days. Choose the
  zip, or the JSON files from it (`YourLibrary.json` and the `Playlist` files).
- **YouTube Music**: at [Google Takeout](https://takeout.google.com), choose **Deselect all**, tick **YouTube and YouTube
  Music**, and under **All YouTube data included** keep **music library songs** and **playlists** (the rest isn't
  needed). It usually comes within the hour. Choose the zip, or the CSV files from its `playlists` and
  `music (library and uploads)` folders.

### What comes across

Melodex lists what's in the copy, with how many songs each has:

- From Spotify: your **liked songs**, **saved albums** and each **playlist**. Followed artists aren't imported.
- From YouTube Music: the **songs in your library**, and each **playlist**. Takeout doesn't include liked songs, and
  **Watch later** is left out. YouTube's other playlists are listed too, music or not, so untick any of videos.

Untick any you don't want, and choose the folder the playlists go in (the service's name to start with) and, for
Spotify, what liked songs' playlist is called.

Each list is then matched as a list import is, by its songs' titles, artists and albums. Neither copy has lengths or
ISRCs, so it's worth checking the matches. YouTube's playlists hold only each video's ID: Melodex asks YouTube for each
one's title and channel as it matches them (a song from YouTube Music's own catalogue has an "Artist - Topic" channel,
which names its artist), and a video that's gone or private is shown as not found.

Open any list to check it, choose another song or leave one out, then **Apply** them all: each playlist (and Spotify's
liked songs, and YouTube Music's library songs) becomes a playlist, and Spotify's saved albums you don't have can be asked
for. **Ask for the songs and albums you don't have** asks for every song found in the catalogue that you don't own, as
well as the albums; songs you don't have hold their places in the playlists either way, and songs not found can wait in
Review, as with a list import.

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
