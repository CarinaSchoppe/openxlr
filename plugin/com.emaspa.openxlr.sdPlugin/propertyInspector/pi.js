// Shared property-inspector glue: registers with the host (Elgato-compatible
// entry point, which OpenDeck calls too), loads the action's settings into
// the #target select, and saves on change.
"use strict";

let ws = null, piUuid = null, actionContext = null;

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
  piUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;

  ws = new WebSocket("ws://localhost:" + inPort);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: inRegisterEvent, uuid: piUuid }));
    const sel = document.getElementById("target");
    const current = actionInfo.payload?.settings?.target;
    if (current) sel.value = current;
    sel.addEventListener("change", () => {
      ws.send(JSON.stringify({
        event: "setSettings",
        context: piUuid,
        payload: { target: sel.value },
      }));
    });
  };
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
