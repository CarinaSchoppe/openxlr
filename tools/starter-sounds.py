#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Build, or with --check compare, the soundboard's starter sounds.

The starter sounds are eighteen short clips the daemon adds to a soundboard
once, on a page of their own. They live in packaging/starter-sounds: the
library file soundboard.json, which lists them as a soundboard archive
does, their Ogg Vorbis files under clips/, and CREDITS, which names each
clip's source, author, licence and the edits made here. Every source is
CC0 1.0, and the air horn is made by this script.

The table below is the one place a clip's trim, fades and level are
written. A clip is cut from START to END seconds of its original; from
START, leading audio quieter than -50 dBFS is skipped, keeping 5 ms before
the first louder sample. Fades are half-sine. Every clip is brought to
-16 LUFS integrated, or lower where its true peak would pass -1 dBTP
first; the shortest clip is longer than the 400 ms block the loudness
measurement gates on, so a whoosh is measured as a crowd is. The level is
measured again on the encoded file and corrected. Clips are 48 kHz Ogg
Vorbis at quality 4, mono or stereo as the original.

    python3 tools/starter-sounds.py --check   # the library file and CREDITS match the table, every clip is there
    python3 tools/starter-sounds.py           # rewrite soundboard.json and CREDITS from the table
    python3 tools/starter-sounds.py --freesound DIR --kenney DIR   # also rebuild the clips (ffmpeg)

--freesound names a folder of the original Freesound downloads, each file
named as Freesound names it (ID__author__title.ext); --kenney names the
unpacked Kenney Voiceover Pack. The check needs only the standard library.
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "packaging" / "starter-sounds"
PAGE = "Starter"
RATE = 48000
QUALITY = 4
TARGET_LUFS = -16.0
CEILING_DBTP = -1.0
SILENCE_DBFS = -50.0
PREROLL = 0.005

CC0 = "CC0 1.0 Universal, https://creativecommons.org/publicdomain/zero/1.0/"

