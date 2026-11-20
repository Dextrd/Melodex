using System.Text.Json.Nodes;
using Melodex.Client;
using Melodex.Core.Playlists;

namespace Melodex.Core.Subsonic;

/// <summary>
/// Your playlists, as players see them (#7): the same as in Melodex, folders and all in their names. A playlist lists
/// the songs in it that are in your library; those that aren't yet keep their places, out of sight.
/// </summary>
public static partial class SubsonicEndpoints
{
    /// <summary>A playlist's songs a player can play, each with the entry it is, in order.</summary>
    private static List<(PlaylistSong Entry, SubsonicSong Song)> Playable(SubsonicLibrary.View library, PlaylistDetail detail)
    {
        var byTrack = library.Songs.GroupBy(s => s.Track.TrackId).ToDictionary(g => g.Key, g => g.First());
        var playable = new List<(PlaylistSong, SubsonicSong)>();
        foreach (var entry in detail.Songs)
        {
            var song = entry.FileId is { } file && library.SongsById.TryGetValue(file, out var byFile) ? byFile
                : entry.TrackId is { } track && byTrack.TryGetValue(track, out var byTrackId) ? byTrackId : null;
            if (song is not null)
                playable.Add((entry, song));
        }
        return playable;
    }

    private static JsonObject PlaylistNode(SubsonicCall c, PlaylistSummary playlist, int? songs = null, int? seconds = null) => new()
    {
        ["id"] = playlist.Id.ToString(),
        ["name"] = playlist.Name,
        ["comment"] = playlist.Comment ?? "",
        ["owner"] = c.Username,
        ["public"] = false,
        ["songCount"] = songs ?? playlist.Count,
        ["duration"] = seconds ?? (int)Math.Round(playlist.DurationSeconds),
        ["created"] = SubsonicLibrary.Iso(SubsonicLibrary.CreatedOf(playlist.Id)),
        ["changed"] = SubsonicLibrary.Iso(playlist.UpdatedAt),
        ["coverArt"] = $"pl-{playlist.Id}",
    };

    private static async Task<JsonObject> PlaylistWithSongsAsync(SubsonicCall c, Guid id)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var detail = await c.Get<PlaylistService>().GetAsync(c.UserId, id, c.Ct) ?? throw NotFound("playlist");
        var playable = Playable(library, detail);
        var node = PlaylistNode(c, detail.Playlist, playable.Count, playable.Sum(p => (int)Math.Round(p.Song.Track.DurationSeconds ?? 0)));
        node["entry"] = Array(playable.Select(p => SongOf(library, p.Song)));
        return node;
    }

    private static async Task<object> GetPlaylistsAsync(SubsonicCall c)
    {
        if (c.P["username"] is { } username && !string.Equals(username, c.Username, StringComparison.OrdinalIgnoreCase))
            throw new SubsonicException(SubsonicErrors.NotAllowed, "Playlists are each person's own: you can only see yours.");
        var playlists = await c.Get<PlaylistService>().ListAsync(c.UserId, c.Ct);
        return SubsonicResponse.Ok().With("playlists", new JsonObject { ["playlist"] = Array(playlists.Select(p => PlaylistNode(c, p))) });
    }

    private static async Task<object> GetPlaylistAsync(SubsonicCall c) =>
        SubsonicResponse.Ok().With("playlist", await PlaylistWithSongsAsync(c, IdOf(c)));

    private static List<PlaylistItem> SongsAsked(SubsonicCall c, string parameter) =>
        c.P.All(parameter).Select(id => Guid.TryParse(id, out var guid) ? new PlaylistItem(FileId: guid) : throw NotFound("song")).ToList();

    /// <summary>A new playlist of the songs, or with <c>playlistId</c>, one of yours made just those songs.</summary>
    private static async Task<object> CreatePlaylistAsync(SubsonicCall c)
    {
        var playlists = c.Get<PlaylistService>();
        var songs = SongsAsked(c, "songId");
        Guid id;
        if (c.P["playlistId"] is not null)
        {
            id = IdOf(c, "playlistId");
            var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
            var detail = await playlists.GetAsync(c.UserId, id, c.Ct) ?? throw NotFound("playlist");
            // Those a player can't see stay; those it can are replaced by what it sent.
            var hidden = detail.Songs.Select(s => s.EntryId).Except(Playable(library, detail).Select(p => p.Entry.EntryId)).ToList();
            await playlists.ArrangeAsync(c.UserId, id, new ArrangePlaylist(hidden), c.Ct);
            if (songs.Count > 0)
                await playlists.AddAsync(c.UserId, id, new AddToPlaylist(songs, Duplicates: true), c.Ct);
            if (c.P["name"] is { } name)
                await playlists.UpdateAsync(c.UserId, id, new UpdatePlaylist(Name: name), c.Ct);
        }
        else
            id = (await playlists.CreateAsync(c.UserId, new CreatePlaylist(c.P["name"] ?? throw Missing("name"), Songs: songs), c.Ct)).Playlist.Id;
        return SubsonicResponse.Ok().With("playlist", await PlaylistWithSongsAsync(c, id));
    }

    private static async Task<object> UpdatePlaylistAsync(SubsonicCall c)
    {
        var playlists = c.Get<PlaylistService>();
        var id = IdOf(c, "playlistId");
        if (c.P["name"] is not null || c.P["comment"] is not null)
            _ = await playlists.UpdateAsync(c.UserId, id, new UpdatePlaylist(c.P["name"], c.P["comment"]), c.Ct) ?? throw NotFound("playlist");
        var remove = c.P.All("songIndexToRemove").Select(i => int.TryParse(i, out var n) ? n : -1).ToHashSet();
        if (remove.Count > 0)
        {
            var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
            var detail = await playlists.GetAsync(c.UserId, id, c.Ct) ?? throw NotFound("playlist");
            // The indexes are of the songs the player sees.
            var gone = Playable(library, detail).Where((_, i) => remove.Contains(i)).Select(p => p.Entry.EntryId).ToHashSet();
            await playlists.ArrangeAsync(c.UserId, id, new ArrangePlaylist(detail.Songs.Select(s => s.EntryId).Where(e => !gone.Contains(e)).ToList()), c.Ct);
        }
        if (SongsAsked(c, "songIdToAdd") is { Count: > 0 } add)
            _ = await playlists.AddAsync(c.UserId, id, new AddToPlaylist(add, Duplicates: true), c.Ct) ?? throw NotFound("playlist");
        return SubsonicResponse.Ok();
    }

    private static async Task<object> DeletePlaylistAsync(SubsonicCall c) =>
        await c.Get<PlaylistService>().DeleteAsync(c.UserId, IdOf(c), c.Ct) ? SubsonicResponse.Ok() : throw NotFound("playlist");
}
