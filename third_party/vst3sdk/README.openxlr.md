# Steinberg VST 3 SDK subset

OpenXLR vendors only the host-side source files required to discover and host
VST3 effects. They come from the official `steinbergmedia/vst3sdk` repository:

- SDK commit: `3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96`
- `base`: `fcf9da0bd27a16f7f03773a3a39822f28f5c8477`
- `pluginterfaces`: `4f547e8e102b47de4a8b8aaf343c73b700786372`
- `public.sdk`: `586dc5e6c8012c3e4b01c79389375cbe96bdb1da`

The upstream SDK is MIT licensed as of this pinned revision. `LICENSE.txt` is
preserved beside the source. Samples, plugin implementations, wrappers,
documentation, VSTGUI, and unrelated platform code are intentionally omitted.
The subset is built into OpenXLR's isolated scanner/host helpers; it is never
loaded into the managed daemon process.

VST is a trademark of Steinberg Media Technologies GmbH, registered in Europe
and other countries. OpenXLR uses the term only to describe compatibility.
