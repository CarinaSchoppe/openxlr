import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import { clipKey, clipPress, clipProgress, progressBucket, clipAppearance, clipChoices, boardNotice, pressMode }
  from "../com.emaspa.openxlr.sdPlugin/soundboard.mjs";
import { iconPath } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const clip = (id, more = {}) => ({id, name: id[0].toUpperCase() + id.slice(1), seconds: 1, gain: 1, mode: "overlap",
  loop: false, icon: "", colour: null, loaded: true, error: null, playing: false, status: "ready", ...more});
const mixer = (soundboard = {}) => ({
  channels: [{id: "sfx", name: "SFX", appearance: {icon: "", defaultIcon: "burst", colour: "#123456"}}],
  soundboard: {channel: "sfx", enabled: true, available: true, error: null, formats: [], playing: [],
               clips: [clip("airhorn"), clip("cheer", {mode: "toggle"})], ...soundboard}});

test("a press plays in the clip's mode unless the key gives its own, and only hold waits for the release", () => {
  const key = clipKey(mixer(), {target: "clip:cheer"});
  assert.deepEqual(clipPress(key, {}), {payload: {cmd: "playClip", clip: "cheer"}, hold: false});
  assert.deepEqual(clipPress(key, {mode: "restart"}), {payload: {cmd: "playClip", clip: "cheer", mode: "restart"}, hold: false});
  assert.deepEqual(clipPress(key, {mode: "hold"}), {payload: {cmd: "playClip", clip: "cheer", mode: "hold"}, hold: true});
  assert.deepEqual(clipPress(key, {mode: "sideways"}), {payload: {cmd: "playClip", clip: "cheer"}, hold: false},
    "a mode the daemon does not know is not sent");
  const hold = clipKey(mixer({clips: [clip("airhorn", {mode: "hold"})]}), {target: "clip:airhorn"});
  assert.equal(clipPress(hold, {}).hold, true);
  assert.equal(clipPress(hold, {mode: "overlap"}).hold, false);
  assert.deepEqual(clipPress(clipKey(mixer(), {target: "stopClips"}), {mode: "hold"}), {payload: {cmd: "stopClips"}, hold: false});
  assert.equal(pressMode(null, ""), "overlap");
});

test("a key reads why it cannot play, the board's reason before the clip's", () => {
  const word = (soundboard, target = "clip:airhorn", settings = {}) => clipKey(mixer(soundboard), {target, ...settings}).word;
  assert.equal(word({}), null);
  // The board cannot play: its error says why and every clip is off.
  const off = (fields, error = "A sentence.") => ({...fields, error, clips: [clip("airhorn", {status: "off", loaded: false})]});
  assert.equal(word(off({available: false, channel: null, enabled: false})), "unavailable");
  assert.equal(word(off({channel: null, enabled: false})), "no SFX");
  assert.equal(word(off({enabled: false})), "off");
  assert.equal(word(off({})), "stopped", "a player starting or failed");
  assert.equal(word(off({enabled: false}, null)), "stopped", "an off clip never reads as ready");
  assert.equal(word({}, "clip:gone-clip"), "gone");
  assert.equal(word(off({enabled: false}), "clip:gone-clip"), "gone", "a removed clip is the key's own fault first");
  assert.equal(word({clips: [clip("airhorn", {loaded: false, status: "loading"})]}), "loading");
  assert.equal(word({clips: [clip("airhorn", {loaded: false, status: "error", error: "The file is not audio."})]}), "error");
  assert.equal(word({clips: [clip("airhorn", {status: undefined})]}), "update", "a clip without a status is from an older daemon");
  assert.equal(clipKey(mixer(off({enabled: false})), {target: "stopClips"}).word, "off");
  assert.equal(clipKey({channels: []}, {target: "clip:airhorn"}).word, "update");
  assert.equal(clipKey(mixer(), {}).word, "set up");
  assert.equal(clipKey(mixer(), {target: "clip:"}).word, "set up");
  assert.equal(clipKey(mixer({}), {target: "clip:old", name: "Old horn"}).label, "Old horn");
  assert.equal(clipKey(mixer({}), {target: "clip:old"}).label, "old");
  const stop = clipKey(mixer({playing: ["airhorn"]}), {target: "stopClips"});
  assert.deepEqual([stop.stop, stop.word, stop.playing], [true, null, true]);
  assert.equal(clipKey(mixer({playing: ["airhorn"], channel: null, error: "SFX was deleted."}), {target: "stopClips"}).playing, false);
});

test("progress is the newest voice's position from the meters, in two pixel steps", () => {
  assert.equal(clipProgress({}, "airhorn"), null);
  assert.equal(clipProgress(null, "airhorn"), null);
  assert.equal(clipProgress({"clip:airhorn": "x"}, "airhorn"), null);
  assert.deepEqual(clipProgress({"clip:airhorn": [.25, 3]}, "airhorn"), {position: .25, voices: 3});
  assert.deepEqual(clipProgress({"clip:airhorn": [1.4, 1]}, "airhorn"), {position: 1, voices: 1});
  assert.equal(progressBucket(null), -1);
  assert.equal(progressBucket({position: .5}), progressBucket({position: .505}));
  assert.notEqual(progressBucket({position: .5}), progressBucket({position: .53}));
});

