using System.Numerics;
using System.Text;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Domino;

/// <summary>
/// Converts between <see cref="DominoFrame"/>s and baseband I/Q. On air each frame is a preamble of alternating chips,
/// fixed sync chips, then the 14 frame bytes Manchester coded (bit 1 = chips "01", bit 0 = chips "10"), all as 2-FSK.
/// </summary>
public static class DominoModem
{
    private const int BitsPerByte = 8;
    private const int FrameBits = DominoFrame.Length * BitsPerByte;
    private const int ChipsPerBit = 2;
    private const string PreamblePair = "10";
    private const string OneChips = "01";
    private const string ZeroChips = "10";
    private const int CodeViolation = -1;
    private const int NoViolation = -1;

    /// <summary>
    /// Builds the transmit signal for a sequence of frames (each followed by the inter-frame gap), centred on the
    /// carrier: the returned buffer's <see cref="IqBuffer.CenterFrequencyHz"/> is <see cref="DominoSettings.CarrierHz"/>.
    /// </summary>
    /// <param name="frames">The frames to send, in order.</param>
    /// <param name="settings">Radio and framing parameters.</param>
    /// <param name="sampleRateHz">The transmitter's sample rate (see <see cref="ISdrTransceiver.SampleRateHz"/>).</param>
    public static IqBuffer Modulate(IEnumerable<DominoFrame> frames, DominoSettings settings, long sampleRateHz)
    {
        var samples = new List<Complex>(FskDsp.Silence(settings.LeadTrailSamples));
        var gap = FskDsp.Silence((int)(settings.InterFrameGap.TotalSeconds * sampleRateHz)).ToArray();
        foreach (var frame in frames)
        {
            var chips = new StringBuilder();
            chips.Insert(0, PreamblePair, settings.PreamblePairs).Append(settings.SyncChips);
            foreach (var b in frame.ToBytes())
                for (var bit = BitsPerByte - 1; bit >= 0; bit--)
                    chips.Append(((b >> bit) & 1) == 1 ? OneChips : ZeroChips);
            samples.AddRange(FskDsp.Modulate(chips.ToString(), sampleRateHz / settings.ChipRateHz, settings.DeviationHz, sampleRateHz, settings.TxAmplitude));
            samples.AddRange(gap);
        }
        samples.AddRange(FskDsp.Silence(settings.LeadTrailSamples));
        return new IqBuffer(FskDsp.ToInterleaved(samples, 0, sampleRateHz), settings.CarrierHz, sampleRateHz);
    }

    /// <summary>
    /// Finds every distinct CRC-valid frame in a buffer. The buffer may be tuned anywhere as long as the carrier lies
    /// within its bandwidth: the offset is computed from <see cref="IqBuffer.CenterFrequencyHz"/>.
    /// </summary>
    /// <param name="buffer">Received (or synthesised) samples.</param>
    /// <param name="settings">Radio parameters; only the carrier, deviation and chip rate are used.</param>
    /// <returns>The frames in order of appearance, without duplicates. Frames of any system identifier are returned.</returns>
    public static IReadOnlyList<DominoFrame> Demodulate(IqBuffer buffer, DominoSettings settings)
    {
        double offsetHz = settings.CarrierHz - buffer.CenterFrequencyHz;
        if (Math.Abs(offsetHz) + settings.DeviationHz > buffer.SampleRateHz / 2.0)
            return [];

        var (frequency, magnitude) = FskDsp.Discriminate(buffer.Samples.Span, offsetHz, buffer.SampleRateHz, FskDsp.NoSmoothing);
        var frames = new List<DominoFrame>();
        var seen = new HashSet<DominoFrame>();
        foreach (var (start, end) in FskDsp.FindBursts(magnitude, buffer.SampleRateHz))
        {
            var chips = FskDsp.SliceSymbols(frequency, start, end, buffer.SampleRateHz / settings.ChipRateHz);
            for (var phase = 0; phase < ChipsPerBit; phase++)
                foreach (var frame in ScanFrames(DecodeManchester(chips, phase)))
                    if (seen.Add(frame))
                        frames.Add(frame);
        }
        return frames;
    }

    private static int[] DecodeManchester(string chips, int phase)
    {
        var bits = new int[(chips.Length - phase) / ChipsPerBit];
        for (var i = 0; i < bits.Length; i++)
            bits[i] = chips.AsSpan(phase + ChipsPerBit * i, ChipsPerBit) switch
            {
                OneChips => 1,
                ZeroChips => 0,
                _ => CodeViolation, // wrong phase or a glitch
            };
        return bits;
    }

    private static IEnumerable<DominoFrame> ScanFrames(int[] bits)
    {
        for (var start = 0; start + FrameBits <= bits.Length; start++)
        {
            var bytes = new byte[DominoFrame.Length];
            var violation = NoViolation;
            for (var i = 0; i < FrameBits && violation == NoViolation; i++)
            {
                if (bits[start + i] == CodeViolation)
                    violation = i;
                else
                    bytes[i / BitsPerByte] = (byte)((bytes[i / BitsPerByte] << 1) | bits[start + i]);
            }
            if (violation != NoViolation)
                start += violation; // no frame can start before the violation
            else if (DominoFrame.TryParse(bytes, out var frame))
                yield return frame;
        }
    }
}
