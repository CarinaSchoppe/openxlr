import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

const voice = (more = {}) => ({enabled: false, voice: "deep", pitch: 0, formant: 0, active: null, available: true, error: null, ...more});
const state = (setting = voice()) => ({type: "state", connected: true, profiles: [], devices: [], capabilities: {xlrInputs: 1}, state: {},
  mixer: {channels: [{id: "xlr1", name: "Main mic", microphone: true, hardware: true, levels: {monitor: 1}, mutedIn: [],
                      appearance: {icon: "", defaultIcon: "mic", colour: null}, voice: setting}],
          mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false}],
          monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0}});

// The voice key against a fake OpenDeck host and a fake daemon: a toggle
// press, a hold press and its release, a multi-action hold, and a press that
// cannot switch a voice on.
test("a voice key toggles or holds its voice with one command per press", async () => {
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, skin: process.env.OPENXLR_SKIN};
  process.env.OPENXLR_SKIN = "default";
  const intervals = [], sockets = [];
  globalThis.setInterval = (...args) => { const value = previous.interval(...args); intervals.push(value); return value; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  const flush = () => new Promise(setImmediate);
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const host = sockets[0];
    const daemon = control.children[0];
    daemon.onopen(); await flush();
    const push = async setting => { daemon.receive(state(setting)); await flush(); };
    await push();
    const appear = (context, settings) => {
      host.receive({event: "willAppear", context, action: "com.emaspa.openxlr.toggle", payload: {settings}});
      host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});
    };
    const sent = () => daemon.messages.filter(m => m.cmd === "setVoiceChanger").map(({requestId, ...rest}) => rest);
    const answer = async () => {
      daemon.receive({type: "commandResult", requestId: daemon.messages.at(-1).requestId, error: null});
      await flush();
    };
    const alerts = context => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const press = async (context, payload = {}) => {
      host.receive({event: "keyDown", context, payload}); await flush();
      host.receive({event: "keyUp", context, payload}); await flush();
    };

    appear("robot", {target: "voice:xlr1:robot"});
    appear("witch", {target: "voice:xlr1:witch", voiceMode: "hold"});

    // Toggle: on with the voice, then off with the switch alone.
    await press("robot");
    assert.deepEqual(sent(), [{cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "robot"}]);
    await answer();
    await push(voice({enabled: true, voice: "robot", active: "robot"}));
    await press("robot");
    assert.deepEqual(sent().at(-1), {cmd: "setVoiceChanger", channel: "xlr1", enabled: false});
    await answer();
    await push();

    // Hold: on at the press, off at the release, without reading the state between.
    const before = sent().length;
    host.receive({event: "keyDown", context: "witch", payload: {}}); await flush();
    assert.deepEqual(sent().at(-1), {cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "witch"});
    await answer();
    host.receive({event: "keyUp", context: "witch", payload: {}}); await flush();
    assert.deepEqual(sent().slice(before), [
      {cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "witch"},
      {cmd: "setVoiceChanger", channel: "xlr1", enabled: false}]);
    await answer();

    // Inside a multi-action the key up comes at once, so a hold key toggles there.
    const multi = sent().length;
    await press("witch", {isInMultiAction: true});
    assert.deepEqual(sent().slice(multi), [{cmd: "setVoiceChanger", channel: "xlr1", enabled: true, voice: "witch"}]);
    await answer();

    // Without the native host a voice cannot be switched on: the key alerts and sends nothing.
    await push(voice({available: false, error: "no host"}));
    const count = sent().length;
    await press("robot");
    assert.equal(sent().length, count);
    assert.equal(alerts("robot"), 1);
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    for (const interval of intervals) clearInterval(interval);
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
