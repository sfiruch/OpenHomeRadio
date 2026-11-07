using System.Security.Cryptography;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// The IoHome security primitives: CRC-16/KERMIT, the initial-value construction with its two-accumulator checksum,
/// the 6-byte truncated AES-128 message authentication code, and the system-key obfuscation used by key transfers.
/// This is plain protocol arithmetic with no secrets of its own; the only secret is the installation's system key.
/// </summary>
public static class IoHomeCrypto
{
    /// <summary>Length of a system key (AES-128) in bytes.</summary>
    public const int KeySize = 16;

    /// <summary>Length of a frame's message authentication code in bytes.</summary>
    public const int MacSize = 6;

    private const int BlockSize = 16;
    private const int NodeIdSize = 3;
    private const int IvPayloadBytes = 8;
    private const int IvChecksumIndex = 8;
    private const int IvSequenceIndex = 10;
    private const int ChallengeIndex = 10;
    private const byte IvPadding = 0x55;
    private const byte ChecksumHighBit = 0x80;
    private const byte ChecksumLowBits = 0x7F;
    private const byte ChecksumMix1 = 0x55;
    private const byte ChecksumMix2 = 0x5B;
    private const ushort CrcPolynomial = 0x8408; // CRC-16/KERMIT: the reflected form of 0x1021
    private const int BitsPerByte = 8;

    /// <summary>
    /// The protocol's published, fixed "transfer key". It obfuscates a system key while the key is handed to a device;
    /// it is a constant of the protocol, not a secret, which is why a captured key transfer reveals the system key.
    /// </summary>
    public static ReadOnlySpan<byte> TransferKey =>
        [0x34, 0xC3, 0x46, 0x6E, 0xD8, 0x8F, 0x4E, 0x8E, 0x16, 0xAA, 0x47, 0x39, 0x49, 0x88, 0x43, 0x73];

    /// <summary>
    /// CRC-16/KERMIT (polynomial 0x1021 reflected, initial value 0, no final XOR) over <paramref name="data"/>. Frames
    /// carry it low byte first, so the CRC over a whole frame including its CRC is 0.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    public static ushort Crc16Kermit(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < BitsPerByte; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ CrcPolynomial : crc >> 1);
        }
        return crc;
    }

    /// <summary>
    /// Builds the 16-byte initial value of a single-way frame's MAC: the first 8 payload bytes (0x55-padded), two
    /// checksum bytes over the whole payload, the two sequence bytes, then 0x55 padding.
    /// </summary>
    /// <param name="payload">The command byte followed by its parameters.</param>
    /// <param name="sequence">The two rolling-code bytes as they appear in the frame.</param>
    public static byte[] ConstructIv1W(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> sequence)
    {
        var iv = new byte[BlockSize];
        iv.AsSpan().Fill(IvPadding);
        payload[..Math.Min(payload.Length, IvPayloadBytes)].CopyTo(iv);

        byte checksum1 = 0, checksum2 = 0;
        foreach (var b in payload)
        {
            var mixed = (byte)(b ^ checksum2);
            var shifted = (byte)(((checksum1 & ChecksumLowBits) << 1) | (mixed >= ChecksumHighBit ? 1 : 0));
            var highBitSet = (checksum1 & ChecksumHighBit) != 0;
            checksum1 = highBitSet ? (byte)(shifted ^ ChecksumMix1) : shifted;
            checksum2 = highBitSet ? (byte)((mixed << 1) ^ ChecksumMix2) : (byte)(mixed << 1);
        }
        iv[IvChecksumIndex] = checksum1;
        iv[IvChecksumIndex + 1] = checksum2;
        sequence[..2].CopyTo(iv.AsSpan(IvSequenceIndex));
        return iv;
    }

    /// <summary>The 6-byte single-way MAC: the AES-128 encryption of <see cref="ConstructIv1W"/>'s result, truncated.</summary>
    /// <param name="payload">The command byte followed by its parameters.</param>
    /// <param name="sequence">The two rolling-code bytes as they appear in the frame.</param>
    /// <param name="systemKey">The 16-byte system key.</param>
    public static byte[] Create1WMac(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> sequence, ReadOnlySpan<byte> systemKey) =>
        EncryptBlock(ConstructIv1W(payload, sequence), systemKey)[..MacSize];

    /// <summary>
    /// Obfuscates a system key for a single-way key transfer (<see cref="IoHomeCommand.KeyTransfer1W"/>): XOR with the
    /// AES encryption, under <see cref="TransferKey"/>, of the controller's 3-byte address repeated to 16 bytes. XOR is
    /// its own inverse, so applying this to a captured encrypted key with the same address recovers the system key.
    /// </summary>
    /// <param name="key">The 16-byte system key (or, to recover, the encrypted key).</param>
    /// <param name="nodeAddress">The 24-bit address of the controller that sends the key.</param>
    public static byte[] Encrypt1WKey(ReadOnlySpan<byte> key, uint nodeAddress)
    {
        var iv = new byte[BlockSize];
        for (var i = 0; i < BlockSize; i++)
            iv[i] = (byte)(nodeAddress >> (BitsPerByte * (NodeIdSize - 1 - i % NodeIdSize)));
        return XorWithMask(key, iv);
    }

    /// <summary>
    /// Obfuscates (or, applied again, recovers) a system key for a two-way key transfer
    /// (<see cref="IoHomeCommand.KeyTransfer2W"/>): XOR with the AES encryption, under <see cref="TransferKey"/>, of
    /// 0x55 padding carrying the exchange's 6-byte challenge in bytes 10–15.
    /// </summary>
    /// <param name="key">The 16-byte system key (or, to recover, the encrypted key).</param>
    /// <param name="challenge">The 6-byte challenge of the exchange.</param>
    public static byte[] Encrypt2WKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> challenge)
    {
        var iv = new byte[BlockSize];
        iv.AsSpan().Fill(IvPadding);
        challenge[..MacSize].CopyTo(iv.AsSpan(ChallengeIndex));
        return XorWithMask(key, iv);
    }

    private static byte[] XorWithMask(ReadOnlySpan<byte> value, ReadOnlySpan<byte> iv)
    {
        if (value.Length != KeySize)
            throw new ArgumentException($"A key is {KeySize} bytes long.", nameof(value));
        var result = EncryptBlock(iv, TransferKey);
        for (var i = 0; i < KeySize; i++)
            result[i] ^= value[i];
        return result;
    }

    private static byte[] EncryptBlock(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"A key is {KeySize} bytes long.", nameof(key));
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptEcb(block, PaddingMode.None);
    }
}
