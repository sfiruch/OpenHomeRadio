namespace OpenHomeRadio.Domino;

/// <summary>
/// Identifies one controllable Domino channel: the 24-bit button address an actuator has learned, and the per-remote
/// group byte that accompanies it. Read both from a received frame of the remote button you want to emulate.
/// </summary>
/// <param name="Address">24-bit button address (frame bytes 5–7). Every remote button has its own address.</param>
/// <param name="Group">Frame byte 9, constant per remote (for example 0x20 or 0x23); its exact meaning is unknown.</param>
public readonly record struct DominoDevice(uint Address, byte Group);
