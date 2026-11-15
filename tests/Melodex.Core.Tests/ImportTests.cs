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
            .UseSetting("Melodex:DataPath", _dataPath)
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
    }
}
