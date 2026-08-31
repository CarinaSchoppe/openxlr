#!/bin/sh
# Remove the Wave XLR Pro UCM profile (run as root); restores the
# stock topology after a pipewire + wireplumber restart.
set -eu
U=/usr/share/alsa/ucm2/USB-Audio
rm -f "$U/conf.d/0fd9-00b4.conf" \
      "$U/Elgato/Wave-XLR-Pro.conf" \
      "$U/Elgato/Wave-XLR-Pro-HiFi.conf"
rmdir "$U/conf.d" "$U/Elgato" 2>/dev/null || true
echo "removed; restart pipewire + wireplumber (user) to apply"
