using System.Text.Json.Nodes;
using Melodex.Client;
using Melodex.Core.Listening;

namespace Melodex.Core.Subsonic;

/// <summary>Stars, ratings and the play queue (#7): each person's own, the same ones the web app shows.</summary>
public static partial class SubsonicEndpoints
{
    /// <summary>The marks a song, album or artist is kept under: a song goes by its track, so a new file of it keeps them.</summary>
    private static (MarkKind Kind, Guid Id)? MarkKeyOf(SubsonicLibrary.View library, Guid id) =>
        library.SongsById.TryGetValue(id, out var song) ? (MarkKind.Song, song.Track.TrackId)
        : library.AlbumsById.ContainsKey(id) ? (MarkKind.Album, id)
        : library.ArtistsById.ContainsKey(id) ? (MarkKind.Artist, id)
        : null;

    private static async Task<Dictionary<(MarkKind, Guid), ItemMark>> MarksAsync(SubsonicCall c) =>
        (await c.Get<MarkService>().ListAsync(c.UserId, c.Ct)).ToDictionary(m => (m.Kind, m.Id));

    /// <summary>
    /// Puts the caller's stars and ratings on every song, album and artist in an answer, wherever it is, so no call can
    /// leave them out.
    /// </summary>
    private static async Task MarkAsync(SubsonicCall c, SubsonicResponse response)
    {
        if (response.Failed)
            return;
        var marks = await MarksAsync(c);
        if (marks.Count == 0)
            return;
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);

        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray list:
                    foreach (var item in list)
                        Visit(item);
                    break;
                case JsonObject item:
                    if (item["id"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && Guid.TryParse((string?)item["id"], out var id)
                        && (item.ContainsKey("isDir") || item.ContainsKey("songCount") || item.ContainsKey("albumCount"))
                        && MarkKeyOf(library, id) is { } key && marks.TryGetValue(key, out var mark))
                    {
                        if (mark.StarredAt is { } starred)
                            item["starred"] = SubsonicLibrary.Iso(starred);
                        if (mark.Rating is { } rating)
                            item["userRating"] = rating;
                    }
                    foreach (var (_, value) in item.ToList())
                        Visit(value);
                    break;
            }
        }
        Visit(response.Body);
    }

    // ---- Stars and ratings ----

    /// <summary>The songs, albums and artists named, by <c>id</c> (any of them), <c>albumId</c> and <c>artistId</c>.</summary>
    private static async Task<List<(MarkKind Kind, Guid Id)>> NamedAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var named = new List<(MarkKind, Guid)>();
        foreach (var (parameter, kind) in new[] { ("id", (MarkKind?)null), ("albumId", MarkKind.Album), ("artistId", MarkKind.Artist) })
            foreach (var value in c.P.All(parameter))
            {
                if (!Guid.TryParse(value, out var id) || MarkKeyOf(library, id) is not { } key || kind is { } expected && key.Kind != expected)
                    throw NotFound(kind switch { MarkKind.Album => "album", MarkKind.Artist => "artist", _ => "song, album or artist" });
                named.Add(key);
            }
        return named.Count > 0 ? named : throw Missing("id, albumId or artistId");
    }

    private static async Task<object> StarAsync(SubsonicCall c, bool starred)
    {
        var marks = c.Get<MarkService>();
        foreach (var (kind, id) in await NamedAsync(c))
            await marks.SetAsync(c.UserId, kind, id, new SetMark(Starred: starred), c.Ct);
        return SubsonicResponse.Ok();
    }

    private static async Task<object> SetRatingAsync(SubsonicCall c)
    {
        var rating = c.P.Int("rating") ?? throw Missing("rating");
        if (rating is < 0 or > 5)
            throw new SubsonicException(SubsonicErrors.Generic, "A rating is 1 to 5, or 0 to take it away.");
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var (kind, id) = MarkKeyOf(library, IdOf(c)) ?? throw NotFound("song, album or artist");
        await c.Get<MarkService>().SetAsync(c.UserId, kind, id, new SetMark(Rating: rating), c.Ct);
        return SubsonicResponse.Ok();
    }

    private static async Task<object> GetStarredAsync(SubsonicCall c, string name)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var starred = (await c.Get<MarkService>().ListAsync(c.UserId, c.Ct)).Where(m => m.StarredAt is not null).ToList();
        var songsByTrack = library.Songs.GroupBy(s => s.Track.TrackId).ToDictionary(g => g.Key, g => g.First());
        var legacy = name == "starred";
        return SubsonicResponse.Ok().With(name, new JsonObject
        {
            ["artist"] = Array(starred.Where(m => m.Kind == MarkKind.Artist)
                .Select(m => library.ArtistsById.GetValueOrDefault(m.Id)).OfType<SubsonicArtist>().Select(SubsonicLibrary.ArtistNode)),
            ["album"] = Array(starred.Where(m => m.Kind == MarkKind.Album)
                .Select(m => library.AlbumsById.GetValueOrDefault(m.Id)).OfType<SubsonicAlbum>().Select(a => SubsonicLibrary.AlbumNode(a, asDirectory: legacy))),
            ["song"] = Array(starred.Where(m => m.Kind == MarkKind.Song)
                .Select(m => songsByTrack.GetValueOrDefault(m.Id)).OfType<SubsonicSong>().Select(s => SongOf(library, s))),
        });
    }

    /// <summary>Albums for <c>getAlbumList</c>'s <c>starred</c> and <c>highest</c>: starred latest first, or best rated first.</summary>
    private static async Task<IEnumerable<SubsonicAlbum>> MarkedAlbumsAsync(SubsonicCall c, SubsonicLibrary.View library, bool starred)
    {
        var marks = (await c.Get<MarkService>().ListAsync(c.UserId, c.Ct)).Where(m => m.Kind == MarkKind.Album);
        marks = starred
            ? marks.Where(m => m.StarredAt is not null).OrderByDescending(m => m.StarredAt)
            : marks.Where(m => m.Rating is not null).OrderByDescending(m => m.Rating).ThenByDescending(m => m.StarredAt is not null);
        return marks.Select(m => library.AlbumsById.GetValueOrDefault(m.Id)).OfType<SubsonicAlbum>().ToList();
    }

    // ---- The play queue ----

    /// <summary>The songs of a saved queue that players can play: those in the library. Songs fetched only to listen to are left out.</summary>
    private static List<(int Place, SubsonicSong Song)> Playable(SubsonicLibrary.View library, PlayQueue queue)
    {
        var songsByTrack = library.Songs.GroupBy(s => s.Track.TrackId).ToDictionary(g => g.Key, g => g.First());
        var playable = new List<(int, SubsonicSong)>();
        for (var i = 0; i < queue.Items.Count; i++)
        {
            var item = queue.Items[i];
            if (item.Missing)
                continue;
            var song = item.FileId is { } file && library.SongsById.TryGetValue(file, out var byFile) ? byFile
                : songsByTrack.GetValueOrDefault(item.TrackId);
            if (song is not null)
                playable.Add((i, song));
        }
        return playable;
    }

    private static async Task<object> GetPlayQueueAsync(SubsonicCall c, bool byIndex)
    {
        var name = byIndex ? "playQueueByIndex" : "playQueue";
        var queue = await c.Get<MarkService>().GetQueueAsync(c.UserId, c.Ct);
        if (queue is null)
            return SubsonicResponse.Ok().With(name, new JsonObject());
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var songs = Playable(library, queue);
        // What's playing, or if a player can't play it, the next it can.
        var current = songs.FindIndex(s => s.Place >= queue.Current);
        var node = new JsonObject
        {
            ["position"] = current >= 0 && songs[current].Place == queue.Current ? (long)Math.Round(queue.Position * 1000) : 0L,
            ["username"] = c.Username,
            ["changed"] = SubsonicLibrary.Iso(queue.ChangedAt),
            ["changedBy"] = queue.ChangedBy,
            ["entry"] = Array(songs.Select(s => SongOf(library, s.Song))),
        };
        if (current >= 0)
        {
            if (byIndex)
                node["currentIndex"] = current;
            else
                node["current"] = songs[current].Song.Id.ToString();
        }
        return SubsonicResponse.Ok().With(name, node);
    }

    private static async Task<object> SavePlayQueueAsync(SubsonicCall c, bool byIndex)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var songs = c.P.All("id").Select(id => Guid.TryParse(id, out var guid) && library.SongsById.TryGetValue(guid, out var song) ? song : throw NotFound("song"))
            .ToList();
        var current = -1;
        if (byIndex)
            current = c.P.Int("currentIndex") is { } index ? index >= 0 && index < songs.Count ? index : throw NotFound("place in the queue") : 0;
        else if (c.P["current"] is { } playing)
            current = Guid.TryParse(playing, out var id) ? songs.FindIndex(s => s.Id == id) : -1;
        if (songs.Count > 0 && current < 0)
            current = 0;
        var items = songs.Select(s => new PlayQueueItem(
            s.Track.TrackId, s.Track.Title, s.Track.ArtistCredit, s.Track.Local ? s.Track.FileId : null, null, s.Track.ArtistId, s.Track.AlbumId,
            s.Track.AlbumTitle, s.Track.CoverUrl, s.Track.DurationSeconds)).ToList();
        var position = (c.P.Long("position") ?? 0) / 1000.0;
        try
        {
            await c.Get<MarkService>().SaveQueueAsync(c.UserId, new SavePlayQueue(items, current, position, null, c.P["c"] ?? "Subsonic player"), c.Ct);
        }
        catch (ArgumentException e)
        {
            throw new SubsonicException(SubsonicErrors.Generic, e.Message.Split(" (Parameter")[0]);
        }
        return SubsonicResponse.Ok();
    }
}
