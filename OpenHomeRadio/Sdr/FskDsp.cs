using System.Numerics;
using System.Text;

namespace OpenHomeRadio.Sdr;

/// <summary>
/// Protocol-independent 2-FSK signal processing shared by the modems: continuous-phase FSK modulation, an FM
/// discriminator, burst detection on the amplitude envelope, and run-length symbol slicing. A "symbol" here is one
/// transmitted FSK element ('1' = the higher tone, '0' = the lower); what the symbols mean (Manchester chips, UART
/// bits, ...) is left to the caller.
/// </summary>
internal static class FskDsp
{
    /// <summary>Discriminator output smoothing length that means "no extra smoothing".</summary>
    public const int NoSmoothing = 1;

    private const int ChannelFilterSamples = 8;
    private const double BurstThresholdFraction = 0.25;
    private const double MinBurstThresholdFraction = 1.0 / 64; // −36 dB: bounds the threshold when there is no noise at all
    private const int NoiseFloorSubsampling = 16;
    private const int NoiseFloorPercentile = 10;
    private const double NoiseFloorFactor = 8; // ≈ 3.7 σ of Rayleigh-distributed noise: rare false triggers
    private const double MaxGapSeconds = 0.0002;
    private const double MinBurstSeconds = 0.0005;
    private const int MinSymbolSamples = 3;
    private const double MinRunFraction = 0.5;
    private const int LowPercentile = 10;
    private const int HighPercentile = 90;
    private const int PercentScale = 100;
    private const int SliceEndMarginSymbols = 4;
    private const int SlicerRefinements = 3;

    /// <summary>Continuous-phase FSK: each '1' symbol transmits <paramref name="deviationHz"/> above the carrier, each '0' below.</summary>
    public static IEnumerable<Complex> Modulate(string symbols, double samplesPerSymbol, double deviationHz, long sampleRateHz, double amplitude)
    {
        var phase = 0.0;
        var total = (int)(symbols.Length * samplesPerSymbol);
        for (var i = 0; i < total; i++)
        {
            phase += 2 * Math.PI * (symbols[(int)(i / samplesPerSymbol)] == '1' ? deviationHz : -deviationHz) / sampleRateHz;
            yield return amplitude * Complex.FromPolarCoordinates(1, phase);
        }
    }

    /// <summary>Silence (zero IQ) for a lead-in, lead-out, or inter-frame gap.</summary>
    public static IEnumerable<Complex> Silence(int samples) => Enumerable.Repeat(Complex.Zero, Math.Max(0, samples));

    /// <summary>
    /// Converts complex samples to interleaved 16-bit I/Q, rotating the whole signal by <paramref name="frequencyOffsetHz"/>
    /// and saturating to the 16-bit range.
    /// </summary>
    public static short[] ToInterleaved(IReadOnlyList<Complex> samples, double frequencyOffsetHz, long sampleRateHz)
    {
        var iq = new short[samples.Count * 2];
        for (var n = 0; n < samples.Count; n++)
        {
            var rotated = samples[n] * Complex.FromPolarCoordinates(1, 2 * Math.PI * frequencyOffsetHz * n / sampleRateHz);
            iq[2 * n] = (short)Math.Clamp(Math.Round(rotated.Real), short.MinValue, short.MaxValue);
            iq[2 * n + 1] = (short)Math.Clamp(Math.Round(rotated.Imaginary), short.MinValue, short.MaxValue);
        }
        return iq;
    }

    /// <summary>
    /// Shifts the signal at <paramref name="offsetHz"/> down to baseband, low-passes it with a short moving average, and
    /// returns the instantaneous frequency (FM discriminator) and amplitude envelope per sample. The frequency is then
    /// optionally smoothed with a trailing moving average of <paramref name="frequencySmoothing"/> samples, which helps
    /// with signals whose deviation is much larger than nominal.
    /// </summary>
    public static (double[] Frequency, double[] Magnitude) Discriminate(ReadOnlySpan<short> interleavedIq, double offsetHz, long sampleRateHz, int frequencySmoothing)
    {
        var count = interleavedIq.Length / 2;
        var frequency = new double[count];
        var magnitude = new double[count];
        var history = new Complex[ChannelFilterSamples];
        var running = Complex.Zero;
        var previous = Complex.Zero;
        for (var n = 0; n < count; n++)
        {
            var shifted = new Complex(interleavedIq[2 * n], interleavedIq[2 * n + 1])
                * Complex.FromPolarCoordinates(1, -2 * Math.PI * offsetHz * n / sampleRateHz);
            running += shifted - history[n % ChannelFilterSamples];
            history[n % ChannelFilterSamples] = shifted;
            var filtered = running / ChannelFilterSamples;
            magnitude[n] = filtered.Magnitude;
            if (n > 0)
                frequency[n] = (filtered * Complex.Conjugate(previous)).Phase * sampleRateHz / (2 * Math.PI);
            previous = filtered;
        }

        if (frequencySmoothing <= NoSmoothing)
            return (frequency, magnitude);

        var smoothed = new double[count];
        var accumulator = 0.0;
        for (var n = 0; n < count; n++)
        {
            accumulator += frequency[n] - (n >= frequencySmoothing ? frequency[n - frequencySmoothing] : 0);
            smoothed[n] = accumulator / Math.Min(n + 1, frequencySmoothing);
        }
        return (smoothed, magnitude);
    }

