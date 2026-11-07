# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0]

### Added
- `OpenHomeRadio.Sdr`: `ISdrTransceiver` and a thread-safe `PlutoSdr` (ADALM-PLUTO over iiod/TCP) with serialised
  transmission and one shared wideband receive stream for any number of subscribers.
- `OpenHomeRadio.Domino`: frames, modem and `DominoClient` (send commands, receive button presses).
- `OpenHomeRadio.IoHome`: single-way frames, crypto, modem and `IoHomeClient` (send, positions, pairing, receive),
  transmit frequency sweep, and `IoHomeKeys` (generate, recover from a key transfer, verify).
- `openhomeradio` command-line tool.
