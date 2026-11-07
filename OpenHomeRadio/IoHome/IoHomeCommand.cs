namespace OpenHomeRadio.IoHome;

/// <summary>
/// IoHome message codes (the command byte of an <see cref="IoHomeFrame"/>). This library sends
/// <see cref="Execute"/> and <see cref="KeyTransfer1W"/>; the other codes are listed so received frames are readable.
/// Movement is always <see cref="Execute"/> with a main parameter (see <see cref="IoHomeAction"/>); there are no
/// separate per-action command codes.
/// </summary>
public enum IoHomeCommand : byte
{
    /// <summary>Move or switch an actuator; parameters are originator, priority (ACEI), main parameter and two functional parameters.</summary>
    Execute = 0x00,

    /// <summary>Activate a stored mode.</summary>
    ActivateMode = 0x01,

    /// <summary>Discovery broadcast emitted while copying a remote, before the key is exchanged.</summary>
    Discover = 0x2E,

    /// <summary>Single-way key transfer: a controller hands its (obfuscated) system key to an actuator in learn mode.</summary>
    KeyTransfer1W = 0x30,

    /// <summary>Two-way: ask for a challenge.</summary>
    AskChallenge = 0x31,

    /// <summary>Two-way key transfer.</summary>
    KeyTransfer2W = 0x32,

    /// <summary>Two-way key transfer acknowledgement.</summary>
    KeyTransferAck = 0x33,

    /// <summary>Two-way: launch a key transfer.</summary>
    LaunchKeyTransfer = 0x38,

    /// <summary>Remove a single-way controller from an actuator.</summary>
    RemoveController1W = 0x39,

    /// <summary>Two-way challenge request.</summary>
    ChallengeRequest = 0x3C,

    /// <summary>Two-way challenge response.</summary>
    ChallengeResponse = 0x3D,
}

/// <summary>
/// Main parameter values of an <see cref="IoHomeCommand.Execute"/> command. Values between <see cref="Up"/> and
/// <see cref="Down"/> are positions (see <see cref="IoHomeClient.SendPositionAsync"/>).
/// </summary>
public enum IoHomeAction : ushort
{
    /// <summary>Up / open / on (position 0 %).</summary>
    Up = 0x0000,

    /// <summary>Down / closed / off (position 100 %).</summary>
    Down = 0xC800,

    /// <summary>Stop a moving actuator.</summary>
    Stop = 0xD200,
}
