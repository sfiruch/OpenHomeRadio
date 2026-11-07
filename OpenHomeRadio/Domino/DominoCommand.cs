namespace OpenHomeRadio.Domino;

/// <summary>
/// Command codes carried in byte 4 of a <see cref="DominoFrame"/>. Up, Down, ContinuousUp, ContinuousDown and Toggle
/// were confirmed with real remotes and receivers; the others follow the manufacturer's published command numbering.
/// </summary>
public enum DominoCommand : byte
{
    /// <summary>Short up: blinds raise a step / tilt the slats; may also mean "on".</summary>
    Up = 1,

    /// <summary>Short down: blinds lower a step / tilt the slats.</summary>
    Down = 2,

    /// <summary>Long up: blinds drive to the upper end position. A remote sends this ~0.4 s into a held up button.</summary>
    ContinuousUp = 3,

    /// <summary>Long down: blinds drive to the lower end position. A remote sends this ~0.4 s into a held down button.</summary>
    ContinuousDown = 4,

    /// <summary>Stop a moving blind.</summary>
    Stop = 5,

    /// <summary>Toggle a switching actuator (lights, sockets).</summary>
    Toggle = 6,

    /// <summary>Store the current position as the favourite position.</summary>
    Save = 15,

    /// <summary>Raise the lock level (the level is carried in <see cref="DominoFrame.Value"/>).</summary>
    LockLevelSet = 20,

    /// <summary>Lower the lock level.</summary>
    LockLevelClear = 21,

    /// <summary>Drive to the stored favourite position.</summary>
    RestorePosition = 23,

    /// <summary>Sent by a remote when a switch button is held down (observed; exact semantics unknown).</summary>
    LongPress = 28,
}
