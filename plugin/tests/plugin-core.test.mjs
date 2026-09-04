import assert from "node:assert/strict";
import test from "node:test";

import {
  channelName,
  mixName,
  mixerChoices,
  monitorOutputChoices,
  normalisedLevelCommand,
  targetAccent,
  toggledMonitorOutputs,
} from "../com.emaspa.openxlr.sdPlugin/plugin-core.mjs";

const mixer = {
  monitoredMixId: "broadcast-vod",
  monitorOutputs: ["alsa_output.headphones"],
  mixes: [
    { id: "monitor", name: "My Ears", muted: false, volume: 0.8 },
    { id: "broadcast-vod", name: "Broadcast + VOD", muted: true, volume: 0.6 },
  ],
  channels: [
    { id: "xlr1", name: "Host Mic", levels: { monitor: 0.7, "broadcast-vod": 0.9 }, mutedIn: [] },
    { id: "alerts-new", name: "Alerts & SFX", levels: { monitor: 0.4, "broadcast-vod": 0.5 }, mutedIn: ["monitor"] },
  ],
};

test("dynamic choices use stable ids and current editable names", () => {
  const choices = mixerChoices(mixer);
  assert.deepEqual(choices.toggleGroups[0].items, [
    { target: "listen:monitor", label: "My Ears" },
    { target: "listen:broadcast-vod", label: "Broadcast + VOD" },
  ]);
  assert.ok(choices.toggleGroups[2].items.some((entry) =>
    entry.target === "route:alerts-new:broadcast-vod" && entry.label === "Alerts & SFX → Broadcast + VOD"));
  assert.ok(choices.levelGroups.some((group) => group.items.some((entry) =>
    entry.target === "send:alerts-new:broadcast-vod")));
  assert.equal(channelName(mixer, "alerts-new"), "Alerts & SFX");
  assert.equal(mixName(mixer, "broadcast-vod"), "Broadcast + VOD");
});

test("deleted entries disappear instead of leaving new stale choices", () => {
  const afterDelete = {
    ...mixer,
    mixes: mixer.mixes.filter((entry) => entry.id !== "broadcast-vod"),
    channels: mixer.channels.filter((entry) => entry.id !== "alerts-new"),
  };
  const serialised = JSON.stringify(mixerChoices(afterDelete));
  assert.doesNotMatch(serialised, /broadcast-vod|alerts-new/);
});

test("monitor device toggles are additive and preserve unrelated outputs", () => {
  assert.deepEqual(toggledMonitorOutputs(["headphones", "speakers"], "headphones"), ["speakers"]);
  assert.deepEqual(toggledMonitorOutputs(["headphones"], "speakers"), ["headphones", "speakers"]);
  assert.deepEqual(toggledMonitorOutputs(["headphones", "headphones"], "speakers"), ["headphones", "speakers"]);
});

test("physical output picker excludes OpenXLR-owned nodes", () => {
  const choices = monitorOutputChoices({ devices: [
    { kind: 0, isOwn: false, name: "headphones", description: "USB Headphones" },
    { kind: 0, isOwn: true, name: "OpenXLR_mix_monitor", description: "internal" },
    { kind: 1, isOwn: false, name: "microphone", description: "Mic" },
  ] });
  assert.deepEqual(choices, [{ target: "monitor:headphones", label: "USB Headphones" }]);
});

test("level keys map percentages onto each daemon command scale", () => {
  assert.deepEqual(normalisedLevelCommand("mixvol:broadcast-vod", 25, mixer),
    [{ cmd: "setMixVolume", mix: "broadcast-vod", value: 0.25 }]);
  assert.deepEqual(normalisedLevelCommand("send:alerts-new:all", 50, mixer), [
    { cmd: "setLevel", channel: "alerts-new", mix: "monitor", value: 0.5 },
    { cmd: "setLevel", channel: "alerts-new", mix: "broadcast-vod", value: 0.5 },
  ]);
  assert.deepEqual(normalisedLevelCommand("gain", 50, mixer),
    [{ cmd: "set", control: "gain", value: 40 }]);
  assert.deepEqual(normalisedLevelCommand("hp2", 25, mixer),
    [{ cmd: "set", control: "hp2VolumeDb", value: -45 }]);
  assert.deepEqual(normalisedLevelCommand("crossfade", 75, mixer),
    [{ cmd: "set", control: "crossfade", value: 150 }]);
});

test("target accents are stable and target-specific", () => {
  assert.equal(targetAccent("send:xlr1:monitor"), targetAccent("send:xlr1:monitor"));
  assert.notEqual(targetAccent("send:xlr1:monitor"), targetAccent("send:alerts-new:broadcast-vod"));
});