# Each clip: id (also its starter id), name, icon, where it comes from, and
# the cut. "end" None keeps the original to its end.
CLIPS = [
    dict(id="applause", name="Applause", icon="people", freesound=25295, author="freesound",
         title="claping_ses1", start=0.0, end=3.55, fade_in=0.005, fade_out=0.25,
         why="The whole take; its own decay is kept and the last, near-silent 0.3 s cut."),
    dict(id="cheer", name="Cheer", icon="people", freesound=397434, author="FoolBoyMedia",
         title="crowd-cheer", start=0.0, end=5.5, fade_in=0.005, fade_out=0.3,
         why="The first 5.5 s of a 20 s take, through the rise to full cheer; cut while the crowd is loud."),
    dict(id="laugh", name="Laugh", icon="people", freesound=581528, author="HowardV",
         title="live-audience-comedy-show-genuine-laughter_moderate", start=8.48, end=12.40,
         fade_in=0.02, fade_out=0.15,
         why="One laugh from a 16 s take, 8.48 to 12.40 s: it starts and ends in the quiet gaps around the loudest burst."),
    dict(id="boo", name="Boo", icon="people", freesound=264378, author="HowardV",
         title="crowd-booing", start=0.0, end=4.5, fade_in=0.01, fade_out=0.3,
         why="The first 4.5 s of a steady 21 s take, so the cut is a fade out of a sustained boo."),
    dict(id="rimshot", name="Rimshot", icon="music", freesound=440829, author="tlwmdbt",
         title="sting-rimshot-drum-roll-smooth", start=0.0, end=0.64, fade_in=0.005, fade_out=0.1,
         why="The rimshot sting alone, ending just before the drum roll that follows it in the file at 0.66 s."),
    dict(id="drumroll", name="Drumroll", icon="music", freesound=201211, author="Scheffler",
         title="drum-roll-please", start=0.0, end=7.8, fade_in=0.005, fade_out=0.3,
         why="The roll and its cymbal ending; the 2.4 s of silence before it is skipped and the tail cut below -46 dB."),
    dict(id="tada", name="Tada", icon="star", freesound=397355, author="plasterbrain",
         title="tada-fanfare-a", start=0.0, end=1.6, fade_in=0.005, fade_out=0.15,
         why="The whole fanfare, cut where it has decayed below -55 dB."),
    dict(id="sad-trombone", name="Sad trombone", icon="music", freesound=175409, author="kirbydx",
         title="wah-wah-sad-trombone", start=0.0, end=4.95, fade_in=0.005, fade_out=0.1,
         why="The whole phrase; the 0.2 s of silence before it is skipped."),
    dict(id="air-horn", name="Air horn", icon="burst", synth="airhorn", author="the OpenXLR project",
         title="Air horn, synthesised by tools/starter-sounds.py", start=0.0, end=None, fade_in=0.0,
         fade_out=0.0,
         why="Made here: three blasts of detuned sawtooth and square partials around 466 Hz."),
    dict(id="ding", name="Ding", icon="bell", freesound=173932, author="JohnsonBrandEditing",
         title="ding-ding-small-bell", start=0.0, end=3.0, fade_in=0.005, fade_out=0.3,
         why="The first ding of the file and its ring to -56 dB; the later double ding is left out."),
    dict(id="wrong-buzzer", name="Wrong buzzer", icon="burst", freesound=54047, author="guitarguy1985",
         title="buzzer", start=0.0, end=None, fade_in=0.005, fade_out=0.08,
         why="The whole buzz; the original stops at full level, so it gets an 80 ms fade."),
    dict(id="countdown", name="Countdown", icon="bell", freesound=368779, author="gurie",
         title="short-beep-countdown", start=0.0, end=None, fade_in=0.005, fade_out=0.01,
         why="All four beeps; the last one runs to the end of the file, so it gets a 10 ms fade."),
    dict(id="crickets", name="Crickets", icon="", freesound=129678, author="FreethinkerAnon",
         title="crickets", start=0.0, end=5.0, fade_in=0.05, fade_out=0.3,
         why="The first 5 s of a steady 15.6 s recording, faded at both ends."),
    dict(id="whoosh", name="Whoosh", icon="", freesound=60013, author="qubodup",
         title="whoosh", start=0.0, end=None, fade_in=0.002, fade_out=0.05,
         why="The whole 0.43 s whoosh, too short for the loudness gate, so its peak is set instead."),
    dict(id="record-scratch", name="Record scratch", icon="record", freesound=71853, author="ludvique",
         title="record_scratch", start=0.0, end=None, fade_in=0.002, fade_out=0.05,
         why="The whole 0.45 s scratch, too short for the loudness gate, so its peak is set instead."),
    dict(id="cash-register", name="Cash register", icon="", freesound=184438, author="CapsLok",
         title="cash-register-fake", start=0.0, end=1.5, fade_in=0.005, fade_out=0.1,
         why="The ring and the drawer; the 70 ms before it and the silent last 0.7 s are cut."),
    dict(id="you-win", name="You win", icon="game", kenney="Male/you_win.ogg",
         author="Kenney (kenney.nl), voice by Jeffrey M. Smith", title="Voiceover Pack, Male, you_win.ogg",
         start=0.0, end=None, fade_in=0.005, fade_out=0.05,
         why="The whole line; leading silence skipped."),
    dict(id="game-over", name="Game over", icon="game", kenney="Male/game_over.ogg",
         author="Kenney (kenney.nl), voice by Jeffrey M. Smith", title="Voiceover Pack, Male, game_over.ogg",
         start=0.0, end=None, fade_in=0.005, fade_out=0.05,
         why="The whole line; leading silence skipped."),
]

KENNEY_URL = "https://kenney.nl/assets/voiceover-pack"

# Three blasts, short, short, long, each a few detuned sawtooth voices and
# a square an octave down, with a 12 ms attack and a 40 ms release.
AIRHORN_BLASTS = [(0.0, 0.26), (0.34, 0.60), (0.68, 1.75)]


def airhorn_expression():
    def saw(f):
        return f"(2*({f}*t-floor(0.5+{f}*t)))"

    def square(f):
        return f"(2*gte(mod({f}*t,1),0.5)-1)"

    tone = f"({saw(466.2)}+{saw(469.6)}+{saw(462.9)}+0.6*{square(233.1)}+0.35*{saw(932.4)})/3.95"
    gate = "+".join(
        f"between(t,{s},{e})*min(1,(t-{s})/0.012)*min(1,({e}-t)/0.04)" for s, e in AIRHORN_BLASTS)
    return f"{tone}*({gate})"


def url(clip):
    if "freesound" in clip:
        return f"https://freesound.org/people/{clip['author']}/sounds/{clip['freesound']}/"
    if "kenney" in clip:
        return KENNEY_URL
    return "https://github.com/emaspa/openxlr/blob/main/tools/starter-sounds.py"


