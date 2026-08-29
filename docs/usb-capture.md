# Capturing USB traffic for testers

This guide is for owners of the original Wave XLR (`0fd9:007d`) who want
to help map the rest of its protocol. No programming needed, just
Wireshark and about 15 minutes.

The prize is phantom power. On the MK.1 the +48V toggle only exists in
Wave Link, and the device has a front LED that lights when phantom is
actually on. That LED is what makes your capture so valuable: for every
toggle we get both the exact USB packet and hardware confirmation that
it worked. Once we see that packet, OpenXLR can send it too.

The same session can also unlock low cut, ClipGuard, and the mic/PC
crossfade, which the hardware has but whose registers we have never
seen.

## What you need

- A Windows PC with [Wave Link](https://www.elgato.com/downloads)
  installed and the Wave XLR working normally. Windows is the practical
  route; USB capture on macOS is a fight and not worth it here.
- [Wireshark](https://www.wireshark.org/download.html). During
  installation, tick the **USBPcap** component when the installer asks.
  It is off by default. Reboot after installing, the capture driver
  needs it.

## Before you start

- Plug the Wave XLR directly into the PC, not through a hub, ideally on
  a port away from your keyboard and mouse.
- A word on privacy: USBPcap records every device on the same USB
  controller, and that can include keystrokes from a keyboard. Keep the
  capture short, do not type passwords while it runs, and you are fine.
  The file you send should contain nothing but device chatter.

## The capture

1. Open Wireshark. You will see interfaces named `USBPcap1`,
   `USBPcap2`, and so on. Click the small gear next to each one: a
   window lists the devices attached to that controller. Pick the one
   showing the Elgato Wave XLR. If you cannot tell, capturing on all of
   them works too.
2. Double click the interface to start capturing. Packets will scroll
   by, that is normal.
3. Unplug the Wave XLR, wait 3 seconds, plug it back in. This puts the
   device's introduction handshake into the capture and lets us find it
   among the other traffic.
4. Wait for Wave Link to see the device again, then do nothing for 10
   seconds.
5. In Wave Link, turn phantom power **on**. Check the 48V LED lights.
   Wait 5 seconds.
6. Turn phantom power **off**. LED goes dark. Wait 5 seconds.
7. Repeat the on/off pair two more times, three rounds total. The
   repetition is what lets us tell the phantom packet apart from
   background noise.
8. In Wireshark press the red stop button, then File, Save As, and save
   as a `.pcapng` file.

## Optional round two, everything else

If you have five more minutes, start a second capture (replug the
device again, step 3 above) and work through this list, one action at a
time with 5 second pauses between them, in this order:

1. Gain: turn the dial to minimum, pause, then to maximum, pause, then
   back to a middle value you note down (the device shows it).
2. Mute on, pause, mute off.
3. Low cut on, pause, off.
4. ClipGuard on, pause, off.
5. Headphone volume: minimum, pause, maximum, pause, middle.
6. Mic/PC balance: sweep it fully one way, pause, fully back.

Jot down the order you actually did things in and roughly when. The
notes matter as much as the capture.

## Sending it in

Open an [issue](https://github.com/emaspa/openxlr/issues) titled "Wave
XLR MK.1 USB capture" and attach:

- the `.pcapng` file or files, zipped (GitHub does not accept raw
  pcapng)
- your Wave Link version and the firmware version it shows for the
  device
- your notes on what you toggled and when

## Checking your own capture (optional)

Curious whether you caught the right thing? In Wireshark, type
`usb.idProduct == 0x007d` into the filter bar and press enter. You
should see a packet from the replug; its "Device address" field, say 5,
identifies your Wave XLR. Now filter `usb.device_address == 5` and you
are looking at only the Wave XLR's traffic. Toggling phantom in Wave
Link should have produced a small burst of packets at each moment you
clicked. If it did, we can take it from there.
