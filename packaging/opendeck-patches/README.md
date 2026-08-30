# OpenDeck patches

Patches against OpenDeck 2.14.0 (`src-tauri`, apply with `patch -Np1`)
that this project's Stream Deck experience currently depends on, kept
here until they land upstream.

- `lanczos-keys.patch`: pre-scales key images to the device's native
  resolution with Lanczos3. The elgato-streamdeck crate otherwise
  resizes with nearest-neighbour, which visibly aliases every plugin's
  keys. Upstream:
  [nekename/OpenDeck#439](https://github.com/nekename/OpenDeck/pull/439)
  (draft; the maintainer prefers the fix in the crate, see
  [OpenActionAPI/rust-elgato-streamdeck#62](https://github.com/OpenActionAPI/rust-elgato-streamdeck/pull/62)).
- `swipe-dial.patch`: turns touch strip swipes into dial-rotate ticks
  for the dial the swipe started over (horizontal delta, 12 px per
  tick). Stock OpenDeck discards swipe events entirely; with this, any
  plugin's dial value can be dragged from the touchscreen. Upstream:
  [nekename/OpenDeck#441](https://github.com/nekename/OpenDeck/pull/441)
  (draft).

The Arch build that applies them lives outside this repo (an AUR
`opendeck` checkout with these patches added to `prepare()`); a stock
OpenDeck works fine with the plugin minus these two refinements.
