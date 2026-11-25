using Melodex.Client;
using Melodex.Core.Library;
using Melodex.Core.Subsonic;

namespace Melodex.Core.Tests;

/// <summary>Songs' loudness as BS.1770 measures it, which ReplayGain is worked out from (#23).</summary>
public sealed class LoudnessTests
{
    // A 997 Hz tone, the frequency BS.1770 checks against, with the given peak, in every channel.
    private static float[] Tone(double peak, double seconds, int channels = 2) =>
        Enumerable.Range(0, (int)(seconds * LoudnessMeter.Rate))
            .SelectMany(i => Enumerable.Repeat((float)(peak * Math.Sin(2 * Math.PI * 997 * i / LoudnessMeter.Rate)), channels)).ToArray();

    private static Loudness Measure(int channels, params float[][] parts)
    {
        var counter = new LoudnessCounter(channels);
        foreach (var part in parts)
            counter.Add(part);
        return counter.Result();
    }

    [Fact]
    public void A_tone_is_as_loud_as_BS1770_says()
    {
        // At -20 dBFS in both channels, it's -20 LUFS; in one channel alone, 3 dB quieter.
        var stereo = Measure(2, Tone(0.1, 5));
        Assert.Equal(-20, stereo.Integrated!.Value, 0.1);
        Assert.Equal(0.1, stereo.Peak, 0.001);
        Assert.Equal(-23, Measure(1, Tone(0.1, 5, 1)).Integrated!.Value, 0.1);

        // Fed in pieces of whole frames, as the meter passes on ffmpeg's output.
        var tone = Tone(0.1, 5);
        Assert.Equal(stereo.Integrated.Value, Measure(2, tone[..12346], tone[12346..54322], tone[54322..]).Integrated!.Value, 9);
    }

    [Fact]
    public void Silence_and_quiet_passages_are_gated_out()
    {
        Assert.Null(Measure(2, new float[LoudnessMeter.Rate * 2 * 3]).Integrated);
        // Silence around it doesn't make it quieter, but for the few blocks it starts and ends in; nor does a passage 30 dB under it.
        Assert.Equal(-20, Measure(2, new float[LoudnessMeter.Rate * 2 * 3], Tone(0.1, 20), new float[LoudnessMeter.Rate * 2 * 3]).Integrated!.Value, 0.1);
        Assert.Equal(-20, Measure(2, Tone(0.1, 20), Tone(0.0032, 5)).Integrated!.Value, 0.1);
    }

    [Fact]
    public void An_albums_loudness_is_its_songs_together()
    {
        var quiet = Measure(2, Tone(0.1, 5));
        var loud = Measure(2, Tone(0.2, 5));
        Assert.Equal(-14, loud.Integrated!.Value, 0.1);
        // Not their loudnesses averaged, -17, but their sound: -20 and -14 LUFS together are about -16.
        Assert.Equal(-16, Loudness.Of(quiet.Blocks.Concat(loud.Blocks))!.Value, 0.1);
    }

    [Fact]
    public void Subsonic_players_are_given_a_songs_ReplayGain()
    {
        var track = new LibraryTrack(Guid.NewGuid(), Guid.NewGuid(), "Song", "Artist", null, null, null, null, null, 200, "a/song.flac", "flac", null);
        var song = new SubsonicSong(track, 1000, null, null, null, "flac", null, [], DateTime.UtcNow, null, new FileGains(2.5, 0.4, -1.25, 0.9));
        var gain = SubsonicLibrary.SongNode(song, null)["replayGain"]!;
        Assert.Equal(2.5, (double)gain["trackGain"]!);
        Assert.Equal(0.4, (double)gain["trackPeak"]!);
        Assert.Equal(-1.25, (double)gain["albumGain"]!);
        Assert.Equal(0.9, (double)gain["albumPeak"]!);

        Assert.Null(SubsonicLibrary.SongNode(song with { Gains = new FileGains(null, null, null, null) }, null)["replayGain"]);
    }
}
