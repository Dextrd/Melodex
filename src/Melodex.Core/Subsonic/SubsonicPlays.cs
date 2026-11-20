using System.Text.Json.Nodes;
using Melodex.Client;
using Melodex.Core.Listening;

namespace Melodex.Core.Subsonic;

/// <summary>Plays (#2): what players scrobble goes into the same listening history as the web player's.</summary>
public static partial class SubsonicEndpoints
{
    /// <summary>
    /// A play, or with <c>submission=false</c> a song started: each <c>id</c> with its <c>time</c>, in milliseconds, when
    /// players send several at once.
    /// </summary>
    private static async Task<object> ScrobbleAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var ids = c.P.All("id");
        if (ids.Count == 0)
            throw Missing("id");
        var times = c.P.All("time");
        var submission = c.P.Bool("submission") ?? true;
        var plays = c.Get<PlayService>();
        var client = c.P["c"] ?? "Subsonic player";
        for (var i = 0; i < ids.Count; i++)
        {
            if (!Guid.TryParse(ids[i], out var id) || !library.SongsById.TryGetValue(id, out var song))
                throw NotFound("song");
            DateTime? at = i < times.Count && long.TryParse(times[i], out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
            var body = new RecordPlay(song.Track.TrackId, song.Id, PlayedAt: at, Client: client);
            try
            {
                if (submission)
                    await plays.RecordAsync(c.UserId, body, c.Ct);
                else
                    await c.Get<Scrobbler>().StartedAsync(c.UserId, c.Username, await plays.ToPlayAsync(c.UserId, body, c.Ct), c.Ct);
            }
            catch (ArgumentException e)
            {
                throw new SubsonicException(SubsonicErrors.Generic, e.Message.Split(" (Parameter")[0]);
            }
        }
        return SubsonicResponse.Ok();
    }

    /// <summary>What everyone's playing now, in a player or the web app.</summary>
    private static async Task<object> GetNowPlayingAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var entries = new JsonArray();
        foreach (var playing in c.Get<Scrobbler>().Playing())
        {
            if (playing.Play.FileId is not { } file || !library.SongsById.TryGetValue(file, out var song))
                continue;
            var node = SongOf(library, song);
            node["username"] = playing.Username;
            node["minutesAgo"] = (int)(DateTime.UtcNow - playing.StartedAt).TotalMinutes;
            node["playerId"] = 0;
            node["playerName"] = playing.Play.Client;
            entries.Add(node);
        }
        return SubsonicResponse.Ok().With("nowPlaying", new JsonObject { ["entry"] = entries });
    }

    /// <summary>Albums for <c>getAlbumList</c>'s <c>frequent</c> and <c>recent</c>: most played first, or played latest first.</summary>
    private static async Task<IEnumerable<SubsonicAlbum>> PlayedAlbumsAsync(SubsonicCall c, SubsonicLibrary.View library, bool frequent)
    {
        var albums = (await c.Get<PlayService>().CountsAsync(c.UserId, c.Ct)).Albums;
        var ordered = frequent
            ? albums.OrderByDescending(a => a.Value.Count).ThenByDescending(a => a.Value.Last)
            : albums.OrderByDescending(a => a.Value.Last);
        return ordered.Select(a => library.AlbumsById.GetValueOrDefault(a.Key)).OfType<SubsonicAlbum>().ToList();
    }
}
