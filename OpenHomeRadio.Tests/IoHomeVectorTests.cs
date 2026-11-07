using OpenHomeRadio.IoHome;

namespace OpenHomeRadio.Tests;

/// <summary>The worked examples from the published protocol documentation (public test vectors).</summary>
[TestClass]
public sealed class IoHomeVectorTests
{
    private const string CrcSampleHex = "F80000003F1A380B000161000080D8050002A624222E8BA351";
    private const ushort CrcSampleValue = 0x525F;
    private const string DocumentationKeyHex = "01020304050607080910111213141516";
    private const uint DocumentationNode = 0xABCDEF;
    private const string EncryptedKeyHex = "7E60491F976ADF653DB0ED785E49A201";
    private const ushort PairingSequence = 0x1234;
    private const byte PairingManufacturer = 0x02;
    private const byte PairingTag = 0x01;
    private const byte PairingControlByte0 = 0xFC; // key transfers use the length rule that excludes the MAC
    private const string PairingMacHex = "19E81EC43D5E";
    private const string PairingFrameHex = "FC0000003FABCDEF307E60491F976ADF653DB0ED785E49A2010201123419E81EC43D5E9BF2";

    private static readonly byte[] DocumentationKey = Convert.FromHexString(DocumentationKeyHex);

    [TestMethod]
    public void Crc16Kermit_MatchesDocumentedSample() =>
        Assert.AreEqual(CrcSampleValue, IoHomeCrypto.Crc16Kermit(Convert.FromHexString(CrcSampleHex)));

    [TestMethod]
    public void Crc16Kermit_OverFrameWithLowByteFirstCrc_IsZero()
    {
        // The frame carries the CRC low byte first: 5F 52.
        Assert.AreEqual((ushort)0, IoHomeCrypto.Crc16Kermit(Convert.FromHexString(CrcSampleHex + "5F52")));
        Assert.AreNotEqual((ushort)0, IoHomeCrypto.Crc16Kermit(Convert.FromHexString(CrcSampleHex + "525F")));
    }

    [TestMethod]
    public void ConstructIv1W_MatchesDocumentedVector() =>
        Assert.AreEqual("000143D2000000550500059955555555",
            Convert.ToHexString(IoHomeCrypto.ConstructIv1W(Convert.FromHexString("000143D2000000"), [0x05, 0x99])));

    [TestMethod]
    public void DocumentedIvPayload_IsAnExecuteStop() =>
        // command Execute, originator 0x01, ACEI 0x43, main parameter Stop, two functional parameters
        Assert.AreEqual("000143D2000000", Convert.ToHexString(
            [(byte)IoHomeCommand.Execute, 0x01, 0x43, (byte)((ushort)IoHomeAction.Stop >> 8), unchecked((byte)IoHomeAction.Stop), 0x00, 0x00]));

    [TestMethod]
    public void Encrypt1WKey_MatchesDocumentedVector() =>
        Assert.AreEqual(EncryptedKeyHex, Convert.ToHexString(IoHomeCrypto.Encrypt1WKey(DocumentationKey, DocumentationNode)));

    [TestMethod]
    public void Encrypt1WKey_IsItsOwnInverse() =>
        Assert.AreEqual(DocumentationKeyHex, Convert.ToHexString(IoHomeCrypto.Encrypt1WKey(Convert.FromHexString(EncryptedKeyHex), DocumentationNode)));

    [TestMethod]
    public void Create1WMac_MatchesDocumentedVector() =>
        // The key-transfer MAC covers the command and the encrypted key, not the trailing manufacturer/tag bytes.
        Assert.AreEqual(PairingMacHex, Convert.ToHexString(IoHomeCrypto.Create1WMac(
            Convert.FromHexString("30" + EncryptedKeyHex), [(byte)(PairingSequence >> 8), unchecked((byte)PairingSequence)], DocumentationKey)));

    [TestMethod]
    public void PairingFrame_IsReproducedByteForByte()
    {
        var frame = new IoHomeFrame
        {
            Command = IoHomeCommand.KeyTransfer1W,
            Destination = IoHomeFrame.BroadcastAddress,
            Source = DocumentationNode,
            Sequence = PairingSequence,
            Data = IoHomeCrypto.Encrypt1WKey(DocumentationKey, DocumentationNode),
            Trailer = [PairingManufacturer, PairingTag],
        }.Sign(DocumentationKey);

        Assert.IsNull(frame.ControlByte0, "control byte 0 is computed, not forced");
        Assert.AreEqual(PairingMacHex, Convert.ToHexString(frame.Mac));
        var bytes = frame.ToBytes();
        Assert.AreEqual(PairingControlByte0, bytes[0]);
        Assert.AreEqual(PairingFrameHex, Convert.ToHexString(bytes));
    }

    [TestMethod]
    public void PairingFrame_ParsesAndYieldsTheDocumentedKey()
    {
        Assert.IsTrue(IoHomeFrame.TryParse(Convert.FromHexString(PairingFrameHex), out var frame));
        Assert.AreEqual(IoHomeCommand.KeyTransfer1W, frame.Command);
        Assert.AreEqual(IoHomeFrame.BroadcastAddress, frame.Destination);
        Assert.AreEqual(DocumentationNode, frame.Source);
        Assert.AreEqual(PairingSequence, frame.Sequence);
        Assert.AreEqual(EncryptedKeyHex, Convert.ToHexString(frame.Data));
        Assert.AreEqual("0201", Convert.ToHexString(frame.Trailer));
        Assert.AreEqual(PairingMacHex, Convert.ToHexString(frame.Mac));
        Assert.AreEqual(PairingFrameHex, Convert.ToHexString(frame.ToBytes()), "re-serialising a parsed frame reproduces it");

        Assert.AreEqual(DocumentationKeyHex, Convert.ToHexString(IoHomeKeys.RecoverFromKeyTransfer(frame)));
        Assert.IsTrue(IoHomeKeys.Verify(DocumentationKey, frame));
    }
}