def source_name(clip):
    if "freesound" in clip:
        return f"Freesound sound {clip['freesound']}, {clip['title']}"
    return clip["title"]


def library_text():
    clips = [{"id": c["id"], "name": c["name"], "file": f"{c['id']}.ogg", "icon": c["icon"], "page": PAGE,
              "starter": c["id"]} for c in CLIPS]
    return json.dumps({"format": 1, "clips": clips}, indent=2) + "\n"


def edits(clip):
    parts = []
    if "synth" not in clip:
        span = f"{clip['start']:g} s to {clip['end']:g} s" if clip["end"] is not None else f"{clip['start']:g} s to the end"
        parts.append(f"cut from {span} of the original, leading audio under {SILENCE_DBFS:g} dBFS skipped")
        fades = []
        if clip["fade_in"]:
            fades.append(f"{clip['fade_in'] * 1000:g} ms fade in")
        if clip["fade_out"]:
            fades.append(f"{clip['fade_out'] * 1000:g} ms fade out")
        if fades:
            parts.append(", ".join(fades))
    parts.append(f"level set to {TARGET_LUFS:g} LUFS integrated, or lower where the true peak reaches {CEILING_DBTP:g} dBTP")
    parts.append(f"resampled to {RATE // 1000} kHz and encoded as Ogg Vorbis")
    return "; ".join(parts) + "."


def credits_text():
    lines = [
        "OpenXLR starter sounds",
        "",
        "OpenXLR adds the clips in clips/ to the soundboard once, on its Starter page.",
        "Each author dedicated their clip to the public domain under CC0 1.0:",
        CC0,
        "The licence asks for no credit; it is given here because it is due.",
        "tools/starter-sounds.py in the OpenXLR source makes the edits and holds",
        "the exact cut, fades and level of every clip. The edits are CC0 1.0 too,",
        "as is the air horn, which that script makes.",
        "",
    ]
    for clip in CLIPS:
        lines += [
            f"{clip['id']}.ogg: {clip['name']}",
            f"  Source: {source_name(clip)}",
            f"  URL: {url(clip)}",
            f"  Author: {clip['author']}",
            "  Licence: CC0 1.0",
            f"  Edits: {edits(clip)}",
            "",
        ]
    return "\n".join(lines)


def check():
    problems = []
    for name, text in (("soundboard.json", library_text()), ("CREDITS", credits_text())):
        path = OUT / name
        if not path.exists() or path.read_text() != text:
            problems.append(f"{path.relative_to(ROOT)} differs from the table; run python3 tools/starter-sounds.py")
    wanted = {f"{c['id']}.ogg" for c in CLIPS}
    present = {p.name for p in (OUT / "clips").iterdir()} if (OUT / "clips").is_dir() else set()
    for missing in sorted(wanted - present):
        problems.append(f"clips/{missing} is missing")
    for extra in sorted(present - wanted):
        problems.append(f"clips/{extra} is not in the table")
    for name in sorted(wanted & present):
        head = (OUT / "clips" / name).read_bytes()[:64]
        if not head.startswith(b"OggS") or b"\x01vorbis" not in head:
            problems.append(f"clips/{name} is not Ogg Vorbis")
    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


# --- building the clips, with ffmpeg ------------------------------------------

def run(args, data=None):
    return subprocess.run(args, input=data, capture_output=True, check=True)


def source_of(clip, freesound, kenney):
    if "freesound" in clip:
        if freesound is None:
            sys.exit("--freesound is needed to build the clips")
        found = sorted(freesound.glob(f"{clip['freesound']}__*"))
        if len(found) != 1:
            sys.exit(f"{clip['id']}: expected one file named {clip['freesound']}__* in {freesound}")
        return found[0]
    if "kenney" in clip:
        if kenney is None:
            sys.exit("--kenney is needed to build the clips")
        return kenney / clip["kenney"]
    return None


def channels_of(path):
    out = run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels",
               "-of", "csv=p=0", str(path)]).stdout.decode().strip()
    return min(2, int(out))


def decode(clip, path):
    """The clip's original as raw 32-bit float at RATE, and its channel count."""
    if path is None:
        end = AIRHORN_BLASTS[-1][1] + 0.05
        raw = run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i",
                   f"aevalsrc=exprs='{airhorn_expression()}':s={RATE}:d={end}",
                   "-af", "lowpass=f=5000,highpass=f=120", "-f", "f32le", "-"]).stdout
        return raw, 1
    channels = channels_of(path)
    raw = run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", str(channels), "-ar", str(RATE),
               "-f", "f32le", "-"]).stdout
    return raw, channels


