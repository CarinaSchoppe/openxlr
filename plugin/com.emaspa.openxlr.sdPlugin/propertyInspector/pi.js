// Shared property-inspector glue: registers with the host (Elgato-compatible
// entry point, which OpenDeck calls too), loads the action's settings into
// the #target select, and saves on change. It also asks the plugin for the
// list of physical output devices and appends them as a "Monitor output"
// group, so a key can switch the monitor mix to a specific device.
"use strict";

let ws = null, piUuid = null, actionContext = null;

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
  piUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  const wanted = actionInfo.payload?.settings?.target;

  ws = new WebSocket("ws://localhost:" + inPort);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: inRegisterEvent, uuid: piUuid }));
    const sel = document.getElementById("target");
    if (wanted) sel.value = wanted;
    sel.addEventListener("change", () => {
      ws.send(JSON.stringify({
        event: "setSettings",
        context: piUuid,
        payload: { target: sel.value },
      }));
    });
    // Ask the plugin for the live output-device list.
    ws.send(JSON.stringify({
      event: "sendToPlugin",
      context: actionContext,
      payload: { request: "outputs" },
    }));
  };

  ws.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.event === "sendToPropertyInspector" && Array.isArray(m.payload?.outputs))
      fillMonitors(m.payload.outputs, wanted);
  };
}

function fillMonitors(outputs, wanted) {
  const sel = document.getElementById("target");
  if (!sel || document.getElementById("monitor-group")) return;
  const group = document.createElement("optgroup");
  group.id = "monitor-group";
  group.label = "Monitor output";
  for (const o of outputs) {
    const opt = document.createElement("option");
    opt.value = "monitor:" + o.name;
    opt.textContent = o.description || o.name;
    group.appendChild(opt);
  }
  sel.appendChild(group);
  // A previously saved monitor target may not have existed as an <option>
  // until now, so re-apply the selection once the group is present.
  if (wanted) sel.value = wanted;
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
