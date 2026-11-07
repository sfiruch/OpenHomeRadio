# Domino protocol

Namespace `OpenHomeRadio.Domino`. Compatible with Dominoswiss-compatible blind, light and socket actuators and their
hand-held remotes. The description below comes from observing such devices off-air; it is not an official
specification. All example values are synthetic.

## Physical layer

| Property | Value |
|---|---|
| Frequency | 869.000 MHz |
| Modulation | 2-FSK, deviation ±48 kHz (occupied width ≈ 200 kHz) |
| Chip rate | 100 kchip/s |
| Line coding | Manchester: chips `01` = bit 1, `10` = bit 0 (50 kbit/s) |
| Preamble | 32 × chips `10`, then the sync chips `110011001` |
| Byte order | Frame bytes MSB first |

`DominoSettings` holds all of these; the defaults match real devices.

## Frame

14 bytes: 12 payload bytes and a CRC.

| Bytes | Field | Notes |
|---|---|---|
| 0–3 | System ID | `C3 0F 22 77` on every remote observed so far. Possibly installation-specific: configurable as `DominoSettings.SystemId`. |
| 4 | Command | See below |
| 5–7 | Button address | 24-bit, little-endian. Each remote button has its own address (up/down share one). |
| 8 | Counter | De-duplication token (see below) |
| 9 | Group | Constant per remote (e.g. `0x20`); meaning unknown |
| 10 | Flag | Depends on the command; each press sends a few frames with value *N*, then frames with *N*−1 |
| 11 | Value | Command argument (e.g. a lock level), otherwise 0 |
| 12–13 | CRC | CRC-16/CCITT-FALSE over bytes 0–11 (poly `0x1021`, init `0xFFFF`, no reflection, no final XOR), big-endian |

Example (synthetic): `ContinuousDown` from button `0x123456`, group `0x20`, counter `0x01`:

```
C3 0F 22 77 04 56 34 12 01 20 18 00 | D7 C0   flag 0x18, sent 3×
C3 0F 22 77 04 56 34 12 01 20 17 00 | C7 FE   flag 0x17, sent 5×
```

There is no encryption and no authentication beyond the CRC.

## Commands

| Code | `DominoCommand` | Meaning |
|---|---|---|
| 1 | `Up` | Short up (blind: step / tilt slats) |
| 2 | `Down` | Short down |
| 3 | `ContinuousUp` | Long up (blind: travel to the top) |
| 4 | `ContinuousDown` | Long down |
| 5 | `Stop` | Stop a moving blind |
| 6 | `Toggle` | Toggle a switch (light, socket) |
| 15 | `Save` | Store the current position |
| 20 / 21 | `LockLevelSet` / `LockLevelClear` | Raise / lower the lock level (argument in Value) |
| 23 | `RestorePosition` | Drive to the stored position |
| 28 | `LongPress` | Sent by a remote when a switch button is held |

Remotes send flag `0x05`/`0x04` with `Up`/`Down` and `0x18`/`0x17` with the other commands; `DominoClient` does the same.
A held up/down button sends the short command at once and the continuous one about 0.4 s later; nothing is sent on
release.

## Counter semantics

An actuator acts on a frame only if its counter differs from the last counter it saw for that address, in either
direction. The repeated frames of one press share a counter, so a press acts once. There is no rolling code: any
different value works, and no pairing is required to send. Actuators learn several button addresses (for example one
per remote), and any of them controls the device.

`DominoClient` increments its `Counter` on every send. Persist it so the first command after a restart does not
repeat the previous value.

## Open questions

- The meaning of the Group and Flag bytes.
- Whether the system ID is a manufacturer constant or an installation ID.
- Whether actuators ever transmit (acknowledge or report state).
