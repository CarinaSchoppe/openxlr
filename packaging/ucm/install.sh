#!/bin/sh
# Install the Wave XLR Pro UCM profile system-wide (run as root).
# Purely additive: only creates new files, edits nothing shipped by
# alsa-ucm-conf. revert.sh removes them again.
set -eu
SRC="$(dirname "$(realpath "$0")")"
U=/usr/share/alsa/ucm2/USB-Audio
install -Dm644 "$SRC/0fd9-00b4.conf" "$U/conf.d/0fd9-00b4.conf"
install -Dm644 "$SRC/Elgato/Wave-XLR-Pro.conf" "$U/Elgato/Wave-XLR-Pro.conf"
install -Dm644 "$SRC/Elgato/Wave-XLR-Pro-HiFi.conf" "$U/Elgato/Wave-XLR-Pro-HiFi.conf"
echo "installed; restart pipewire + wireplumber (user) to apply"
