import assert from "node:assert/strict";
import test from "node:test";
import {eqTarget, eqChoices, eqStep, eqCommand, eqDial, eqLabel} from "../com.emaspa.openxlr.sdPlugin/microphone-eq.mjs";
import {layoutChoices, targetAppearance} from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";
import {eqMixer} from "./microphone-eq-fixture.mjs";

test("choices cover each microphone's EQ, four band switches, types and numeric dials", () => {
  const mixer = eqMixer();
  mixer.channels.push({...structuredClone(mixer.channels[0]), id: "guest", name: "Guest mic", hardware: false, captureSource: "usb"},
    {id: "music", name: "Music", microphone: false},
    {...structuredClone(mixer.channels[0]), id: "xlr2", present: false});
  const choices = eqChoices(mixer);
  assert.equal(choices.toggleGroups.length, 2);
  assert.equal(choices.toggleGroups[0].items.length, 9);
  assert.equal(choices.levelGroups[0].items.length, 16);
  assert.ok(layoutChoices(mixer).levelGroups.some(group => group.items.some(item => item.target === "eqq:guest:4")));
  mixer.channels[0].name = "Renamed";
  assert.ok(eqChoices(mixer).toggleGroups[0].items.some(item => item.target === "eq:xlr1" && item.label.includes("Renamed")));
  assert.equal(targetAppearance(mixer, "eqgain:xlr1:2").colour, "#12ab34");
});

for (const target of ["eq:missing", "eq:xlr1:1", "eqband:xlr1:0", "eqband:xlr1:5", "eqgain:xlr1:01",
  "eqgain:xlr1:-1", "eqq:xlr1:2:extra", "eqgain:xlr1", "eqgain::1", "eqgain:xlr1:NaN", "eqgain:xlr1:1.5",
  "eqgain:xlr1:" + ":".repeat(1024), null, {}])
  test("invalid or removed target is unavailable: " + String(target), () => assert.equal(eqTarget(eqMixer(), target), null));

for (const change of [
  mixer => { delete mixer.eqLimits; },
  mixer => { mixer.eqLimits.q = {min: .1, max: 10, step: .016}; },
  mixer => { mixer.eqLimits.frequency.step = 0; },
  mixer => { mixer.eqLimits.gain.max = Infinity; },
  mixer => { mixer.eqLimits.q.min = 0; },
  mixer => { mixer.eqLimits.types = ["peak", "peak"]; },
  mixer => { mixer.channels[0].eq.bands.pop(); },
  mixer => { mixer.channels[0].eq.bands[0] = null; },
  mixer => { mixer.channels[0].eq.bands[1].gain = NaN; },
  mixer => { mixer.channels[0].eq.bands[1].frequency = "1000"; },
  mixer => { mixer.channels[0].eq.bands[1].q = -1; },
  mixer => { mixer.channels[0].microphone = false; },
  mixer => { mixer.channels = {}; },
  mixer => { mixer.channels = [null]; },
]) test("malformed EQ state offers no control: " + change.toString(), () => {
  const mixer = eqMixer(); change(mixer);
  assert.equal(eqTarget(mixer, "eqgain:xlr1:2"), null);
  assert.deepEqual(eqChoices(mixer), {toggleGroups: [], levelGroups: []});
});

test("all writes are partial and a missing host can still be switched off", () => {
  const mixer = eqMixer();
  assert.deepEqual(eqCommand(eqTarget(mixer, "eq:xlr1"), true), {cmd: "setEqEnabled", channel: "xlr1", value: true});
  assert.deepEqual(eqCommand(eqTarget(mixer, "eqband:xlr1:3"), false), {cmd: "setEqBand", channel: "xlr1", band: 3, enabled: false});
  assert.deepEqual(eqCommand(eqTarget(mixer, "eqtype:xlr1:4"), 5), {cmd: "setEqBand", channel: "xlr1", band: 4, type: "highCut"});
  assert.deepEqual(eqCommand(eqTarget(mixer, "eqgain:xlr1:2"), -.25), {cmd: "setEqBand", channel: "xlr1", band: 2, gain: -.25});
  mixer.channels[0].eq.available = false;
  assert.equal(eqCommand(eqTarget(mixer, "eq:xlr1"), true), null);
  assert.deepEqual(eqCommand(eqTarget(mixer, "eq:xlr1"), false), {cmd: "setEqEnabled", channel: "xlr1", value: false});
});

