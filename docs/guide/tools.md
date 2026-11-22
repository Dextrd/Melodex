# Tools

The **Tools** page holds things you run now and then. **Import / Export** has importing lists of songs, and exporting
your playlists and library; importing needs the **Request** permission. **Formatting** has what writes your music files
themselves, over the whole library: [formatting](library.md#formatting) and [even volume](library.md#even-volume),
which need the **Format** permission. What each does shows in **Activity** as it goes.

## Importing from a list

**Import from a list** turns a list of songs into a playlist, and asks for the missing ones. It reads:

- **M3U and M3U8 playlists**, from another player or media server. Each song is read from its `#EXTINF` line, or worked
  out from its file's path: the file's name for the title, its folders for the album and artist.
- **CSV files**, from a spreadsheet or another service's export, separated by commas, semicolons or tabs. Melodex guesses
  what each column holds from its name, or from what's in it; check its guesses, and say which column is the title,
  artist, album, length, ISRC or MusicBrainz ID. Columns you don't need are left out.
- **Lines you paste**, one song to a line, as "Artist – Title". Numbering such as "1." is left off, and lines starting
  with `#` are skipped.

Choose **Start Import**, and each song is looked for: first in your library, by its file's path, its IDs or its name and
length; then in the catalogue, by its IDs, its ISRC, or a search. A long list takes a while, about a second for each song
you don't have, and goes on if you leave the page.

Each song then shows what it was matched to:

- **Owned**: a song you have.
- **Missing**: found in the catalogue, but not in your library.
- **To review**: something like it was found, but Melodex isn't sure it's the same song.
- **Couldn't match**: nothing like it was found.

Check the matches before going on. The magnifier beside a song searches for the one it really is, and its tick says
whether it's imported: untick it to leave it out.

Then say what to do with them:

- **Make a playlist of them**, named as you like (a folder too, such as `Imported/Evening`), or **add them to the end
  of a playlist** you have, or make **no playlist**. Songs go in the list's order. Making a playlist needs the
  **Listen** permission too.
- **Get the missing songs**, as requests, to add to your library: only those marked Missing. In the playlist, missing
  songs hold their places, greyed out, until they arrive: "missing", or "on its way" once they're asked for. A playlist
  with missing songs has a **Get** button, to ask for them all later, and each has **Get it into your library** in its
  menu.

Then **Apply Import**. The page stays open, and each song asked for shows how it's getting on, as in Activity.

Songs that couldn't be matched, or are to review, go to Review once it's applied, under **Matching**, beside the files
that couldn't be matched. Each shows the import it came from and the closest song the catalogues had, with how sure that
is. **Approve** takes the suggestion, and the search button looks for the song it is; either way it goes into the
playlist where it was in the list, asked for too if the import asked for the rest. **Dismiss** takes one off the list.

Each import shows in **Activity** as it's matched, and opens from there. The bottom of an import's page deletes it, or
once it's applied forgets it, leaving its playlist and requests as they are.

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

## Importing from Deezer

**Import from Deezer** reads the copy of your personal data Deezer gives you, so your playlists, favourite tracks and
favourite albums come across. It needs no Deezer account linked to Melodex.

To get the copy, on Deezer's website open **Account settings → Privacy settings** and, under **My personal data**, ask
for it. An email brings a link to a spreadsheet (`deezer-data_….xlsx`), usually within a day. Choose it as it is.

The spreadsheet has a sheet for each kind of data Deezer keeps, your account's details and listening among them.
Melodex opens only three: your **favourite tracks**, which become a playlist (call it what you like), your **favourite
albums**, which can be asked for, and **each playlist you made**. The rest is left unread. Favourite artists are left
out too: they're artists you follow, not music to fetch.

Deezer's copy names each song's ISRC, so its songs match surely, by the recording itself.

## Checking and applying the lists

Choose **Start Import**, and each list is matched as a list import is, by its songs' ISRCs where the copy has them, and
otherwise their titles, artists and albums. YouTube Music's copy has no lengths or ISRCs, so it's worth checking the
matches. The list being matched opens by itself and comes
into view, closing the one before unless you opened it yourself or are using it. Untick any song you don't want, or open
**Check its matches** to choose another song.

Then **Apply Import**: each playlist, and YouTube Music's library songs, becomes a playlist. **Get the missing songs**
asks for every song marked Missing; missing songs hold their places in the playlists either way. The page stays open, following each list's songs
as they're fetched. Songs that couldn't be matched, or are to review, go to Review, as with a list import.

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
