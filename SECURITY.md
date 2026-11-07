# Security

## Reporting a problem

Please report security problems in this library privately through GitHub's
[private vulnerability reporting](https://github.com/sfiruch/OpenHomeRadio/security/advisories/new) rather than in a
public issue.

## Known protocol properties

Some weaknesses are properties of the radio protocols themselves, not of this library, and are documented openly:

- **Domino** frames carry no authentication beyond a CRC. Anyone in radio range can send commands to these devices.
- **IoHome** single-way key transfers protect the system key only with a published constant, so a captured key
  transfer reveals the key (see [docs/IoHome.md](docs/IoHome.md#why-key-recovery-works)).

Treat system keys like passwords: keep them out of source control and logs. Use the key-recovery functions only on
installations you own or are authorised to control.
