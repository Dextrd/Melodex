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
