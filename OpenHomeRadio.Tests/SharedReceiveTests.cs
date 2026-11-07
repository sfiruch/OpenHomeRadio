using OpenHomeRadio.Domino;
using OpenHomeRadio.IoHome;

namespace OpenHomeRadio.Tests;

/// <summary>One receive buffer at the shared wideband tuning serves both protocols.</summary>
[TestClass]
public sealed class SharedReceiveTests
{
    private const int BufferSamples = 1 << 18; // PlutoSdrOptions.ReceiveBufferSamples
    private const int DominoStart = 10_000;
    private const int IoHomeStart = 120_000;
    private const double NoiseSigma = 50;
    private const int NoiseSeed = 42;
    private const double FullAmplitude = 2000;
    private const double QuarterAmplitude = FullAmplitude / 4;
    private const double EighthAmplitude = FullAmplitude / 8;

    [TestMethod]
    public void OneBuffer_BothDemodulatorsFindTheirFrameOnly()
    {
        var dominoSettings = new DominoSettings();
        var ioHomeSettings = new IoHomeSettings();
        DominoFrame[] dominoFrames = [new() { Command = DominoCommand.ContinuousDown, Address = 0x123456, Counter = 0x33, Group = 0x20, Flag = 0x18 }];
        var dominoSignal = DominoModem.Modulate(dominoFrames, dominoSettings, TestSignals.SampleRateHz);
        var ioHomeSignal = IoHomeModem.Modulate([IoHomeFrameTests.ExecuteDown()], ioHomeSettings, TestSignals.SampleRateHz);
        Assert.IsTrue(DominoStart + dominoSignal.SampleCount < IoHomeStart, "the bursts must not overlap");
        Assert.IsTrue(IoHomeStart + ioHomeSignal.SampleCount < BufferSamples);

        var shared = TestSignals.Compose(TestSignals.SharedCenterHz, BufferSamples,
            [new Placement(dominoSignal, DominoStart), new Placement(ioHomeSignal, IoHomeStart)], NoiseSigma, NoiseSeed);

        CollectionAssert.AreEqual(dominoFrames, DominoModem.Demodulate(shared, dominoSettings).ToArray());
        var ioHomeFrames = IoHomeModem.Demodulate(shared, ioHomeSettings);
        Assert.AreEqual(1, ioHomeFrames.Count);
        CollectionAssert.AreEqual(IoHomeFrameTests.ExecuteDown().ToBytes(), ioHomeFrames[0].ToBytes());
    }

    [TestMethod]
    [DataRow(FullAmplitude, QuarterAmplitude)]
    [DataRow(QuarterAmplitude, FullAmplitude)]
    [DataRow(FullAmplitude, EighthAmplitude)]
    [DataRow(EighthAmplitude, FullAmplitude)]
    public void OneBuffer_UnequalLevels_BothStillDecode(double dominoAmplitude, double ioHomeAmplitude)
    {
        // Burst detection must not threshold only on the whole buffer's peak, or a much weaker burst next to a strong one
        // of the other protocol is lost (with a peak-only threshold, 1/8 of the other's amplitude was already lost).
        var dominoSettings = new DominoSettings { TxAmplitude = dominoAmplitude };
        var ioHomeSettings = new IoHomeSettings { TxAmplitude = ioHomeAmplitude };
        DominoFrame[] dominoFrames = [new() { Command = DominoCommand.Stop, Address = 0x123456, Counter = 0x34, Group = 0x20, Flag = 0x18 }];
        var shared = TestSignals.Compose(TestSignals.SharedCenterHz, BufferSamples,
            [
                new Placement(DominoModem.Modulate(dominoFrames, dominoSettings, TestSignals.SampleRateHz), DominoStart),
                new Placement(IoHomeModem.Modulate([IoHomeFrameTests.ExecuteDown()], ioHomeSettings, TestSignals.SampleRateHz), IoHomeStart),
            ], NoiseSigma, NoiseSeed);

        CollectionAssert.AreEqual(dominoFrames, DominoModem.Demodulate(shared, dominoSettings).ToArray());
        Assert.AreEqual(1, IoHomeModem.Demodulate(shared, ioHomeSettings).Count);
    }

    [TestMethod]
    public void EachDemodulator_IgnoresTheOtherProtocolAlone()
    {
        var dominoSettings = new DominoSettings();
        var ioHomeSettings = new IoHomeSettings();
        var dominoOnly = TestSignals.Retune(DominoModem.Modulate(
            [new DominoFrame { Command = DominoCommand.Toggle, Address = 0x123456, Group = 0x20, Flag = 0x18 }], dominoSettings, TestSignals.SampleRateHz),
            TestSignals.SharedCenterHz);
        var ioHomeOnly = TestSignals.Retune(IoHomeModem.Modulate([IoHomeFrameTests.ExecuteDown()], ioHomeSettings, TestSignals.SampleRateHz),
            TestSignals.SharedCenterHz);

        Assert.AreEqual(1, DominoModem.Demodulate(dominoOnly, dominoSettings).Count);
        Assert.AreEqual(0, IoHomeModem.Demodulate(dominoOnly, ioHomeSettings).Count);
        Assert.AreEqual(1, IoHomeModem.Demodulate(ioHomeOnly, ioHomeSettings).Count);
        Assert.AreEqual(0, DominoModem.Demodulate(ioHomeOnly, dominoSettings).Count);
    }
}