def first_sound(raw, channels, start):
    """Where the cut starts: the first sample from START louder than the silence floor, less the pre-roll."""
    import array
    samples = array.array("f")
    samples.frombytes(raw)
    floor = 10 ** (SILENCE_DBFS / 20)
    for index in range(int(start * RATE) * channels, len(samples)):
        if abs(samples[index]) > floor:
            return max(start, index // channels / RATE - PREROLL)
    return start


def chain(clip, begin, length):
    filters = [f"atrim=start={begin:.5f}:end={begin + length:.5f}", "asetpts=PTS-STARTPTS"]
    if clip["fade_in"]:
        filters.append(f"afade=t=in:st=0:d={clip['fade_in']}:curve=hsin")
    if clip["fade_out"]:
        filters.append(f"afade=t=out:st={length - clip['fade_out']:.5f}:d={clip['fade_out']}:curve=hsin")
    return filters


def measure(args, data=None):
    """Integrated loudness and true peak of what ARGS feed ffmpeg."""
    err = run(["ffmpeg", "-hide_banner", "-nostats", *args, "-af",
               f"loudnorm=I={TARGET_LUFS}:TP={CEILING_DBTP}:print_format=json", "-f", "null", "-"], data).stderr.decode()
    stats = json.JSONDecoder().raw_decode(err[err.rindex("{"):])[0]
    return float(stats["input_i"]), float(stats["input_tp"])


def build(clip, freesound, kenney):
    path = source_of(clip, freesound, kenney)
    raw, channels = decode(clip, path)
    total = len(raw) // 4 // channels / RATE
    begin = first_sound(raw, channels, clip["start"])
    end = min(total, clip["end"]) if clip["end"] is not None else total
    length = end - begin
    pcm = ["-f", "f32le", "-ar", str(RATE), "-ac", str(channels), "-i", "-"]
    shaped = run(["ffmpeg", "-v", "error", *pcm, "-af", ",".join(chain(clip, begin, length)), "-f", "f32le", "-"],
                 raw).stdout
    loudness, peak = measure(pcm, shaped)
    gain = min(TARGET_LUFS - loudness, CEILING_DBTP - peak)
    target = OUT / "clips" / f"{clip['id']}.ogg"
    for _ in range(4):
        encode(shaped, channels, gain, target)
        loudness, peak = measure(["-i", str(target)])
        # Encoding moves the level a little and can lift the true peak:
        # correct toward the target, never past the ceiling.
        step = min(TARGET_LUFS - loudness, CEILING_DBTP - 0.05 - peak)
        if abs(step) < 0.1:
            break
        gain += step
    return dict(id=clip["id"], seconds=length, start=begin, gain=gain, lufs=loudness, tp=peak,
                size=target.stat().st_size, channels=channels)


def encode(raw, channels, gain, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    run(["ffmpeg", "-v", "error", "-y", "-f", "f32le", "-ar", str(RATE), "-ac", str(channels), "-i", "-",
         "-af", f"volume={gain:.3f}dB", "-c:a", "libvorbis", "-q:a", str(QUALITY), "-map_metadata", "-1",
         "-fflags", "+bitexact", "-flags:a", "+bitexact", str(target)], raw)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="compare instead of writing")
    parser.add_argument("--freesound", type=Path, help="folder of the original Freesound downloads")
    parser.add_argument("--kenney", type=Path, help="the unpacked Kenney Voiceover Pack")
    args = parser.parse_args()
    if args.check:
        return check()
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "soundboard.json").write_text(library_text())
    (OUT / "CREDITS").write_text(credits_text())
    if args.freesound is None and args.kenney is None:
        return 0
    total = 0
    for clip in CLIPS:
        result = build(clip, args.freesound, args.kenney)
        total += result["size"]
        print(f"{result['id']:15} {result['seconds']:5.2f} s  from {result['start']:6.3f} s  {result['channels']} ch"
              f"  gain {result['gain']:+6.2f} dB  {result['lufs']:6.1f} LUFS  {result['tp']:5.1f} dBTP"
              f"  {result['size'] / 1024:6.1f} KiB")
    print(f"total {total / 1024:.0f} KiB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
