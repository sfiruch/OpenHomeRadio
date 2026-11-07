using System.Numerics;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

/// <summary>One transmitted signal placed into a synthetic receive buffer.</summary>
/// <param name="Signal">A modulated signal (centred on its carrier).</param>
/// <param name="StartSample">Where the signal starts in the composed buffer.</param>
/// <param name="FrequencyErrorHz">An extra frequency error, as a transmitter with an inaccurate oscillator would add.</param>
internal readonly record struct Placement(IqBuffer Signal, int StartSample = 0, double FrequencyErrorHz = 0);

/// <summary>Builds synthetic receive buffers, as a wideband receiver tuned elsewhere would capture them.</summary>
internal static class TestSignals
{
    /// <summary>The shared receive tuning of <see cref="PlutoSdrOptions"/>.</summary>
    public const long SharedCenterHz = 868_750_000;

    /// <summary>The default sample rate of <see cref="PlutoSdrOptions"/>.</summary>
    public const long SampleRateHz = 2_500_000;

    /// <summary>
    /// Mixes signals into one buffer tuned to <paramref name="centerFrequencyHz"/>: each signal is shifted by the
    /// difference between its carrier and the tuning (plus its frequency error) and added at its start sample. Optional
    /// Gaussian noise comes from a seeded generator, so the result is deterministic.
    /// </summary>
    public static IqBuffer Compose(long centerFrequencyHz, int sampleCount, IEnumerable<Placement> placements, double noiseSigma = 0, int seed = 0)
    {
        var mixed = new Complex[sampleCount];
        foreach (var placement in placements)
        {
            var samples = placement.Signal.Samples.Span;
            var shiftHz = placement.Signal.CenterFrequencyHz - centerFrequencyHz + placement.FrequencyErrorHz;
            for (var n = 0; n < placement.Signal.SampleCount && placement.StartSample + n < sampleCount; n++)
                mixed[placement.StartSample + n] += new Complex(samples[2 * n], samples[2 * n + 1])
                    * Complex.FromPolarCoordinates(1, 2 * Math.PI * shiftHz * n / placement.Signal.SampleRateHz);
        }

        var random = new Random(seed);
        for (var n = 0; n < sampleCount && noiseSigma > 0; n++)
            mixed[n] += noiseSigma * new Complex(Gaussian(random), Gaussian(random));
        return new IqBuffer(FskDsp.ToInterleaved(mixed, 0, SampleRateHz), centerFrequencyHz, SampleRateHz);
    }

    /// <summary>Re-tunes one signal to <paramref name="centerFrequencyHz"/> with optional frequency error and noise.</summary>
    public static IqBuffer Retune(IqBuffer signal, long centerFrequencyHz, double frequencyErrorHz = 0, double noiseSigma = 0, int seed = 0) =>
        Compose(centerFrequencyHz, signal.SampleCount, [new Placement(signal, 0, frequencyErrorHz)], noiseSigma, seed);

    // Box-Muller transform.
    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
