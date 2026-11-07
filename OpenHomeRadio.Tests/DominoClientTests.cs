using Microsoft.Extensions.Time.Testing;
using OpenHomeRadio.Domino;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class DominoClientTests
{
    private const byte StepFlag = 0x05;
    private const byte DefaultFlag = 0x18;
    private const byte LockLevel = 3;
    private const uint OtherSystemId = 0x01020304;
    private static readonly DominoDevice Blind = new(0x123456, 0x20);
    private static readonly TimeSpan BeyondDeduplicationWindow = TimeSpan.FromSeconds(2);

    private static IReadOnlyList<DominoFrame> Decode(IqBuffer signal) => DominoModem.Demodulate(signal, new DominoSettings());

    [TestMethod]
    public async Task SendAsync_EachSendUsesAndIncrementsTheCounter()
    {
        var sdr = new FakeSdr();
        var client = new DominoClient(sdr) { Counter = 0xFE };

        await client.SendAsync(Blind, DominoCommand.ContinuousDown);
        await client.SendAsync(Blind, DominoCommand.Up);
        await client.SendAsync(Blind, DominoCommand.Toggle);

        Assert.AreEqual((byte)0x01, client.Counter, "the counter wraps at 255");
        Assert.AreEqual(3, sdr.Transmissions.Count);
        foreach (var (signal, options) in sdr.Transmissions)
        {
            Assert.AreEqual(client.Settings.CarrierHz, signal.CenterFrequencyHz);
            Assert.AreEqual(new TransmitOptions(client.Settings.TxAttenuationDb, client.Settings.TxBandwidthHz), options);
        }

        (DominoCommand Command, byte Counter, byte Flag)[] expected =
        [
            (DominoCommand.ContinuousDown, 0xFE, DefaultFlag), (DominoCommand.ContinuousDown, 0xFE, DefaultFlag - 1),
            (DominoCommand.Up, 0xFF, StepFlag), (DominoCommand.Up, 0xFF, StepFlag - 1),
            (DominoCommand.Toggle, 0x00, DefaultFlag), (DominoCommand.Toggle, 0x00, DefaultFlag - 1),
        ];
        var decoded = sdr.Transmissions.SelectMany(t => Decode(t.Signal)).ToList();
        CollectionAssert.AreEqual(expected, decoded.Select(f => (f.Command, f.Counter, f.Flag)).ToArray());
        Assert.IsTrue(decoded.All(f => f.Address == Blind.Address && f.Group == Blind.Group && f.SystemId == DominoSettings.DefaultSystemId && f.Value == 0));
    }

    [TestMethod]
    public async Task SendAsync_CarriesTheValueAndConfiguredSystemId()
    {
        var sdr = new FakeSdr();
        var client = new DominoClient(sdr, new DominoSettings { SystemId = OtherSystemId });
        await client.SendAsync(Blind, DominoCommand.LockLevelSet, LockLevel);

        var frames = Decode(sdr.Transmissions.Single().Signal);
        Assert.AreEqual(2, frames.Count);
        Assert.IsTrue(frames.All(f => f.Value == LockLevel && f.SystemId == OtherSystemId && f.Command == DominoCommand.LockLevelSet));
    }

    [TestMethod]
    public async Task ReceiveAsync_ReportsEachPressOnce()
    {
        var settings = new DominoSettings();
        IqBuffer Press(byte counter, uint systemId = DominoSettings.DefaultSystemId) => TestSignals.Retune(DominoModem.Modulate(
            new byte[] { DefaultFlag, DefaultFlag - 1, DefaultFlag - 1 }.Select(flag => new DominoFrame
            {
                SystemId = systemId, Command = DominoCommand.Stop, Address = Blind.Address, Group = Blind.Group, Counter = counter, Flag = flag,
            }), settings, TestSignals.SampleRateHz), TestSignals.SharedCenterHz);

        var first = Press(1);
        var time = new FakeTimeProvider();
        IEnumerable<IqBuffer> Script()
        {
            yield return first;   // reported
            yield return first;   // repeat within the window: collapsed
            time.Advance(BeyondDeduplicationWindow);
            yield return first;   // same counter after the window: reported again
            yield return Press(2);              // new counter: reported
            yield return Press(3, OtherSystemId); // other system: ignored
        }

        var presses = await new DominoClient(new FakeSdr(Script()), settings, time).ReceiveAsync().ToListAsync();

        CollectionAssert.AreEqual(new byte[] { 1, 1, 2 }, presses.Select(p => p.Counter).ToArray());
        Assert.IsTrue(presses.All(p => p.Command == DominoCommand.Stop && p.Address == Blind.Address));
    }
}
