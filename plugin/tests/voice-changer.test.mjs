import assert from "node:assert/strict";
import test from "node:test";
import {VOICES, isVoiceTarget, voiceTarget, voiceMode, voicePress, voiceRelease, voiceLabel, voiceChoices}
  from "../com.emaspa.openxlr.sdPlugin/voice-changer.mjs";
import {layoutChoices, targetAppearance} from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const voiceMixer = (voice = {enabled: false, voice: "deep", pitch: 0, formant: 0, active: null, available: true, error: null}) => ({
  mixes: [{id: "monitor", name: "Monitor A", kind: "monitor"}],
  channels: [
    {id: "xlr1", name: "Main mic", microphone: true, hardware: true, appearance: {icon: "mic", colour: "#12ab34"}, voice},
    {id: "guest", name: "Guest", microphone: true, voice: {...voice, enabled: false}},
    {id: "xlr2", name: "XLR 2", present: false, voice},
    {id: "music", name: "Music", microphone: false},
  ],
});

test("the voices are the daemon's, in its order", () => {
  assert.deepEqual(VOICES, ["custom", "deep", "high", "robot", "radio", "alien", "dracula", "frankenstein", "mummy", "werewolf", "witch"]);
});

test("a voice key reads its microphone's setting and lights while that voice plays", () => {
  const mixer = voiceMixer({enabled: true, voice: "robot", pitch: 0, formant: 0, active: "robot", available: true, error: null});
  assert.ok(isVoiceTarget("voice:xlr1:robot"));
  assert.ok(!isVoiceTarget("eq:xlr1"));
  assert.equal(voiceTarget(mixer, "voice:xlr1:robot").lit, true);
  assert.equal(voiceTarget(mixer, "voice:xlr1:witch").lit, false);
  assert.equal(voiceTarget(mixer, "voice:guest:robot").lit, false);
  assert.equal(voiceLabel(voiceTarget(mixer, "voice:xlr1:frankenstein")), "Main mic\nFrankenstein");
  // Malformed, unknown, absent or without a voice changer: nothing.
  for (const target of ["voice:xlr1", "voice:xlr1:ghost", "voice:xlr1:robot:x", "voice:xlr2:robot", "voice:music:robot", "voice:a b:robot"])
    assert.equal(voiceTarget(mixer, target), null, target);
  assert.equal(voiceLabel(null, "voice:xlr2:mummy"), "Voice\nMummy");
  // The key wears its microphone's icon and colour.
  assert.equal(targetAppearance(mixer, "voice:xlr1:robot").colour, "#12ab34");
});

test("a toggle press switches the voice on, or off when it is the one playing, in one command", () => {
  const mixer = voiceMixer();
  assert.deepEqual(voicePress(voiceTarget(mixer, "voice:xlr1:dracula")),
    {payload: {cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "dracula"}, hold: false});
  mixer.channels[0].voice = {...mixer.channels[0].voice, enabled: true, voice: "dracula"};
  assert.deepEqual(voicePress(voiceTarget(mixer, "voice:xlr1:dracula")),
    {payload: {cmd: "setVoiceChanger", channel: "xlr1", enabled: false}, hold: false});
  // Another voice's key moves the microphone to its voice.
  assert.deepEqual(voicePress(voiceTarget(mixer, "voice:xlr1:witch")).payload,
    {cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "witch"});
  assert.equal(voicePress(null), null);
});

test("a hold press plays the voice while the key is down, and toggles inside a multi-action", () => {
  const mixer = voiceMixer();
  assert.equal(voiceMode({}), "toggle");
  assert.equal(voiceMode({voiceMode: "hold"}), "hold");
  assert.equal(voiceMode({voiceMode: "latch"}), "toggle");
  const entry = voiceTarget(mixer, "voice:guest:alien");
  assert.deepEqual(voicePress(entry, "hold"), {payload: {cmd: "setVoiceChanger", channel: "guest", enabled: true, voice: "alien"}, hold: true});
  assert.deepEqual(voiceRelease("guest"), {cmd: "setVoiceChanger", channel: "guest", enabled: false});
  assert.equal(voicePress(entry, "hold", true).hold, false);
});

test("without the native host a voice cannot be switched on, only off", () => {
  const mixer = voiceMixer({enabled: true, voice: "radio", pitch: 0, formant: 0, active: null, available: false, error: "no host"});
  assert.equal(voicePress(voiceTarget(mixer, "voice:xlr1:robot")), null);
  assert.equal(voicePress(voiceTarget(mixer, "voice:xlr1:robot"), "hold"), null);
  assert.deepEqual(voicePress(voiceTarget(mixer, "voice:xlr1:radio")).payload, {cmd: "setVoiceChanger", channel: "xlr1", enabled: false});
});

test("the inspector offers a key per voice for each microphone with a voice changer", () => {
  const mixer = voiceMixer();
  const groups = voiceChoices(mixer).toggleGroups;
  assert.deepEqual(groups.map(g => g.id), ["layout-voice-keys-xlr1", "layout-voice-keys-guest"]);
  assert.equal(groups[0].label, "Voice changer: Main mic");
  assert.equal(groups[0].items.length, 11);
  assert.deepEqual(groups[0].items[7], {target: "voice:xlr1:frankenstein", label: "Main mic voice: Frankenstein"});
  assert.ok(layoutChoices(mixer).toggleGroups.some(group => group.id === "layout-voice-keys-guest"));
  // A daemon without the voice changer offers none.
  for (const channel of mixer.channels) delete channel.voice;
  assert.deepEqual(voiceChoices(mixer).toggleGroups, []);
});
