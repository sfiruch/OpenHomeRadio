using OpenHomeRadio.IoHome;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class IoHomeKeysTests
{
    private const ushort Sequence = 0x0100;
    private static readonly byte[] WrongKey = Convert.FromHexString("0F0E0D0C0B0A09080706050403020100");

    // A synthetic captured key transfer: serialised and parsed again, as a receiver would see it.
    private static IoHomeFrame CapturedKeyTransfer()
    {
        var bytes = new IoHomeFrame
        {
            Command = IoHomeCommand.KeyTransfer1W,
            Destination = IoHomeFrame.BroadcastAddress,
            Source = IoHomeFrameTests.SyntheticAddress,
            Sequence = Sequence,
            Data = IoHomeCrypto.Encrypt1WKey(IoHomeFrameTests.SyntheticKey, IoHomeFrameTests.SyntheticAddress),
            Trailer = [IoHomeRemote.DefaultManufacturer, 0x01],
        }.Sign(IoHomeFrameTests.SyntheticKey).ToBytes();
        Assert.IsTrue(IoHomeFrame.TryParse(bytes, out var frame));
        return frame;
    }

    [TestMethod]
    public void RecoverFromKeyTransfer_ReturnsTheSystemKey() =>
        CollectionAssert.AreEqual(IoHomeFrameTests.SyntheticKey, IoHomeKeys.RecoverFromKeyTransfer(CapturedKeyTransfer()));

    [TestMethod]
    public void RecoverFromKeyTransfer_RejectsOtherCommands() =>
        Assert.ThrowsExactly<ArgumentException>(() => IoHomeKeys.RecoverFromKeyTransfer(IoHomeFrameTests.ExecuteDown()));

    [TestMethod]
    public void Verify_AcceptsTheRightKeyOnly()
    {
        var keyTransfer = CapturedKeyTransfer();
        Assert.IsTrue(IoHomeKeys.Verify(IoHomeFrameTests.SyntheticKey, keyTransfer));
        Assert.IsFalse(IoHomeKeys.Verify(WrongKey, keyTransfer));

        Assert.IsTrue(IoHomeFrame.TryParse(IoHomeFrameTests.ExecuteDown().ToBytes(), out var execute));
        Assert.IsTrue(IoHomeKeys.Verify(IoHomeFrameTests.SyntheticKey, execute));
        Assert.IsFalse(IoHomeKeys.Verify(WrongKey, execute));
    }

    [TestMethod]
    public void Verify_UnsignedFrame_IsFalse() =>
        Assert.IsFalse(IoHomeKeys.Verify(IoHomeFrameTests.SyntheticKey, new IoHomeFrame { Command = IoHomeCommand.Execute }));

    [TestMethod]
    public void Generate_ReturnsDistinctKeysOfKeySize()
    {
        var first = IoHomeKeys.Generate();
        Assert.AreEqual(IoHomeCrypto.KeySize, first.Length);
        CollectionAssert.AreNotEqual(first, IoHomeKeys.Generate());
    }
}
