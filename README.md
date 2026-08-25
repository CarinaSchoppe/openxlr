# OpenXLR

Open-source Linux control suite for USB audio interfaces, starting with the
Elgato Wave XLR Pro. Replaces the Windows-only Wave Link + Stream Deck stack.

- `src/` is the .NET 10 solution: OpenXLR.Core (device protocol, PipeWire
  submixer), OpenXLR.Daemon (WebSocket control API), OpenXLR.UI (Avalonia),
  OpenXLR.Probe (hardware/mixer test tool). See `src/README.md`.
- `wave-xlr-pro-protocol.md` is the reverse-engineered vendor protocol of the
  Wave XLR Pro (0fd9:00b4), hardware-verified.
- `docs/wave-xlr-linux-audio-stack-research.md` is the full research log of the
  migration, including open items.
- `probe/proprobe.py` is the standalone Python probe for the vendor protocol.
- `capture-analysis/` holds the extracted USB control transfers from the Wave
  Link capture and the retained diff of the openwave-fork prototype.
- `windows-export/` is the backup of the original Windows Wave Link and Stream
  Deck configuration this setup replicates.
