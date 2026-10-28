import assert from "node:assert/strict";
import test from "node:test";
import {mkdtempSync, rmSync} from "node:fs";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {controlFixture} from "./control-fixture.mjs";

test("insert actions distinguish plugin formats and retain legacy settings safely", async t => {
  t.mock.timers.enable({apis: ["Date", "setTimeout"]});
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval,
    skin: process.env.OPENXLR_SKIN, config: process.env.XDG_CONFIG_HOME};
  const directory = mkdtempSync(join(tmpdir(), "openxlr-deck-format-"));
  process.env.OPENXLR_SKIN = "default";
  process.env.XDG_CONFIG_HOME = directory;
  const intervals = [], sockets = [];
  globalThis.setInterval = (...args) => {
    const interval = previous.interval(...args); intervals.push(interval); return interval;
  };
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
  const plugin = "0123456789ABCDEF0123456789ABCDEF";
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0], host = sockets[0];
    daemon.onopen();
    const entry = (kind, max) => ({kind, plugin, name: kind.toUpperCase(),
      params: [{symbol: "1", name: `${kind} trim`, min: 0, max, default: 0}]});
    const state = () => ({type: "state", connected: true, state: {}, profiles: [], devices: [],
      capabilities: {xlrInputs: 1}, mixer: {
        channels: [{id: "xlr1", name: "XLR 1", hardware: true, levels: {monitor: 1}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1}],
        monitorOutputs: [], monitorFeeds: {}, inserts: {xlr1: [
          {insert: {id: "clap-a", kind: "clap", plugin, params: {"1": 5}, bypass: false}},
          {insert: {id: "vst-a", kind: "vst3", plugin, params: {"1": 50}, bypass: false}},
        ]},
      }});
    const reset = () => {
      t.mock.timers.tick(1000);
      daemon.receive(state());
      daemon.receive({type: "plugins", plugins: [entry("clap", 10), entry("vst3", 1000)]});
    };
    const appear = (context, target, meta, dial = false) => host.receive({event: "willAppear", context,
      action: `com.emaspa.openxlr.${dial ? "dial" : "toggle"}`,
      payload: {settings: {target, ...(meta ? {meta: {[target]: meta}} : {})}}});
    const key = context => host.receive({event: "keyDown", context});
    const commands = cmd => daemon.messages.filter(message => message.cmd === cmd);
    const choices = () => {
      host.receive({event: "sendToPlugin", context: "inspector", payload: {request: "inserts"}});
      return host.messages.at(-1).payload;
    };

    await t.test("equal plugin ids retain each format's parameter scale", () => {
      reset();
      appear("clap-dial", "insparam|xlr1|clap-a|1", null, true);
      host.receive({event: "dialRotate", context: "clap-dial", payload: {ticks: 1}});
      assert.equal(commands("setInsertParam").at(-1).value, 5.1);
      appear("vst-dial", "insparam|xlr1|vst-a|1", null, true);
      host.receive({event: "dialRotate", context: "vst-dial", payload: {ticks: 1}});
      assert.equal(commands("setInsertParam").at(-1).value, 60);
    });
    await t.test("inspector choices carry the format and the right labels", () => {
      reset();
      const offered = choices();
      const clap = offered.params.find(choice => choice.target === "insparam|xlr1|clap-a|1");
      const vst = offered.params.find(choice => choice.target === "insparam|xlr1|vst-a|1");
      assert.equal(clap.meta.kind, "clap");
      assert.equal(vst.meta.kind, "vst3");
      assert.match(clap.label, /CLAP: clap trim$/);
      assert.match(vst.label, /VST3: vst3 trim$/);
    });
    await t.test("profile fallback does not substitute another plugin format", () => {
      reset();
      appear("recalled-vst", "insert|xlr1|old-vst", {plugin, kind: "vst3", index: 0});
      key("recalled-vst");
      assert.equal(commands("setInsertBypass").at(-1).insertId, "vst-a");
    });
    await t.test("an ambiguous legacy fallback sends nothing, but an exact id still works", () => {
      reset();
      appear("legacy-ambiguous", "insert|xlr1|gone", {plugin, index: 0});
      const before = commands("setInsertBypass").length;
      key("legacy-ambiguous");
      assert.equal(commands("setInsertBypass").length, before);
      appear("exact", "insert|xlr1|vst-a");
      key("exact");
      assert.equal(commands("setInsertBypass").at(-1).insertId, "vst-a");
    });
    await t.test("legacy metadata still resolves a unique plugin format", () => {
      reset();
      const single = state(); single.mixer.inserts.xlr1.pop(); daemon.receive(single);
      appear("legacy-unique", "insert|xlr1|gone-unique", {plugin, index: 0});
      key("legacy-unique");
      assert.equal(commands("setInsertBypass").at(-1).insertId, "clap-a");
    });
    await t.test("older LV2 catalogues and inserts without a kind remain usable", () => {
      reset();
      const old = state();
      old.mixer.inserts.xlr1 = [{insert: {id: "old-lv2", plugin: "urn:legacy", params: {gain: 5}}}];
      daemon.receive(old);
      daemon.receive({type: "plugins", plugins: [{plugin: "urn:legacy", name: "Legacy",
        params: [{symbol: "gain", name: "Gain", min: 0, max: 10, default: 0}]}]});
      appear("old-dial", "insparam|xlr1|old-lv2|gain", null, true);
      host.receive({event: "dialRotate", context: "old-dial", payload: {ticks: 1}});
      assert.equal(commands("setInsertParam").at(-1).value, 5.1);
    });
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previous.interval;
    globalThis.WebSocket = previous.socket;
    for (const [name, value] of [["OPENXLR_SKIN", previous.skin], ["XDG_CONFIG_HOME", previous.config]])
      if (value === undefined) delete process.env[name]; else process.env[name] = value;
    rmSync(directory, {recursive: true, force: true});
  }
});
