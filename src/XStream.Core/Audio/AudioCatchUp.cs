namespace XStream.Core.Audio;

/// <summary>How the audio player removes lag that has built up, without clicks or dropouts.</summary>
public static class AudioCatchUp
{
    /// <summary>Lag tolerated above the target before catching up (one Opus frame's worth of jitter).</summary>
    public const double SlackMs = 15;

    /// <summary>How much faster a frame plays while catching up: 4% (well under a semitone, hard to hear).</summary>
    public const double Speedup = 0.04;

    /// <summary>
    /// True if a frame is near-silent (below about -50 dBFS of the source), so skipping it entirely can't be heard.
    /// <paramref name="gain"/> is what the samples were scaled by; muted audio counts as quiet.
    /// </summary>
    public static bool IsQuiet(ReadOnlySpan<float> samples, float gain)
    {
        if (gain <= 0) return true;
        double sum = 0;
        foreach (var x in samples) sum += x * x;
        double rms = Math.Sqrt(sum / Math.Max(1, samples.Length)) / (gain * 32768);
        return rms < 0.003;
    }

    /// <summary>
    /// Shorten an interleaved frame of <paramref name="n"/> samples per channel by <see cref="Speedup"/>, in place
    /// (linear resampling: a smooth, slightly faster playback rather than a cut). Returns the new sample count.
    /// </summary>
    public static int Shorten(float[] interleaved, int n, int channels)
    {
        int m = n - (int)Math.Round(n * Speedup);
        if (m < 2 || m >= n) return n;
        double step = (double)(n - 1) / (m - 1);
        for (int j = 0; j < m; j++) // forward in place is safe: the source position j*step is never behind j
        {
            double pos = j * step;
            int i = (int)pos;
            double f = pos - i;
            int i2 = Math.Min(i + 1, n - 1);
            for (int c = 0; c < channels; c++)
                interleaved[j * channels + c] = (float)(interleaved[i * channels + c] * (1 - f) + interleaved[i2 * channels + c] * f);
        }
        return m;
    }
}
