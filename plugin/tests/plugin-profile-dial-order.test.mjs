import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

test("profile recalls follow all pending dial edits and release their old local values", async t => {
  t.mock.timers.enable({apis: ["Date", "setTimeout"]});
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, skin: process.env.OPENXLR_SKIN};
  const sockets = [], intervals = [];
  process.env.OPENXLR_SKIN = "default";
  globalThis.setInterval = (...args) => { const timer = previous.interval(...args); intervals.push(timer); return timer; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor() { sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0], host = sockets[0];
    daemon.onopen();
    const state = {type: "state", connected: true, profiles: ["Quiet"], activeProfile: null,
      capabilities: {gain: true, gainMaxDb: 80, xlrInputs: 1}, state: {gainDb: 40}, devices: [],
      mixer: {channels: [{id: "music", name: "Music", volume: .5, levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor", volume: 1}], inserts: {}}};
    daemon.receive(state);
    const appear = (context, action, target) => host.receive({event: "willAppear", context,
      action: `com.emaspa.openxlr.${action}`, payload: {settings: {target}}});
    appear("channel", "dial", "chvol:music");
    appear("gain", "dial", "gain");
    appear("profile", "toggle", "profile|Quiet");
    const rotate = context => host.receive({event: "dialRotate", context, payload: {ticks: 1}});
    rotate("channel"); rotate("channel");
    rotate("gain"); rotate("gain");
    host.receive({event: "keyDown", context: "profile"});
    t.mock.timers.tick(81);
    assert.deepEqual(daemon.messages.filter(m => m.cmd !== "listPlugins"), [
      {cmd: "setChannelVolume", channel: "music", value: .51},
      {cmd: "set", control: "gain", value: 41},
      {cmd: "setChannelVolume", channel: "music", value: .52},
      {cmd: "set", control: "gain", value: 42},
      {cmd: "loadProfile", name: "Quiet"},
    ], "the recalled profile must be the final command, with each trailing value flushed once");

    state.activeProfile = "Quiet"; state.state.gainDb = 10; state.mixer.channels[0].volume = .2;
    daemon.receive(state);
    const feedback = context => host.messages.filter(m => m.event === "setFeedback" && m.context === context).at(-1).payload;
    assert.equal(feedback("channel").value, "20%");
    assert.equal(feedback("gain").value, "10");
    rotate("channel"); rotate("gain");
    assert.deepEqual(daemon.messages.slice(-2), [
      {cmd: "setChannelVolume", channel: "music", value: .21},
      {cmd: "set", control: "gain", value: 11},
    ], "a new turn starts from the recalled value rather than a warm pre-recall value");
    const before = daemon.messages.length;
    host.receive({event: "keyDown", context: "profile"});
    t.mock.timers.tick(81);
    assert.deepEqual(daemon.messages.slice(before), [{cmd: "loadProfile", name: "Quiet"}],
      "a second recall without a pending turn must not replay already sent edits");
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
  }
});
