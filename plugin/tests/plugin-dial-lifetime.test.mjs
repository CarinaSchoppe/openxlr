import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

test("ordinary dial turns stay with their action and daemon lifetimes", async t => {
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
    let daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const state = {type: "state", connected: true, profiles: [], capabilities: {}, state: {}, devices: [],
      mixer: {channels: [{id: "music", name: "Music", volume: .5, levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor", volume: 1}], inserts: {}}};
    const appear = context => host.receive({event: "willAppear", context, action: "com.emaspa.openxlr.dial",
      payload: {settings: {target: "chvol:music"}}});
    const rotate = context => host.receive({event: "dialRotate", context, payload: {ticks: 1}});
    const writes = () => daemon.messages.filter(m => m.cmd === "setChannelVolume");
    const reset = () => { t.mock.timers.tick(1000); daemon.receive(state); };

    for (const event of ["willDisappear", "willAppear", "didReceiveSettings"]) {
      await t.test(`${event} cancels only the departing action's pending turn`, () => {
        reset(); appear("one"); rotate("one"); rotate("one");
        const before = writes().length;
        host.receive({event, context: "one", action: "com.emaspa.openxlr.dial",
          payload: {settings: {target: "mixvol:monitor"}}});
        t.mock.timers.tick(81);
        assert.equal(writes().length, before, "a retired binding must not send its trailing edit");
      });
    }
    await t.test("a cosmetic settings change preserves a pending turn", () => {
      reset(); appear("one"); rotate("one"); rotate("one");
      host.receive({event: "didReceiveSettings", context: "one", payload: {settings: {target: "chvol:music", icon: "mic"}}});
      t.mock.timers.tick(81);
      assert.equal(writes().at(-1).value, .52);
    });
    await t.test("retiring one dial preserves the most recent turn from another dial of the same field", () => {
      reset(); appear("one"); appear("two"); rotate("one"); rotate("two");
      host.receive({event: "willDisappear", context: "one"});
      t.mock.timers.tick(81);
      assert.equal(writes().at(-1).value, .52);
      const before = writes().length;
      rotate("two");
      host.receive({event: "willDisappear", context: "two"});
      t.mock.timers.tick(81);
      assert.equal(writes().length, before, "retiring the last writer cancels that writer's edit");
    });
    await t.test("a reconnect within the local hold window starts from the new daemon's value", () => {
      reset(); appear("one"); rotate("one"); rotate("one");
      daemon.emit("close");
      t.mock.timers.tick(500);
      daemon = control.children.at(-1); daemon.onopen();
      state.mixer.channels[0].volume = .2;
      daemon.receive(state);
      rotate("one");
      assert.equal(writes().at(-1).value, .21, "a new session must not step from the old session's warm value");
    });
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
  }
});
