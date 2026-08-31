# OpenDeck patches

Patches against OpenDeck 2.14.0 (`src-tauri`, apply with `patch -Np1`)
that this project's Stream Deck experience currently depends on, kept
here until they land upstream. (A swipe-to-dial-ticks patch used to
live here too; upstream declined it as inconsistent with Elgato's
behaviour, so it was dropped.)

- `lanczos-keys.patch`: pre-scales key images to the device's native
  resolution with Lanczos3. The elgato-streamdeck crate otherwise
  resizes with nearest-neighbour, which visibly aliases every plugin's
  keys. Upstream:
  [nekename/OpenDeck#439](https://github.com/nekename/OpenDeck/pull/439)
  (draft; the maintainer prefers the fix in the crate, see
  [OpenActionAPI/rust-elgato-streamdeck#62](https://github.com/OpenActionAPI/rust-elgato-streamdeck/pull/62)).
The Arch build that applies it lives outside this repo (an AUR
`opendeck` checkout with the patch added to `prepare()`); a stock
OpenDeck works fine with the plugin minus this refinement.