    /// <summary>
    /// Groups samples whose envelope exceeds a threshold into bursts, bridging short gaps. The threshold is the lower of
    /// a fraction of the peak and a multiple of the noise floor, so a weak burst is still found when a much stronger
    /// one (for example another protocol's, on the shared receive stream) is in the same buffer.
    /// </summary>
    public static IEnumerable<(int Start, int End)> FindBursts(double[] magnitude, long sampleRateHz)
    {
        var peak = magnitude.Length == 0 ? 0 : magnitude.Max();
        if (peak <= 0)
            return [];

        // A low percentile of a subsample estimates the noise floor cheaply, as long as signal fills less than ~90% of the buffer.
        var subsample = magnitude.Where((_, n) => n % NoiseFloorSubsampling == 0).Order().ToArray();
        var threshold = Math.Clamp(subsample[subsample.Length * NoiseFloorPercentile / PercentScale] * NoiseFloorFactor,
            peak * MinBurstThresholdFraction, peak * BurstThresholdFraction);
        var maxGap = (int)(MaxGapSeconds * sampleRateHz);
        var minBurst = (int)(MinBurstSeconds * sampleRateHz);
        var groups = new List<(int Start, int End)>();
        for (var n = 0; n < magnitude.Length; n++)
        {
            if (magnitude[n] < threshold)
                continue;
            if (groups.Count > 0 && n - groups[^1].End < maxGap)
                groups[^1] = (groups[^1].Start, n);
            else
                groups.Add((n, n));
        }
        return groups.Where(g => g.End - g.Start > minBurst);
    }

    /// <summary>
    /// Turns the discriminated frequency over one burst into a string of '0'/'1' symbols, recovering the symbol
    /// period from the run lengths so the exact samples-per-symbol need not be known in advance. The decision threshold
    /// is the midpoint between the burst's low and high tones, so a constant frequency offset does not matter.
    /// </summary>
    public static string SliceSymbols(double[] frequency, int start, int end, double samplesPerSymbol)
    {
        var sorted = frequency[start..end].Order().ToArray();
        var center = (sorted[sorted.Length * LowPercentile / PercentScale] + sorted[sorted.Length * HighPercentile / PercentScale]) / 2;

        // The amplitude threshold clips the last few symbols, so slice a little past the burst end.
        var sliceEnd = Math.Min(frequency.Length, end + (int)(samplesPerSymbol * SliceEndMarginSymbols));
        var runs = new List<(bool High, int Length)>();
        for (var n = start; n < sliceEnd; n++)
        {
            var high = frequency[n] > center;
            if (runs.Count > 0 && runs[^1].High == high)
                runs[^1] = (high, runs[^1].Length + 1);
            else
                runs.Add((high, 1));
        }

        var shortest = runs.Select(r => r.Length).Where(l => l >= MinSymbolSamples).GroupBy(l => l).MaxBy(g => g.Count())?.Key ?? (int)samplesPerSymbol;
        runs = runs.Where(r => r.Length >= shortest * MinRunFraction).Aggregate(new List<(bool High, int Length)>(), (merged, r) =>
        {
            if (merged.Count > 0 && merged[^1].High == r.High)
                merged[^1] = (r.High, merged[^1].Length + r.Length);
            else
                merged.Add(r);
            return merged;
        });

        var recovered = (double)shortest;
        for (var iteration = 0; iteration < SlicerRefinements; iteration++)
            recovered = runs.Sum(r => r.Length) / runs.Sum(r => Math.Max(1, Math.Round(r.Length / recovered)));

        var symbols = new StringBuilder();
        foreach (var (high, length) in runs)
            symbols.Append(high ? '1' : '0', (int)Math.Max(1, Math.Round(length / recovered)));
        return symbols.ToString();
    }
}
