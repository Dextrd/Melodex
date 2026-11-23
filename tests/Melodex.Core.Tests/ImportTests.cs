using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;
using Melodex.Core.Tools;

namespace Melodex.Core.Tests;

/// <summary>Importing songs from a list: reading it, matching its songs, and making a playlist of them.</summary>
public sealed class ImportTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public ImportTests()
    {
        var replay = new FakeCoverArt(new ReplayHandler(TestLibrary.MusicBrainzFixtures));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => replay;
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    // ---- Reading lists ----

    [Fact]
    public void An_m3u_gives_its_songs_from_extinf_lines_or_their_paths()
    {
        const string m3u = """
            #EXTM3U
            #EXTINF:225,Portishead - Glory Box
            Portishead/Dummy (1994)/11 Glory Box.flac
            /music/Daft Punk/Discovery (2001)/03 Digital Love.mp3
            #EXTINF:-1,Radio stream
            http://example.com/stream
            """;
        Assert.Equal(ImportFormat.M3u, ListParser.Detect(m3u, null));
        var songs = ListParser.Read(m3u, ImportFormat.M3u, null, null);
        Assert.Equal(3, songs.Count);
        Assert.Equal(new ImportSong("Glory Box", "Portishead", "Dummy (1994)", 225, Path: "Portishead/Dummy (1994)/11 Glory Box.flac"), songs[0]);
        Assert.Equal(new ImportSong("Digital Love", "Daft Punk", "Discovery (2001)", null, Path: "/music/Daft Punk/Discovery (2001)/03 Digital Love.mp3"), songs[1]);
        Assert.Equal("Radio stream", songs[2].Title);
        Assert.Null(songs[2].Artist);
    }

    [Fact]
    public void A_csv_with_named_columns_is_read_by_their_names()
    {
        // As Exportify writes Spotify playlists: lengths in milliseconds, and an album artist column to leave alone.
        const string csv = """
            Track URI,Track Name,Artist Name(s),Album Name,Album Artist Name(s),Duration (ms),ISRC
            spotify:track:1,Glory Box,Portishead,Dummy,Portishead,305600,GBBKS9400211
            spotify:track:2,"Harder, Better, Faster, Stronger",Daft Punk,Discovery,Daft Punk,224693,GBDUW0000059
            """;
        var peek = ListParser.Peek(csv, "liked.csv");
        Assert.Equal(ImportFormat.Csv, peek.Format);
        Assert.True(peek.HasHeader);
        Assert.Equal(2, peek.Count);
        Assert.Equal([ImportColumn.Ignore, ImportColumn.Title, ImportColumn.Artist, ImportColumn.Album, ImportColumn.Ignore, ImportColumn.Length, ImportColumn.Isrc], peek.Guess);
        Assert.Equal(new ImportSong("Harder, Better, Faster, Stronger", "Daft Punk", "Discovery", 224.693, "GBDUW0000059"), peek.Sample[1]);
    }

    [Fact]
    public void A_csv_without_a_header_has_its_columns_guessed_or_chosen()
    {
        const string csv = "Portishead;Glory Box;5:05\nDaft Punk;Digital Love;4:58\n";
        var peek = ListParser.Peek(csv, null);
        Assert.Equal(ImportFormat.Csv, peek.Format);
        Assert.False(peek.HasHeader);
        Assert.Equal([ImportColumn.Artist, ImportColumn.Title, ImportColumn.Length], peek.Guess);
        Assert.Equal(new ImportSong("Glory Box", "Portishead", null, 305), peek.Sample[0]);

        var chosen = ListParser.Read(csv, ImportFormat.Csv, [ImportColumn.Title, ImportColumn.Ignore, ImportColumn.Ignore], false);
        Assert.Equal(["Portishead", "Daft Punk"], chosen.Select(s => s.Title));
    }

    [Fact]
    public void Pasted_lines_are_artist_and_title_either_side_of_a_dash()
    {
        const string text = "1. Portishead – Glory Box\nDaft Punk - Digital Love\n\n# a note\nTeardrop\n";
        var songs = ListParser.Read(text, ListParser.Detect(text, null), null, null);
        Assert.Equal([("Portishead", "Glory Box"), ("Daft Punk", "Digital Love"), (null, "Teardrop")],
            songs.Select(s => (s.Artist, s.Title)));
    }

    // ---- Importing ----

    [TestLibraryFact]
    public async Task An_import_matches_owned_found_and_missing_songs_and_makes_a_playlist_in_order()
    {
        await ScanAsync();
        const string csv = """
            Artist,Title
            Portishead,Glory Box
            Zzqxv Nobody Band,Qwxyz Never A Song
            Portishead,Hunter
            Daft Punk,Digital Love
            """;
        var import = await _api.CreateImportAsync(new CreateImport(csv, "Evening.csv"));
        Assert.Equal("Evening", import.Name);
        Assert.Equal(4, import.Total);
        var detail = await MatchedAsync(import.Id);

        Assert.Equal([ImportOutcome.Owned, ImportOutcome.NotFound, ImportOutcome.Found, ImportOutcome.Owned], detail.Lines.Select(l => l.Outcome));
        Assert.Equal("Glory Box", detail.Lines[0].Match!.Title);
        Assert.Equal("Hunter", detail.Lines[2].Match!.Title);
        var digitalLove = detail.Lines[3].Match!.Title;
        Assert.StartsWith("Digital Love", digitalLove);

        // Applied without requesting: the song not owned holds its place, and the one not found goes to Review.
        var applied = await _api.ApplyImportAsync(import.Id, new ApplyImport(Name: "Imported/Evening", RequestMissing: false));
        Assert.Equal(new ImportApplied(applied.PlaylistId, 3, 0, 1), applied);
        var playlist = (await _api.GetPlaylistAsync(applied.PlaylistId!.Value))!;
        Assert.Equal("Imported/Evening", playlist.Playlist.Name);
        Assert.Equal([("Glory Box", PlaylistSongState.Library), ("Hunter", PlaylistSongState.Awaiting), (digitalLove, PlaylistSongState.Library)],
            playlist.Songs.Select(s => (s.Title, s.State)));

        // Matched in Review, it goes in where it was in the list: after Glory Box.
        var unmatched = Assert.Single(await _api.GetUnmatchedImportSongsAsync());
        Assert.Equal("Qwxyz Never A Song", unmatched.Song.Title);
        Assert.Equal("Imported/Evening", unmatched.PlaylistName);
        Assert.True(unmatched.Suggestion is null || unmatched.Confidence < Melodex.Core.Tools.ImportMatcher.Close);
        await _api.MatchUnmatchedImportSongAsync(unmatched.LineId, new MatchImportSong(detail.Lines[3].Match!.TrackId!.Value));
        playlist = (await _api.GetPlaylistAsync(applied.PlaylistId.Value))!;
        Assert.Equal(["Glory Box", digitalLove, "Hunter", digitalLove], playlist.Songs.Select(s => s.Title));
        Assert.Empty(await _api.GetUnmatchedImportSongsAsync());

        var listed = Assert.Single(await _api.ListImportsAsync());
        Assert.Equal(ImportState.Applied, listed.State);
        Assert.Equal("Imported/Evening", listed.PlaylistName);
    }

    [TestLibraryFact]
    public async Task A_match_can_be_changed_or_dropped_before_applying()
    {
        await ScanAsync();
        var import = await _api.CreateImportAsync(new CreateImport("Portishead - Glory Box\nDaft Punk - Digital Love\n"));
        var detail = await MatchedAsync(import.Id);
        var other = detail.Lines[1].Match!.TrackId!.Value;

        var changed = await _api.ChangeImportLineAsync(import.Id, detail.Lines[0].Id, new ChangeImportLine(TrackId: other));
        Assert.True(changed.Chosen);
        Assert.Equal(detail.Lines[1].Match!.Title, changed.Match!.Title);
        var dropped = await _api.ChangeImportLineAsync(import.Id, detail.Lines[1].Id, new ChangeImportLine(Dropped: true));
        Assert.Equal(ImportOutcome.Dropped, dropped.Outcome);
        Assert.Equal(ImportOutcome.Owned, (await _api.ChangeImportLineAsync(import.Id, detail.Lines[1].Id, new ChangeImportLine(Dropped: false))).Outcome);
        await _api.ChangeImportLineAsync(import.Id, detail.Lines[1].Id, new ChangeImportLine(Dropped: true));

        var applied = await _api.ApplyImportAsync(import.Id, new ApplyImport(RequestMissing: false));
        Assert.Equal([detail.Lines[1].Match!.Title], (await _api.GetPlaylistAsync(applied.PlaylistId!.Value))!.Songs.Select(s => s.Title));
        Assert.Equal("Pasted list", (await _api.GetPlaylistAsync(applied.PlaylistId.Value))!.Playlist.Name);
    }

    [TestLibraryFact]
    public async Task A_song_to_review_goes_to_review_with_its_match_suggested_and_is_not_asked_for()
    {
        await ScanAsync();
        var import = await _api.CreateImportAsync(new CreateImport("Portishead - Glory Box\nDaft Punks Tribute - Digital Love\n"));
        var detail = await MatchedAsync(import.Id);
        var unsure = detail.Lines[1];
        Assert.Equal(ImportOutcome.Unsure, unsure.Outcome);

        var applied = await _api.ApplyImportAsync(import.Id, new ApplyImport(RequestMissing: true));
        Assert.Equal(new ImportApplied(applied.PlaylistId, 1, 0, 1), applied);
        Assert.Equal(["Glory Box"], (await _api.GetPlaylistAsync(applied.PlaylistId!.Value))!.Songs.Select(s => s.Title));
        var waiting = Assert.Single(await _api.GetUnmatchedImportSongsAsync());
        Assert.Equal(unsure.Match!.TrackId, waiting.Suggestion!.TrackId);
        Assert.Equal(unsure.Confidence, waiting.Confidence);
    }

    // ---- Services' exports ----

    private const string YourLibrary = """
        {"tracks":[{"artist":"Portishead","album":"Dummy","track":"Glory Box","uri":"spotify:track:glory"},
                   {"artist":"Portishead","album":"Third","track":"Hunter","uri":"spotify:track:hunter"}],
         "albums":[{"artist":"Portishead","album":"Dummy","uri":"spotify:album:dummy"},
                   {"artist":"Daft Punk","album":"Discovery","uri":"spotify:album:discovery"}],
         "artists":[{"name":"Portishead","uri":"spotify:artist:p"}],"shows":[],"episodes":[],"bannedTracks":[],"other":[]}
        """;

    private const string OneMore = """
        ,{"track":{"trackName":"One More Time","artistName":"Daft Punk","albumName":"Discovery","trackUri":"spotify:track:onemore"}}
        """;

    private static string Playlists(bool more) => $$"""
        {"playlists":[{"name":"Road trip","lastModifiedDate":"2026-10-01","items":[
          {"track":{"trackName":"Glory Box","artistName":"Portishead","albumName":"Dummy","trackUri":"spotify:track:glory"},"episode":null,"localTrack":null,"addedDate":"2026-10-01"},
          {"track":null,"episode":{"episodeName":"A podcast","showName":"Show","episodeUri":"spotify:episode:x"},"localTrack":null,"addedDate":"2026-10-01"},
          {"track":null,"episode":null,"localTrack":{"uri":"spotify:local:Daft+Punk:Homework:Da+Funk:328"},"addedDate":"2026-10-01"},
          {"track":{"trackName":"Digital Love","artistName":"Daft Punk","albumName":"Discovery","trackUri":"spotify:track:digital"},"episode":null,"localTrack":null,"addedDate":"2026-10-02"}
          {{(more ? OneMore : "")}}
        ],"description":null,"numberOfFollowers":0}]}
        """;

    private static byte[] SpotifyZip(bool more)
    {
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("Spotify Account Data/YourLibrary.json", YourLibrary), ("Spotify Account Data/Playlist1.json", Playlists(more)),
                         ("Spotify Account Data/Userdata.json", """{"username":"someone"}""") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return bytes.ToArray();
    }

    [Fact]
    public void A_spotify_export_gives_its_liked_songs_saved_albums_and_playlists()
    {
        var export = ServiceExports.Read([("my_spotify_data.zip", SpotifyZip(more: false))])!;
        Assert.Equal(ImportFormat.Spotify, export.Service);
        Assert.Equal([("liked", ServiceListKind.Liked, 2), ("albums", ServiceListKind.Albums, 2), ("playlist:Road trip", ServiceListKind.Playlist, 3)],
            export.Lists.Select(l => (l.Key, l.Kind, l.Items.Count)));
        var trip = export.Lists[2].Items;
        Assert.Equal(new ImportSong("Da Funk", "Daft Punk", "Homework", 328), trip[1].Song);
        Assert.Equal("spotify:track:digital", trip[2].Id);
        Assert.Equal(new ImportSong("Discovery", "Daft Punk", null, null), export.Lists[1].Items[1].Song);
        Assert.Null(ServiceExports.Read([("notes.json", "{\"hello\":1}"u8.ToArray())]));
    }

    [Fact]
    public void A_youtube_takeout_gives_its_library_songs_and_playlists_by_video()
    {
        const string root = "Takeout/YouTube and YouTube Music/";
        var files = new (string, string)[]
        {
            (root + "music (library and uploads)/music library songs.csv",
                "Video ID,Song Title,Album Title,Artist Name 1,Artist Name 2,Artist Name 3\nvid1,Glory Box,Dummy,Portishead,Geoff Barrow,\nvid2,\"Harder, Better, Faster, Stronger\",Discovery,Daft Punk,,\n\n\n"),
            (root + "playlists/playlists.csv", "Playlist ID,Add new videos to top,Playlist Title (Original),Playlist Visibility\nPL1,False,Evening,Private\n"),
            (root + "playlists/Evening-videos.csv", "Video ID,Playlist Video Creation Timestamp\nvid2,2026-11-15T01:54:20+00:00\nvid9,2026-11-15T01:54:27+00:00\n\n"),
            (root + "playlists/Watch later-videos.csv", "Video ID,Playlist Video Creation Timestamp\nvid7,2026-10-22T00:17:37+00:00\n"),
            (root + "comments/comments.csv", "Comment ID,Channel ID\nx,y\n"),
        };
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

        var export = ServiceExports.Read([("takeout.zip", bytes.ToArray())])!;
        Assert.Equal(ImportFormat.YouTubeMusic, export.Service);
        Assert.Equal(["library", "playlist:Evening"], export.Lists.Select(l => l.Key));
        Assert.Equal(new ImportSong("Harder, Better, Faster, Stronger", "Daft Punk", "Discovery", null), export.Lists[0].Items[1].Song);
        // A playlist's video the library has is named from it; the other is only its ID, to look up.
        Assert.Equal([("Harder, Better, Faster, Stronger", "youtube:vid2"), ("", "youtube:vid9")], export.Lists[1].Items.Select(i => (i.Song.Title, i.Id)));
    }

    [Fact]
    public void An_apple_export_gives_its_favourites_library_and_playlists_from_the_zips_inside_it()
    {
        const string tracks = """
            [
              {"Content Type": "Song", "Track Identifier": 101, "Title": "Glory Box", "Artist": "Portishead", "Album": "Dummy", "Track Duration": 306000,
               "Playlist Only Track": false, "Apple Music Track Identifier": 5001},
              {"Content Type": "Song", "Track Identifier": 102, "Title": "Da Funk", "Artist": "Daft Punk", "Album": "Homework", "Track Duration": 328000,
               "Playlist Only Track": true, "Apple Music Track Identifier": 5002, "Track Like Rating": "liked", "Favorite Status - Track": true},
              {"Content Type": "Song", "Track Identifier": 103, "Title": "Teardrop", "Artist": "Massive Attack", "Album": "Mezzanine", "Track Duration": 330000,
               "Playlist Only Track": true}
            ]
            """;
        const string playlists = """
            [
              {"Container Type": "Favorites Playlist", "Container Identifier": 2, "Title": "Favorite Songs", "Playlist Item Identifiers": [102]},
              {"Container Type": "Playlist", "Container Identifier": 7, "Title": "Evening", "Playlist Item Identifiers": [103, 101]}
            ]
            """;
        const string favourites = "\"Favorite Type\",\"Item Reference\",\"Item Description\",\"Last Modified\",Preference\n"
            + "Song,5002,\"Daft Punk - Da Funk\",2026-11-15T01:47:03Z,LIKE\nSong,5009,\"Air - La Femme d'Argent\",2026-11-15T01:47:09Z,LIKE\n"
            + "Artist,77,\"Air\",2026-11-15T01:47:10Z,LIKE\n";
        const string folder = "Apple_Media_Services/Apple Music Activity/";
        var inner = Zip([
            (folder + "Apple Music Library Tracks.json.zip", Zip([("Apple Music Library Tracks.json", System.Text.Encoding.UTF8.GetBytes(tracks))])),
            (folder + "Apple Music Library Playlists.json.zip", Zip([("Apple Music Library Playlists.json", System.Text.Encoding.UTF8.GetBytes(playlists))])),
            (folder + "Apple Music - Favorites.csv", System.Text.Encoding.UTF8.GetBytes(favourites)),
            // Not opened: only Apple Music's library and favourites are read.
            ("Apple_Media_Services/Apple Media Services/Billing Information History.csv", "Video ID,Song Title\nx,Not music\n"u8.ToArray()),
        ]);
        var outer = Zip([("Apple Media Services information Part 1 of 2/Apple_Media_Services.zip", inner)]);

        var export = ServiceExports.Read([("Apple Media Services information Part 1 of 2.zip", outer)])!;
        Assert.Equal(ImportFormat.AppleMusic, export.Service);
        Assert.Equal([("liked", 2), ("library", 1), ("playlist:Evening", 2)], export.Lists.Select(l => (l.Key, l.Items.Count)));
        Assert.Equal([("Da Funk", "Daft Punk", "apple:song:5002"), ("La Femme d'Argent", "Air", "apple:song:5009")],
            export.Lists[0].Items.Select(i => (i.Song.Title, i.Song.Artist, i.Id)));
        Assert.Equal(new ImportSong("Glory Box", "Portishead", "Dummy", 306), export.Lists[1].Items[0].Song);
        // Playlists in their own order; a song of only your own has its library ID.
        Assert.Equal(["apple:library:103", "apple:song:5001"], export.Lists[2].Items.Select(i => i.Id));

        static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> files)
        {
            using var bytes = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, data) in files)
                {
                    using var entry = zip.CreateEntry(name).Open();
                    entry.Write(data);
                }
            return bytes.ToArray();
        }
    }

    [Fact]
    public void A_deezer_export_gives_its_favourites_and_playlists_and_leaves_the_rest_unread()
    {
        var export = ServiceExports.Read([("deezer-data_1.xlsx", DeezerXlsx(more: false, isrcs: true))])!;
        Assert.Equal(ImportFormat.Deezer, export.Service);
        Assert.Equal([("liked", ServiceListKind.Liked, 2), ("albums", ServiceListKind.Albums, 1), ("playlist:Evening", ServiceListKind.Playlist, 2),
            ("playlist:Evening (2)", ServiceListKind.Playlist, 1)], export.Lists.Select(l => (l.Key, l.Kind, l.Items.Count)));
        Assert.Equal(new ImportSong("Glory Box", "Portishead", "Dummy", null, "GBUM70500141"), export.Lists[0].Items[0].Song);
        Assert.Equal("deezer:track:3135556", export.Lists[0].Items[0].Id);
        Assert.Equal((new ImportSong("Discovery", "Daft Punk", null, null), "deezer:album:302127"), (export.Lists[1].Items[0].Song, export.Lists[1].Items[0].Id));
        Assert.Equal(["Roads", "Glory Box"], export.Lists[2].Items.Select(i => i.Song.Title));
        // The account's sheet isn't read: nothing of it is in what's imported.
        Assert.DoesNotContain(export.Lists.SelectMany(l => l.Items), i => i.Song.Title.Contains("someone@example.com") || i.Song.Artist == "Paris");
    }

    [Theory]
    [InlineData("Glory Box", "Portishead - Topic", "Glory Box", "Portishead")]
    [InlineData("Daft Punk - One More Time (Official Video)", "Daft Punk", "One More Time", "Daft Punk")]
    [InlineData("Portishead - Roads [Official Music Video]", "PortisheadVEVO", "Roads", "Portishead")]
    [InlineData("Teardrop", "MassiveAttackVEVO", "Teardrop", "MassiveAttack")]
    public void A_videos_title_and_channel_say_what_song_it_is(string title, string channel, string song, string artist) =>
        Assert.Equal((song, artist), VideoLookup.Song(title, channel));

    [TestLibraryFact]
    public async Task A_spotify_export_makes_its_playlists_and_importing_it_again_adds_what_is_new()
    {
        await ScanAsync();
        var peek = await _api.PeekServiceImportAsync([("my_spotify_data.zip", new MemoryStream(SpotifyZip(more: false)))]);
        Assert.Equal(["liked", "albums", "playlist:Road trip"], peek.Lists.Select(l => l.Key));
        Assert.All(peek.Lists, l => Assert.Equal(l.Count, l.New));

        var batch = await _api.CreateServiceImportAsync([("my_spotify_data.zip", new MemoryStream(SpotifyZip(more: false)))],
            new CreateServiceImport(["liked", "albums", "playlist:Road trip"], "Spotify/", "Loved"));
        Assert.Equal(["Saved albums", "Spotify/Loved", "Spotify/Road trip"], batch.Imports.Select(i => i.Name).Order());
        var albums = await MatchedAsync(batch.Imports.Single(i => i.Albums).Id);
        Assert.Equal([ImportOutcome.Owned, ImportOutcome.Owned], albums.Lines.Select(l => l.Outcome));
        Assert.Equal(["Dummy", "Discovery"], albums.Lines.Select(l => l.Match!.Title));
        foreach (var import in batch.Imports)
            await MatchedAsync(import.Id);

        var applied = await _api.ApplyImportBatchAsync(batch.Id, new ApplyImportBatch(RequestMissing: false));
        Assert.Equal(3, applied.Imports);
        var playlists = await _api.ListPlaylistsAsync();
        var trip = playlists.Single(p => p.Name == "Spotify/Road trip");
        Assert.Equal(["Glory Box", "Da Funk", "Digital Love"], (await _api.GetPlaylistAsync(trip.Id))!.Songs.Select(s => s.Title[..Math.Min(s.Title.Length, 12)]));
        Assert.Contains(playlists, p => p.Name == "Spotify/Loved" && p.Count == 2);

        // Again, a song longer: only it's new, and it goes on the end of the same playlist.
        peek = await _api.PeekServiceImportAsync([("Playlist1.json", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Playlists(more: true))))]);
        var again = Assert.Single(peek.Lists);
        Assert.Equal((4, 1, trip.Id), (again.Count, again.New, again.UpdatesPlaylistId));
        var second = await _api.CreateServiceImportAsync([("my_spotify_data.zip", new MemoryStream(SpotifyZip(more: true)))],
            new CreateServiceImport(["liked", "albums", "playlist:Road trip"], "Spotify/", "Loved"));
        Assert.Equal(2, second.Unchanged);
        var update = Assert.Single(second.Imports);
        Assert.Equal((1, trip.Id), (update.Total, update.UpdatesPlaylistId));
        await MatchedAsync(update.Id);
        await _api.ApplyImportBatchAsync(second.Id, new ApplyImportBatch(RequestMissing: false));
        Assert.Equal("One More Time", (await _api.GetPlaylistAsync(trip.Id))!.Songs[^1].Title);
        Assert.Equal(playlists.Count, (await _api.ListPlaylistsAsync()).Count);
    }

    [TestLibraryFact]
    public async Task A_deezer_export_makes_its_playlists_and_importing_it_again_adds_what_is_new()
    {
        await ScanAsync();
        var file = () => ("deezer-data_1.xlsx", (Stream)new MemoryStream(DeezerXlsx(more: false, isrcs: false)));
        var peek = await _api.PeekServiceImportAsync([file()]);
        Assert.Equal(ImportFormat.Deezer, peek.Service);
        Assert.Equal(["liked", "albums", "playlist:Evening", "playlist:Evening (2)"], peek.Lists.Select(l => l.Key));

        var batch = await _api.CreateServiceImportAsync([file()], new CreateServiceImport(["liked", "playlist:Evening"], "Deezer/", "Favourites"));
        foreach (var import in batch.Imports)
            await MatchedAsync(import.Id);
        await _api.ApplyImportBatchAsync(batch.Id, new ApplyImportBatch(RequestMissing: false));
        var evening = (await _api.ListPlaylistsAsync()).Single(p => p.Name == "Deezer/Evening");
        Assert.Equal(["Roads", "Glory Box"], (await _api.GetPlaylistAsync(evening.Id))!.Songs.Select(s => s.Title));
        Assert.Contains(await _api.ListPlaylistsAsync(), p => p.Name == "Deezer/Favourites" && p.Count == 2);

        // A later copy, with a song more on the playlist: only it's new, and it goes on the end.
        var again = (await _api.PeekServiceImportAsync([("deezer-data_2.xlsx", new MemoryStream(DeezerXlsx(more: true, isrcs: false)))]))
            .Lists.Single(l => l.Key == "playlist:Evening");
        Assert.Equal((3, 1, evening.Id), (again.Count, again.New, again.UpdatesPlaylistId));
    }

    /// <summary>
    /// A spreadsheet laid out as Deezer's copy of your data is: a sheet for each kind of data, its first row the columns'
    /// names, its text mostly in shared strings. Two playlists share a name; the account's sheet has things that mustn't be read.
    /// </summary>
    private static byte[] DeezerXlsx(bool more, bool isrcs)
    {
        string? Isrc(string isrc) => isrcs ? isrc : null;
        var sheets = new (string Name, string?[][] Rows)[]
        {
            ("1_creationData", [["Email", "City"], ["someone@example.com", "Paris"]]),
            ("4_favoriteArtist", [["Artist Name", "Link"], ["Portishead", "www.deezer.com/artist/1"]]),
            ("5_favoriteAlbum", [["Album Title", "Artists", "Link"], ["Discovery", "Daft Punk", "www.deezer.com/album/302127"]]),
            ("8_favoriteSong", [["Song Title", "Artists", "Album Title", "ISRC", "Link"],
                ["Glory Box", "Portishead", "Dummy", Isrc("GBUM70500141"), "www.deezer.com/track/3135556"],
                ["Roads", "Portishead", "Dummy", Isrc("GBUM70500137"), "www.deezer.com/track/3135553"]]),
            ("11_playlistCreated", [["Playlist Title", "Playlist Link", "Status", "Song Title", "Artists", "Album Title", "ISRC", "Track Link"],
                ["Evening", "www.deezer.com/playlist/1", "Public", "Roads", "Portishead", "Dummy", Isrc("GBUM70500137"), "www.deezer.com/track/3135553"],
                ["Evening", "www.deezer.com/playlist/2", "Private", "Glory Box", "Portishead", "Dummy", null, "www.deezer.com/track/3135556"],
                ["Evening", "www.deezer.com/playlist/1", "Public", "Glory Box", "Portishead", "Dummy", Isrc("GBUM70500141"), "www.deezer.com/track/3135556"],
                .. more ? [new[] { "Evening", "www.deezer.com/playlist/1", "Public", "Digital Love", "Daft Punk", "Discovery", null, "www.deezer.com/track/3135"}] : Array.Empty<string?[]>()]),
        };
        var shared = new List<string>();
        string Cell(string column, string? text, int row)
        {
            if (text is null)
                return "";
            // Some as inline strings, as a spreadsheet may have.
            if (row % 2 == 0)
                return $"<c r=\"{column}{row}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c>";
            shared.Add(text);
            return $"<c r=\"{column}{row}\" t=\"s\"><v>{shared.Count - 1}</v></c>";
        }
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Write("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets>"
                + string.Concat(sheets.Select((sh, i) => $"<sheet name=\"{sh.Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
            Write("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"{rel}/worksheet\" Target=\"/xl/worksheets/sheet{i + 1}.xml\"/>"))
                + "</Relationships>");
            for (var i = 0; i < sheets.Length; i++)
                Write($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet xmlns=\"{main}\"><sheetData>"
                    + string.Concat(sheets[i].Rows.Select((row, r) => $"<row r=\"{r + 1}\">"
                        + string.Concat(row.Select((text, c) => Cell(((char)('A' + c)).ToString(), text, r + 1))) + "</row>"))
                    + "</sheetData></worksheet>");
            Write("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\">" + string.Concat(shared.Select(t => $"<si><t>{System.Security.SecurityElement.Escape(t)}</t></si>")) + "</sst>");
        }
        return bytes.ToArray();
    }

    // ---- Exporting ----

    [Fact]
    public void An_m3u_line_with_no_path_is_a_song_by_name()
    {
        const string m3u = "#EXTM3U\n#EXTINF:305,Portishead - Glory Box\n#EXTALB:Dummy\n#EXTINF:-1,Portishead - Hunter\n#EXTINF:240,Daft Punk - Digital Love\nDaft Punk/Discovery/03 Digital Love.flac\n";
        Assert.Equal(
            [new ImportSong("Glory Box", "Portishead", "Dummy", 305), new ImportSong("Hunter", "Portishead", null, null),
             new ImportSong("Digital Love", "Daft Punk", "Discovery", 240, Path: "Daft Punk/Discovery/03 Digital Love.flac")],
            ListParser.Read(m3u, ImportFormat.M3u, null, null));
    }

    [TestLibraryFact]
    public async Task Playlists_export_as_m3u8_or_csv_and_read_back_as_imports()
    {
        await ScanAsync();
        var import = await _api.CreateImportAsync(new CreateImport("Portishead - Glory Box\nPortishead - Hunter\n", Name: "Evening"));
        var detail = await MatchedAsync(import.Id);
        Assert.Equal([ImportOutcome.Owned, ImportOutcome.Found], detail.Lines.Select(l => l.Outcome));
        var playlist = (await _api.ApplyImportAsync(import.Id, new ApplyImport(Name: "Evening", RequestMissing: false))).PlaylistId!.Value;

        // Only what you have, by its path in the music folder, or under another machine's.
        var m3u = Text(await _api.ExportPlaylistsAsync([playlist]));
        var path = Assert.Single(m3u.Split('\n'), l => l.Length > 0 && !l.StartsWith('#'));
        Assert.Matches(@"^Portishead/.*Glory Box.*\.\w+$", path);
        Assert.Contains("#EXTINF:", m3u);
        Assert.Contains("Portishead - Glory Box", m3u);
        Assert.DoesNotContain("Hunter", m3u);
        Assert.Contains($"/mnt/music/{path}", Text(await _api.ExportPlaylistsAsync([playlist], prefix: "/mnt/music/")));

        // Songs not had kept by name, which reads back.
        var kept = ListParser.Read(Text(await _api.ExportPlaylistsAsync([playlist], keepMissing: true)), ImportFormat.M3u, null, null);
        Assert.Equal([("Glory Box", true), ("Hunter", false)], kept.Select(s => (s.Title, s.Path is not null)));

        // A CSV's columns are the ones the import knows.
        var csv = Text(await _api.ExportPlaylistsAsync([playlist], PlaylistExportFormat.Csv, keepMissing: true));
        var peek = ListParser.Peek(csv, "Evening.csv");
        Assert.Equal([ImportColumn.Artist, ImportColumn.Title, ImportColumn.Album, ImportColumn.Length, ImportColumn.Isrc, ImportColumn.MusicBrainzId], peek.Guess);
        Assert.Equal(["Glory Box", "Hunter"], peek.Sample.Select(s => s.Title));
        Assert.Equal("Dummy", peek.Sample[0].Album);
        Assert.NotNull(peek.Sample[0].DurationSeconds);

        // Several make a zip.
        var other = (await _api.CreatePlaylistAsync(new CreatePlaylist("Folder/Morning"))).Playlist.Id;
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await _api.ExportPlaylistsAsync([playlist, other])));
        Assert.Equal(["Evening.m3u8", "Folder/Morning.m3u8"], zip.Entries.Select(e => e.FullName).Order());
    }

    [TestLibraryFact]
    public async Task The_library_exports_as_a_csv_of_songs_or_albums()
    {
        await ScanAsync();
        var tracks = await _api.GetLibraryTracksAsync();
        var songs = Text(await _api.ExportLibraryAsync(LibraryExportRows.Songs)).TrimEnd().Split("\r\n");
        Assert.Equal("Artist,Album,Title,Year,Disc,Track number,Length,Path,Format,Quality,ISRC,MusicBrainz ID", songs[0]);
        Assert.Equal(tracks.Count, songs.Length - 1);

        var albums = Text(await _api.ExportLibraryAsync(LibraryExportRows.Albums, [LibraryExportColumn.Album, LibraryExportColumn.Owned, LibraryExportColumn.Title]))
            .TrimEnd().Split("\r\n");
        Assert.Equal("Album,Owned", albums[0]);
        Assert.Contains("Dummy,All", albums);
        Assert.Contains(albums, a => a.StartsWith("Portishead,") && a.Contains(" of "));
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private async Task<ImportDetail> MatchedAsync(Guid id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        ImportDetail detail;
        do
        {
            await Task.Delay(200);
            detail = (await _api.GetImportAsync(id))!;
        }
        while (detail.Import.State == ImportState.Matching && DateTime.UtcNow < deadline);
        Assert.Equal(ImportState.Ready, detail.Import.State);
        return detail;
    }

    private async Task ScanAsync()
    {
        // The test library is shared, so no lyrics are written into it.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Melodex.Core.Library.SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);
        await _api.WaitForCatalogueAsync();
    }
}
