import assert from "node:assert/strict";
import test from "node:test";
import childProcess from "node:child_process";
import {syncBuiltinESMExports} from "node:module";
import {EventEmitter} from "node:events";
import {PassThrough, Writable} from "node:stream";
import {mkdtempSync, rmSync} from "node:fs";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {eqMixer} from "./microphone-eq-fixture.mjs";

test("EQ keys and dial stacks preserve turns without replaying stale sessions or bindings", async t => {
  t.mock.timers.enable({apis: ["Date", "setTimeout"]});
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, spawn: childProcess.spawn,
    skin: process.env.OPENXLR_SKIN, runtime: process.env.XDG_RUNTIME_DIR, config: process.env.XDG_CONFIG_HOME};
  const directory = mkdtempSync(join(tmpdir(), "openxlr-deck-eq-"));
  const sockets = [], children = [], intervals = [];
  process.env.OPENXLR_SKIN = "default";
  process.env.XDG_RUNTIME_DIR = directory;
  process.env.XDG_CONFIG_HOME = directory;
  globalThis.setInterval = (...args) => { const value = previous.interval(...args); intervals.push(value); return value; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
    open() { this.onopen(); }
    drop() { this.readyState = 3; this.onclose({code: 1006}); }
  }
  // The same protocol test works with TCP today and the proposed Unix
  // stdio adapter. Neither transport opens a real connection in this test.
  childProcess.spawn = (file, args) => {
    assert.equal(file, "openxlr-control"); assert.deepEqual(args, []);
    const child = new EventEmitter();
    child.messages = [];
    child.stdout = new PassThrough();
    child.stdin = new Writable({write(chunk, encoding, done) {
      for (const line of chunk.toString().trim().split("\n")) child.messages.push(JSON.parse(line));
      done();
    }});
    child.kill = () => { child.killed = true; };
    child.receive = value => child.stdout.write(JSON.stringify(value) + "\n");
    child.open = () => child.receive({type: "controlReady"});
    child.drop = () => child.emit("close");
    children.push(child);
    return child;
  };
  syncBuiltinESMExports();
  globalThis.WebSocket = Socket;
  const currentPeer = () => children.at(-1) ?? sockets.filter(socket => socket.url.includes(":37890/")).at(-1);
  const svg = image => Buffer.from(image.split(",")[1], "base64").toString();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    let daemon = currentPeer();
    const host = sockets.find(socket => !socket.url.includes(":37890/"));
    daemon.open();
    let state;
    const reset = () => {
      t.mock.timers.tick(1000);
      state = {type: "state", connected: true, capabilities: {xlrInputs: 1}, state: {}, devices: [], profiles: [], mixer: eqMixer()};
      daemon.receive(state);
    };
    const appear = (context, target, dial = false, more = {}) => {
      host.receive({event: "willAppear", context, action: `com.emaspa.openxlr.${dial ? "dial" : "toggle"}`,
        payload: {settings: {...(dial ? {targets: [target]} : {target}), ...more}}});
      if (!dial) host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});
    };
    const key = context => host.receive({event: "keyDown", context});
    const rotate = (context, ticks) => host.receive({event: "dialRotate", context, payload: {ticks}});
    const writes = () => daemon.messages.filter(message => /^setEq/.test(message.cmd));
    const feedback = context => host.messages.filter(message => message.event === "setFeedback" && message.context === context).at(-1).payload;
    const image = context => svg(host.messages.filter(message => message.event === "setImage" && message.context === context).at(-1).payload.image);
    const meter = context => svg(host.messages.filter(message => message.event === "setFeedback" && message.context === context && message.payload.meter).at(-1).payload.meter);
    reset();
    appear("eq", "eq:xlr1"); appear("band", "eqband:xlr1:2"); appear("type", "eqtype:xlr1:2");
    appear("gain", "eqgain:xlr1:2", true);
    host.receive({event: "sendToPlugin", context: "inspector", payload: {request: "layout"}});
    assert.ok(host.messages.at(-1).payload.levelGroups.some(group => group.items.some(item => item.target === "eqq:xlr1:4")));
    assert.ok(image("eq").includes("#12ab34"));
    assert.equal(feedback("gain").value, "0 dB");
    daemon.receive({type: "meters", levels: {"mix:monitor": [.9, .9], "ch:xlr1": [.25, .25]}});
    assert.match(meter("gain"), /<rect width="33" height="6"/);
    key("eq"); key("eq"); key("eq");
    t.mock.timers.tick(81);
    assert.deepEqual(writes().at(-1), {cmd: "setEqEnabled", channel: "xlr1", value: true});
    reset();
    key("band"); key("band"); key("band");
    t.mock.timers.tick(81);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, enabled: false});
    reset(); key("type");
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, type: "notch"});
    reset();
    rotate("gain", 1); rotate("gain", 1); rotate("gain", 1);
    assert.equal(feedback("gain").value, "0.75 dB");
    state.mixer.channels[0].eq.bands[1].gain = .25;
    daemon.receive(state);
    assert.equal(feedback("gain").value, "0.75 dB", "an older report must not pull a turn back");
    t.mock.timers.tick(81);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, gain: .75});
    host.receive({event: "dialDown", context: "gain"});
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, enabled: false});

    reset(); state.mixer.channels[0].eq.bands[1].type = "notch"; daemon.receive(state);
    const unused = writes().length;
    rotate("gain", 1);
    assert.equal(writes().length, unused);
    assert.equal(feedback("gain").value, "n/a");

    reset(); rotate("gain", 1); rotate("gain", 1);
    const rebound = writes().length;
    host.receive({event: "didReceiveSettings", context: "gain", payload: {settings: {targets: ["eqgain:missing:2"]}}});
    assert.ok(!meter("gain").includes('fill="url(#level)"'), "rebinding must clear the old microphone meter immediately");
    t.mock.timers.tick(81);
    assert.equal(writes().length, rebound, "a rebound key must discard its previous trailing turn");

    reset(); appear("gain", "eqgain:xlr1:2", true); appear("second-gain", "eqgain:xlr1:2", true);
    rotate("gain", 1); rotate("second-gain", 1);
    host.receive({event: "didReceiveSettings", context: "gain", payload: {settings: {targets: ["eqgain:missing:2"]}}});
    t.mock.timers.tick(81);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, gain: .5},
      "changing one action must not cancel another action's last turn of the same field");
    host.receive({event: "willDisappear", context: "second-gain"});

    reset(); appear("gain", "eqgain:xlr1:2", true, {targets: ["eqgain:xlr1:2", "eqfreq:xlr1:2"], cycleGesture: "tap"});
    rotate("gain", 1); rotate("gain", 1);
    host.receive({event: "touchTap", context: "gain"});
    const settings = host.messages.filter(message => message.event === "setSettings" && message.context === "gain").at(-1).payload;
    assert.equal(settings.activeIndex, 1);
    host.receive({event: "didReceiveSettings", context: "gain", payload: {settings}});
    t.mock.timers.tick(81);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, gain: .5}, "cycling the stack must retain its last turn");
    rotate("gain", 24);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, frequency: 2000});

    reset(); appear("gain", "eqgain:xlr1:2", true); rotate("gain", 1); rotate("gain", 1);
    const changed = writes().length;
    state.mixer.eqLimits.minBands = 1; state.mixer.eqLimits.maxBands = 16;
    state.mixer.channels[0].eq.bands.splice(0, 1);
    daemon.receive(state); t.mock.timers.tick(81);
    assert.equal(writes().length, changed, "a shape change cancels turns even when the old number still exists");

    reset(); appear("gain", "eqgain:xlr1:2", true); rotate("gain", 1); rotate("gain", 1);
    const removed = writes().length;
    state.mixer.channels = []; daemon.receive(state); t.mock.timers.tick(81); key("eq");
    assert.equal(writes().length, removed);
    assert.ok(!meter("gain").includes('fill="url(#level)"'), "a removed microphone must leave no stale meter");
    assert.ok(host.messages.some(message => message.event === "showAlert" && message.context === "eq"));

    reset(); rotate("gain", 1); rotate("gain", 1);
    daemon.drop(); t.mock.timers.tick(501);
    daemon = currentPeer(); daemon.open();
    state.mixer.channels[0].eq.bands[1].gain = -3;
    daemon.receive(state); t.mock.timers.tick(81);
    assert.equal(writes().length, 0, "the new session must not receive an old turn");
    rotate("gain", 1);
    assert.deepEqual(writes().at(-1), {cmd: "setEqBand", channel: "xlr1", band: 2, gain: -2.75});

    reset(); rotate("gain", 1); rotate("gain", 1);
    const disappeared = writes().length;
    host.receive({event: "willDisappear", context: "gain"}); t.mock.timers.tick(81);
    assert.equal(writes().length, disappeared);
    state.mixer.channels[0].name = "<Guest & mic>"; daemon.receive(state);
    assert.match(image("eq"), /&lt;Guest &amp; mic(?:>|&gt;)/);
    assert.ok(!image("eq").includes("<Guest & mic>"));
    state.mixer.channels[0].name = "]]>"; daemon.receive(state);
    assert.ok(!image("eq").includes("]]>"), "a label must not produce a forbidden XML text sequence");
    state.mixer.channels[0].eq.available = false; daemon.receive(state);
    const refused = writes().length;
    key("eq"); assert.equal(writes().length, refused);
    state.mixer.channels[0].eq.enabled = true; daemon.receive(state); key("eq");
    assert.deepEqual(writes().at(-1), {cmd: "setEqEnabled", channel: "xlr1", value: false});
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    childProcess.spawn = previous.spawn; syncBuiltinESMExports();
    for (const interval of intervals) clearInterval(interval);
    for (const child of children) { child.stdout.destroy(); child.stdin.destroy(); }
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
    if (previous.runtime === undefined) delete process.env.XDG_RUNTIME_DIR; else process.env.XDG_RUNTIME_DIR = previous.runtime;
    if (previous.config === undefined) delete process.env.XDG_CONFIG_HOME; else process.env.XDG_CONFIG_HOME = previous.config;
    rmSync(directory, {recursive: true, force: true});
  }
});
