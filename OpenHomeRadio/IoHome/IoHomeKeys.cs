using System.Security.Cryptography;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// System-key utilities: generate a key for a new controller, recover a key from a captured key transfer, and check
/// a key against a captured frame.
/// </summary>
/// <remarks>
/// Key recovery works because a key transfer obfuscates the system key only with the protocol's public transfer key
/// (see <see cref="IoHomeCrypto.TransferKey"/>). Use it only on installations you own or are authorised to control.
/// </remarks>
public static class IoHomeKeys
{
    /// <summary>
    /// Generates a new random 16-byte system key for a new controller. Store it safely: actuators paired with it accept
    /// only commands signed with it, and losing it means pairing again.
    /// </summary>
    public static byte[] Generate() => RandomNumberGenerator.GetBytes(IoHomeCrypto.KeySize);

    /// <summary>
    /// Recovers the system key from a captured single-way key transfer (<see cref="IoHomeCommand.KeyTransfer1W"/>),
    /// such as the one a remote broadcasts while its key is copied to another remote or while it is paired. Confirm the
    /// result with <see cref="Verify"/> against the same or another captured frame from that remote.
    /// </summary>
    /// <param name="keyTransfer">The captured key-transfer frame.</param>
    /// <exception cref="ArgumentException">The frame is not a single-way key transfer.</exception>
    public static byte[] RecoverFromKeyTransfer(IoHomeFrame keyTransfer)
    {
        ArgumentNullException.ThrowIfNull(keyTransfer);
        if (keyTransfer.Command != IoHomeCommand.KeyTransfer1W || keyTransfer.Data.Length != IoHomeCrypto.KeySize)
            throw new ArgumentException($"Not a single-way key transfer (command {keyTransfer.Command}, {keyTransfer.Data.Length} data bytes).", nameof(keyTransfer));
        return IoHomeCrypto.Encrypt1WKey(keyTransfer.Data, keyTransfer.Source);
    }

    /// <summary>
    /// Checks whether <paramref name="systemKey"/> is the key that signed <paramref name="frame"/>, by recomputing the
    /// frame's MAC. Only the right key reproduces it, so this proves a key without transmitting anything.
    /// </summary>
    /// <param name="systemKey">The 16-byte candidate key.</param>
    /// <param name="frame">A received (parsed) frame, which carries its MAC.</param>
    public static bool Verify(ReadOnlySpan<byte> systemKey, IoHomeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.Mac.Length == IoHomeCrypto.MacSize && frame.Sign(systemKey).Mac.AsSpan().SequenceEqual(frame.Mac);
    }
}