test("turns respect frequency ratios, quarter dB steps, the Q grid and both bounds", () => {
  const mixer = eqMixer();
  const frequency = eqTarget(mixer, "eqfreq:xlr1:2");
  assert.equal(eqStep(frequency, 1000, 24), 2000);
  assert.equal(eqStep(frequency, 20, 1), 21);
  assert.equal(eqStep(frequency, 20000, Number.MAX_SAFE_INTEGER), 20000);
  assert.equal(eqStep(frequency, 20, -Number.MAX_SAFE_INTEGER), 20);
  const gain = eqTarget(mixer, "eqgain:xlr1:2");
  assert.equal(eqStep(gain, 0, -1), -.25);
  assert.equal(eqStep(gain, 0, 1000), 12);
  assert.equal(eqStep(gain, 0, -1000), -12);
  for (const ticks of [NaN, Infinity, "2", null, 1.5, 0]) assert.equal(eqStep(gain, 0, ticks), null);
  const q = eqTarget(mixer, "eqq:xlr1:2");
  assert.ok(Math.abs(eqStep(q, q.value, 1) - Math.pow(10, -.136)) < 1e-12);
  assert.equal(eqStep(q, 10, 1), 10);
  assert.equal(eqStep(q, .1, -1), .1);
  const type = eqTarget(mixer, "eqtype:xlr1:2");
  assert.equal(eqStep(type, 5, 1), 0);
  assert.equal(eqStep(type, 0, -1), 5);
});

test("cut and notch gain stays stored and is neither written nor presented as editable", () => {
  for (const type of ["lowCut", "notch", "highCut"]) {
    const mixer = eqMixer(); mixer.channels[0].eq.bands[1].type = type;
    const gain = eqTarget(mixer, "eqgain:xlr1:2");
    assert.equal(eqStep(gain, 0, 1), null);
    assert.equal(eqCommand(gain, 1), null);
    assert.equal(eqDial(gain, 0).text, "n/a");
    assert.equal(mixer.channels[0].eq.bands[1].gain, 0);
  }
});

test("every Q grid point steps to its neighbour without floating point drift", () => {
  const entry = eqTarget(eqMixer(), "eqq:xlr1:2");
  for (let point = 0; point <= 125; point++) {
    const value = Math.pow(10, -1 + point * .016);
    for (const delta of [-1, 1]) {
      const expected = Math.pow(10, -1 + Math.min(125, Math.max(0, point + delta)) * .016);
      assert.ok(Math.abs(eqStep(entry, value, delta) - expected) < 1e-12, `Q grid ${point}, step ${delta}`);
    }
  }
});

test("unknown future type labels remain strings instead of object prototype members", () => {
  const mixer = eqMixer();
  mixer.eqLimits.types = ["constructor"];
  for (const band of mixer.channels[0].eq.bands) band.type = "constructor";
  const entry = eqTarget(mixer, "eqtype:xlr1:2");
  assert.equal(eqDial(entry, 0).text, "constructor");
  assert.match(eqLabel(entry, 0), /constructor/);
});

test("dial labels separate bypass from values, and report names without treating them as markup", () => {
  const mixer = eqMixer();
  mixer.channels[0].name = "<Guest & mic>";
  const entry = eqTarget(mixer, "eqfreq:xlr1:2");
  assert.equal(eqDial(entry, 1000).text, "1000 Hz");
  assert.match(eqDial(entry, 1000).scroll, /EQ off/);
  assert.equal(eqDial(entry, 1000).muted, false);
  assert.equal(eqDial(entry, 20).pct, 0);
  assert.equal(eqDial(entry, 20000).pct, 100);
  assert.match(eqLabel(entry), /<Guest & mic>/);
  mixer.channels[0].eq.enabled = true; mixer.channels[0].eq.bands[1].enabled = false;
  assert.match(eqDial(eqTarget(mixer, "eqq:xlr1:2"), .1).scroll, /band off/);
  const type = eqTarget(mixer, "eqtype:xlr1:2");
  assert.equal(eqDial(type, 0).text, "High pass");
  assert.equal(eqDial(type, 5).text, "Low pass");
  assert.equal(eqDial(type, 4).text, "Hi shelf");
  assert.match(eqLabel(type, 4), /High shelf/);
});


test("variable band choices follow each microphone and never expose a removed band", () => {
  const mixer = eqMixer();
  mixer.eqLimits.minBands = 1; mixer.eqLimits.maxBands = 16;
  const first = mixer.channels[0].eq.bands[0];
  for (const count of [1, 3, 4, 16]) {
    mixer.channels[0].eq.bands = Array.from({length: count}, () => ({...first}));
    const choices = eqChoices(mixer);
    assert.equal(choices.levelGroups[0].items.length, count * 4);
    assert.ok(eqTarget(mixer, `eqgain:xlr1:${count}`));
    assert.equal(eqTarget(mixer, `eqgain:xlr1:${count + 1}`), null);
  }
  mixer.channels[0].eq.bands.push({...first});
  assert.equal(eqTarget(mixer, "eq:xlr1"), null);
});