test("a clip's icon and colour fall back to SFX's one by one", () => {
  const sfx = mixer();
  assert.deepEqual(clipAppearance(sfx, clip("a")), {icon: "", path: iconPath("burst"), colour: "#123456"});
  assert.deepEqual(clipAppearance(sfx, clip("a", {icon: "♪"})), {icon: "♪", path: null, colour: "#123456"});
  assert.deepEqual(clipAppearance(sfx, clip("a", {icon: "bell", colour: "#ABCDEF"})), {icon: "", path: iconPath("bell"), colour: "#ABCDEF"});
  assert.deepEqual(clipAppearance(sfx, clip("a", {icon: "none"})), {icon: "", path: null, colour: "#123456"});
  assert.deepEqual(clipAppearance(sfx, clip("a", {icon: "<svg>", colour: "red"})), {icon: "", path: iconPath("burst"), colour: "#123456"},
    "only listed glyphs, built-in icons and #RRGGBB reach the key");
  assert.deepEqual(clipAppearance({channels: [], soundboard: {channel: null}}, clip("a")), {icon: "", path: null, colour: null});
});

test("the inspector's notice is the daemon's sentence first", () => {
  assert.equal(boardNotice(null, false), "The OpenXLR daemon is not running.");
  assert.match(boardNotice({channels: []}, true), /no soundboard/);
  assert.equal(boardNotice(mixer({channel: null, enabled: false, error: "SFX was deleted."}), true), "SFX was deleted.");
  assert.equal(boardNotice(mixer({available: false, error: null}), true), null, "the plugin infers no sentence of its own");
  assert.equal(boardNotice(mixer(), true), null);
  assert.deepEqual(clipChoices(mixer(), true).clips[1], {id: "cheer", name: "Cheer", mode: "toggle", error: null});
});

// A small DOM for clip.js: enough for its selects and the notice.
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this._value = ""; this.textContent = ""; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options ?? []); }
  get value() { return this.tag === "select" ? (this.options.some(o => o.value === this._value) ? this._value : "") : this._value; }
  set value(value) { this._value = value; }
}

test("the inspector lists clips by id, keeps a missing clip selected under its saved name, and names the clip's mode", () => {
  const select = new Element("select"); select.id = "clip";
  for (const value of ["", "stopClips"]) { const o = new Element("option"); o.value = value; select.appendChild(o); }
  const mode = new Element("select"); mode.id = "mode";
  for (const value of ["", "overlap", "restart", "toggle", "hold"]) { const o = new Element("option"); o.value = value; mode.appendChild(o); }
  const notice = new Element("div"); notice.id = "notice";
  const all = node => [node, ...node.children.flatMap(all)];
  const document = {createElement: tag => new Element(tag),
    getElementById: id => [select, mode, notice].flatMap(all).find(node => node.id === id)};
  const context = vm.createContext({document, window: {}});
  vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/clip.js", import.meta.url), "utf8"), context);
  const settings = {target: "clip:cheer", name: "Cheer"};
  const show = choices => { vm.runInContext("library = " + JSON.stringify(choices), context); context.fillClips(choices, settings); context.showChoice(settings); };

  show(clipChoices(mixer(), true));
  assert.equal(select.value, "clip:cheer");
  assert.deepEqual(select.options.map(o => o.textContent).slice(2), ["Airhorn", "Cheer"]);
  assert.equal(mode.options[0].textContent, "The clip's own mode (toggle)");
  assert.equal(notice.textContent, "");

  show(clipChoices(mixer({clips: [clip("airhorn")]}), true));
  assert.equal(select.value, "clip:cheer");
  const missing = document.getElementById("missing-clip");
  assert.equal(missing.textContent, "Missing: Cheer");
  assert.equal(missing.disabled, true);
  assert.match(notice.textContent, /no longer in the soundboard/);

  show(clipChoices(mixer({clips: [clip("airhorn"), clip("cheer", {name: "Crowd cheer", status: "error", loaded: false, error: "The file is not audio."})]}), true));
  assert.equal(select.value, "clip:cheer");
  assert.equal(document.getElementById("missing-clip"), undefined);
  assert.equal(select.options.filter(o => o.value === "clip:cheer").length, 1);
  assert.equal(select.options.find(o => o.value === "clip:cheer").textContent, "Crowd cheer");
  assert.equal(notice.textContent, "The file is not audio.");

  show(clipChoices(mixer({enabled: false, error: "The soundboard is off. Turn it on first."}), true));
  assert.equal(notice.textContent, "The soundboard is off. Turn it on first.");

  select.value = "stopClips"; settings.target = "stopClips";
  context.showChoice(settings);
  assert.equal(mode.disabled, true);
});
