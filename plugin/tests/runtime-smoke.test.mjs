import assert from "node:assert/strict";
import crypto from "node:crypto";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import test from "node:test";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const pluginDirectory = path.resolve(testDirectory, "../com.emaspa.openxlr.sdPlugin");

class MessageQueue {
  #messages = [];
  #waiters = [];

  push(message) {
    const index = this.#waiters.findIndex((waiter) => waiter.predicate(message));
    if (index >= 0) {
      const [waiter] = this.#waiters.splice(index, 1);
      clearTimeout(waiter.timer);
      waiter.resolve(message);
    } else {
      this.#messages.push(message);
    }
  }

  wait(predicate, timeoutMs = 3000) {
    const index = this.#messages.findIndex(predicate);
    if (index >= 0) return Promise.resolve(this.#messages.splice(index, 1)[0]);
    return new Promise((resolve, reject) => {
      const waiter = { predicate, resolve, timer: null };
      waiter.timer = setTimeout(() => {
        this.#waiters = this.#waiters.filter((entry) => entry !== waiter);
        reject(new Error("timed out waiting for WebSocket message"));
      }, timeoutMs);
      this.#waiters.push(waiter);
    });
  }
}

class TinyWebSocketServer {
  constructor() {
    this.messages = new MessageQueue();
    this.socket = null;
    this.server = net.createServer((socket) => this.#accept(socket));
  }

  async listen() {
    await new Promise((resolve, reject) => {
      this.server.once("error", reject);
      this.server.listen(0, "127.0.0.1", resolve);
    });
    return this.server.address().port;
  }

  #accept(socket) {
    let buffer = Buffer.alloc(0);
    let upgraded = false;
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (!upgraded) {
        const end = buffer.indexOf("\r\n\r\n");
        if (end < 0) return;
        const headers = buffer.subarray(0, end).toString("utf8");
        const key = headers.match(/^Sec-WebSocket-Key:\s*(.+)$/im)?.[1]?.trim();
        if (!key) { socket.destroy(); return; }
        const accept = crypto.createHash("sha1")
          .update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest("base64");
        socket.write("HTTP/1.1 101 Switching Protocols\r\n" +
          "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
          `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
        upgraded = true;
        this.socket = socket;
        buffer = buffer.subarray(end + 4);
      }
      buffer = this.#frames(buffer);
    });
  }

  #frames(input) {
    let buffer = input;
    while (buffer.length >= 2) {
      const opcode = buffer[0] & 0x0f;
      const masked = (buffer[1] & 0x80) !== 0;
      let length = buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (buffer.length < 4) break;
        length = buffer.readUInt16BE(2); offset = 4;
      } else if (length === 127) {
        if (buffer.length < 10) break;
        length = Number(buffer.readBigUInt64BE(2)); offset = 10;
      }
      const maskBytes = masked ? 4 : 0;
      if (buffer.length < offset + maskBytes + length) break;
      const mask = masked ? buffer.subarray(offset, offset + 4) : null;
      offset += maskBytes;
      const payload = Buffer.from(buffer.subarray(offset, offset + length));
      if (mask) for (let index = 0; index < payload.length; index++) payload[index] ^= mask[index % 4];
      buffer = buffer.subarray(offset + length);
      if (opcode === 0x1) {
        try { this.messages.push(JSON.parse(payload.toString("utf8"))); } catch { /* tested peer only sends JSON */ }
      } else if (opcode === 0x8) {
        this.socket?.end();
      } else if (opcode === 0x9) {
        this.#sendFrame(payload, 0xA);
      }
    }
    return buffer;
  }

  #sendFrame(payload, opcode = 0x1) {
    const body = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
    let header;
    if (body.length < 126) {
      header = Buffer.from([0x80 | opcode, body.length]);
    } else if (body.length <= 0xffff) {
      header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 126; header.writeUInt16BE(body.length, 2);
    } else {
      header = Buffer.alloc(10); header[0] = 0x80 | opcode; header[1] = 127; header.writeBigUInt64BE(BigInt(body.length), 2);
    }
    this.socket.write(Buffer.concat([header, body]));
  }

  send(message) {
    assert.ok(this.socket, "WebSocket client has connected");
    this.#sendFrame(JSON.stringify(message));
  }

  async close() {
    this.socket?.destroy();
    await new Promise((resolve) => this.server.close(resolve));
  }
}

const state = {
  type: "state",
  daemonVersion: "test",
  connected: true,
  capabilities: { gain: true, mute: true, phantom: true, lowCut: true,
    expander: true, voiceTune: true, clipGuard: true, compressor: true,
    lowImpedance: true, hpVolume: true, crossfade: true, outputRouting: true,
    auxInput: true, xlrInputs: 1, hpOutputs: 1 },
  state: { gainDb: 40, mute: false, hpVolumeDb: -12, outHp1: true, crossfade: 100 },
  profiles: ["Podcast"], activeProfile: null,
  devices: [
    { kind: 0, isOwn: false, name: "headphones", description: "Studio Headphones" },
    { kind: 0, isOwn: false, name: "speakers", description: "Desk Speakers" },
  ],
  mixer: {
    monitoredMixId: "broadcast-vod", monitorOutputs: ["headphones"], outputVolume: 0.7,
    lowCutHz: 0, softClipGuard: false, softClipGuardAvailable: true, inserts: {},
    mixes: [
      { id: "monitor", name: "My Ears", volume: 0.8, muted: false },
      { id: "broadcast-vod", name: "Broadcast + VOD", volume: 0.6, muted: false },
    ],
    channels: [
      { id: "xlr1", name: "Host Mic", levels: { monitor: 0.7, "broadcast-vod": 0.9 }, mutedIn: [] },
      { id: "alerts-new", name: "Alerts & SFX", levels: { monitor: 0.4, "broadcast-vod": 0.5 }, mutedIn: [] },
    ],
  },
};

const is = (event, context) => (message) => message.event === event && (!context || message.context === context);
const appear = (host, context, action, settings) => host.send({
  event: "willAppear", context, action, payload: { settings, controller: action.endsWith("dial") ? "Encoder" : "Keypad" },
});

test("runtime bridges live editable mixer controls between OpenDeck and daemon", async (t) => {
  const host = new TinyWebSocketServer();
  const daemon = new TinyWebSocketServer();
  const [hostPort, daemonPort] = await Promise.all([host.listen(), daemon.listen()]);
  const child = spawn(process.execPath, ["plugin.mjs", "-port", String(hostPort),
    "-pluginUUID", "com.emaspa.openxlr.sdPlugin", "-registerEvent", "registerPlugin"], {
    cwd: pluginDirectory,
    env: { ...process.env, OPENXLR_DAEMON_URL: `ws://127.0.0.1:${daemonPort}/ws` },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let stderr = "";
  child.stderr.on("data", (chunk) => { stderr += chunk; });
  t.after(async () => {
    child.kill("SIGTERM");
    await Promise.race([
      new Promise((resolve) => child.once("exit", resolve)),
      new Promise((resolve) => setTimeout(resolve, 1000)),
    ]);
    await Promise.all([host.close(), daemon.close()]);
    assert.equal(stderr, "");
  });

  assert.equal((await host.messages.wait(is("registerPlugin"))).uuid, "com.emaspa.openxlr.sdPlugin");
  assert.equal((await daemon.messages.wait((message) => message.cmd === "listPlugins")).cmd, "listPlugins");
  daemon.send(state);
  daemon.send({ type: "plugins", plugins: [] });

  appear(host, "route-key", "com.emaspa.openxlr.toggle", { target: "route:alerts-new:broadcast-vod" });
  appear(host, "monitor-key", "com.emaspa.openxlr.toggle", { target: "monitor:speakers" });
  appear(host, "listen-key", "com.emaspa.openxlr.toggle", { target: "listen:monitor" });
  appear(host, "level-key", "com.emaspa.openxlr.level",
    { target: "send:alerts-new:broadcast-vod", behaviour: "set", amount: 25 });
  appear(host, "dial", "com.emaspa.openxlr.dial",
    { target: "mixvol:broadcast-vod", targets: ["mixvol:broadcast-vod"] });

  const routeImage = await host.messages.wait(is("setImage", "route-key"));
  assert.match(routeImage.payload.image, /^data:image\/svg\+xml;base64,/);
  const levelImage = await host.messages.wait(is("setImage", "level-key"));
  assert.match(Buffer.from(levelImage.payload.image.split(",")[1], "base64").toString(), /Alerts &amp; SFX/);
  const dialFeedback = await host.messages.wait(is("setFeedback", "dial"));
  assert.match(dialFeedback.payload.needle, /^data:image\/svg\+xml;base64,/);
  assert.match(dialFeedback.payload.accent, /^data:image\/svg\+xml;base64,/);

  host.send({ event: "sendToPlugin", context: "route-key", payload: { request: "configuration" } });
  const configuration = await host.messages.wait(is("sendToPropertyInspector", "route-key"));
  assert.equal(configuration.payload.online, true);
  assert.ok(configuration.payload.toggleGroups.some((group) => group.items.some((entry) =>
    entry.target === "route:alerts-new:broadcast-vod" && entry.label === "Alerts & SFX → Broadcast + VOD")));

  host.send({ event: "propertyInspectorDidAppear", context: "level-key" });
  const levelConfiguration = await host.messages.wait(is("sendToPropertyInspector", "level-key"));
  assert.ok(levelConfiguration.payload.levelGroups.some((group) => group.items.some((entry) =>
    entry.target === "send:alerts-new:broadcast-vod")));

  host.send({ event: "keyDown", context: "route-key" });
  assert.deepEqual(await daemon.messages.wait((message) => message.cmd === "setChannelMuted"),
    { cmd: "setChannelMuted", channel: "alerts-new", mix: "broadcast-vod", value: true });

  host.send({ event: "keyDown", context: "monitor-key" });
  assert.deepEqual(await daemon.messages.wait((message) => message.cmd === "setMonitorOutputs"),
    { cmd: "setMonitorOutputs", devices: ["headphones", "speakers"] });

  host.send({ event: "keyDown", context: "listen-key" });
  assert.deepEqual(await daemon.messages.wait((message) => message.cmd === "setMonitoredMix"),
    { cmd: "setMonitoredMix", mix: "monitor" });

  host.send({ event: "keyDown", context: "level-key" });
  assert.deepEqual(await daemon.messages.wait((message) => message.cmd === "setLevel"),
    { cmd: "setLevel", channel: "alerts-new", mix: "broadcast-vod", value: 0.25 });

  host.send({ event: "dialRotate", context: "dial", payload: { ticks: 5 } });
  assert.deepEqual(await daemon.messages.wait((message) => message.cmd === "setMixVolume"),
    { cmd: "setMixVolume", mix: "broadcast-vod", value: 0.65 });
});
