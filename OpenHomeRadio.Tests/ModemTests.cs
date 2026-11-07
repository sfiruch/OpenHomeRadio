using OpenHomeRadio.Domino;
using OpenHomeRadio.IoHome;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class ModemTests
{
    private const double TxFrequencyErrorHz = 4_000;
    private const double DoubleDeviationHz = 38_400;
    private const double ModerateNoiseSigma = 100;
    private const double NominalDeviationHz = 19_200;
    private const double NominalHeavyNoiseSigma = 300; // of a 2000 peak amplitude
    private const double DoubleHeavyNoiseSigma = 400;
    private const int NoiseSeed = 7;
    private const int ExtraSmoothing = 16;
    private const int Copies = 3;
    private const int DominoBufferSamples = 1 << 17;
    private const int IoHomeBufferSamples = 1 << 18;

    private static readonly DominoSettings DominoDefaults = new();
    private static readonly IoHomeSettings IoHomeDefaults = new();

    private static readonly DominoFrame[] DominoPress =
    [
        new() { Command = DominoCommand.Up, Address = 0x123456, Counter = 0x10, Group = 0x20, Flag = 0x05 },
        new() { Command = DominoCommand.Up, Address = 0x123456, Counter = 0x10, Group = 0x20, Flag = 0x04 },
    ];

    private static IqBuffer ModulateDomino() => DominoModem.Modulate(DominoPress, DominoDefaults, TestSignals.SampleRateHz);

    private static IqBuffer ModulateIoHome(double deviationHz = NominalDeviationHz, double frequencyOffsetHz = 0) =>
        IoHomeModem.Modulate([IoHomeFrameTests.ExecuteDown()], new IoHomeSettings { DeviationHz = deviationHz }, TestSignals.SampleRateHz, frequencyOffsetHz);

    private static void AssertSingleIoHomeFrame(IReadOnlyList<IoHomeFrame> frames)
    {
        Assert.AreEqual(1, frames.Count);
        CollectionAssert.AreEqual(IoHomeFrameTests.ExecuteDown().ToBytes(), frames[0].ToBytes());
    }

    [TestMethod]
    public void Domino_RoundTrip_AtCarrier()
    {
        var signal = ModulateDomino();
        Assert.AreEqual(DominoDefaults.CarrierHz, signal.CenterFrequencyHz);
        CollectionAssert.AreEqual(DominoPress, DominoModem.Demodulate(signal, DominoDefaults).ToArray());
    }

    [TestMethod]
    public void Domino_RoundTrip_OffCentreWithFrequencyErrorAndNoise() =>
        CollectionAssert.AreEqual(DominoPress, DominoModem.Demodulate(
            TestSignals.Retune(ModulateDomino(), TestSignals.SharedCenterHz, TxFrequencyErrorHz, ModerateNoiseSigma, NoiseSeed), DominoDefaults).ToArray());

    [TestMethod]
    public void Domino_CarrierOutsideBuffer_FindsNothing() =>
        Assert.AreEqual(0, DominoModem.Demodulate(
            TestSignals.Compose(DominoDefaults.CarrierHz + TestSignals.SampleRateHz, DominoBufferSamples, [new Placement(ModulateDomino())]), DominoDefaults).Count);

    [TestMethod]
    public void Domino_Silence_FindsNothing() =>
        Assert.AreEqual(0, DominoModem.Demodulate(
            TestSignals.Compose(TestSignals.SharedCenterHz, DominoBufferSamples, [], ModerateNoiseSigma, NoiseSeed), DominoDefaults).Count);

    [TestMethod]
    public void IoHome_RoundTrip_AtCarrier()
    {
        var signal = ModulateIoHome();
        Assert.AreEqual(IoHomeDefaults.CarrierHz, signal.CenterFrequencyHz);
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, IoHomeDefaults));
    }

    [TestMethod]
    public void IoHome_RoundTrip_WithModulatorFrequencyOffset() =>
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(ModulateIoHome(frequencyOffsetHz: -TxFrequencyErrorHz), IoHomeDefaults));

    [TestMethod]
    public void IoHome_RoundTrip_OffCentreWithFrequencyErrorAndNoise() =>
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(
            TestSignals.Retune(ModulateIoHome(), TestSignals.SharedCenterHz, TxFrequencyErrorHz, ModerateNoiseSigma, NoiseSeed), IoHomeDefaults));

    [TestMethod]
    public void IoHome_RepeatedCopies_AreReportedOnce() =>
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(
            IoHomeModem.Modulate(Enumerable.Repeat(IoHomeFrameTests.ExecuteDown(), Copies), IoHomeDefaults, TestSignals.SampleRateHz), IoHomeDefaults));

    [TestMethod]
    public void IoHome_DoubleDeviation_CleanSignal_DecodesEvenWithoutSmoothing()
    {
        // Observed: a clean synthetic signal at twice the nominal deviation decodes without extra smoothing, so the
        // chatter seen on real remotes is not reproduced by deviation alone.
        var signal = TestSignals.Retune(ModulateIoHome(DoubleDeviationHz), TestSignals.SharedCenterHz);
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, new IoHomeSettings { DiscriminatorSmoothingSamples = [FskDsp.NoSmoothing] }));
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, IoHomeDefaults));
    }

    [TestMethod]
    [DataRow(NominalDeviationHz, NominalHeavyNoiseSigma)]
    [DataRow(DoubleDeviationHz, DoubleHeavyNoiseSigma)]
    public void IoHome_NoisySignal_ExtraSmoothingRecoversIt(double deviationHz, double noiseSigma)
    {
        // Observed: the extra smoothing rescues noise at any deviation; unsmoothed, the nominal deviation is actually
        // the more fragile one (it fails from sigma ~200, twice the deviation only from ~400).
        var signal = TestSignals.Retune(ModulateIoHome(deviationHz), TestSignals.SharedCenterHz, 0, noiseSigma, NoiseSeed);
        Assert.AreEqual(0, IoHomeModem.Demodulate(signal, new IoHomeSettings { DiscriminatorSmoothingSamples = [FskDsp.NoSmoothing] }).Count);
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, new IoHomeSettings { DiscriminatorSmoothingSamples = [ExtraSmoothing] }));
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, IoHomeDefaults));
    }

    [TestMethod]
    public void IoHome_Inverted_RoundTripsOnlyWithMatchingPolarity()
    {
        var inverted = new IoHomeSettings { Invert = true };
        var signal = IoHomeModem.Modulate([IoHomeFrameTests.ExecuteDown()], inverted, TestSignals.SampleRateHz);
        AssertSingleIoHomeFrame(IoHomeModem.Demodulate(signal, inverted));
        Assert.AreEqual(0, IoHomeModem.Demodulate(signal, IoHomeDefaults).Count);
    }

    [TestMethod]
    public void IoHome_Silence_FindsNothing() =>
        Assert.AreEqual(0, IoHomeModem.Demodulate(
            TestSignals.Compose(TestSignals.SharedCenterHz, IoHomeBufferSamples, [], ModerateNoiseSigma, NoiseSeed), IoHomeDefaults).Count);

    [TestMethod]
    public void IoHome_Diagnose_ReportsTheBurstAndSyncWord()
    {
        var report = IoHomeModem.Diagnose(TestSignals.Retune(ModulateIoHome(), TestSignals.SharedCenterHz), IoHomeDefaults);
        Assert.IsTrue(report[0].Contains("bursts=1", StringComparison.Ordinal), report[0]);
        Assert.IsTrue(report.Any(line => line.Contains("invert=False", StringComparison.Ordinal) && line.Contains("bytes=F600", StringComparison.Ordinal)), string.Join('\n', report));
    }
}
