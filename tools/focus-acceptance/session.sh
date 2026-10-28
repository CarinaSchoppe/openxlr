#!/bin/bash
# Run only inside the disposable acceptance container, on its private buses and virtual displays.
set -euo pipefail
source_name=$1
export HOME=/tmp/focus-home XDG_RUNTIME_DIR=/tmp/focus-runtime
export XDG_CONFIG_HOME=$HOME/config XDG_DATA_HOME=$HOME/data DOTNET_CLI_HOME=$HOME/dotnet
mkdir -p "$XDG_RUNTIME_DIR" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
chmod 700 "$XDG_RUNTIME_DIR"
export LIBGL_ALWAYS_SOFTWARE=1 NO_AT_BRIDGE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
if [[ ${FOCUS_PRIVATE_BUS:-0} != 1 ]]; then
    exec env FOCUS_PRIVATE_BUS=1 dbus-run-session -- bash "$0" "$source_name"
fi
fixture=/repo/tools/focus-acceptance
export OPENXLR_TEST_FOCUS_SOURCE=$source_name
case "$source_name" in
    gnome)
        export XDG_CURRENT_DESKTOP=GNOME XDG_SESSION_TYPE=wayland WAYLAND_DISPLAY=wayland-focus GDK_BACKEND=wayland
        mkdir -p "$XDG_DATA_HOME/gnome-shell/extensions"
        cp -r /repo/packaging/gnome/openxlr-focus@emaspa.github.com "$XDG_DATA_HOME/gnome-shell/extensions/"
        gsettings set org.gnome.shell enabled-extensions '["openxlr-focus@emaspa.github.com"]'
        gnome-shell --version
        gnome-shell --headless --wayland --no-x11 --virtual-monitor 1280x720 --wayland-display "$WAYLAND_DISPLAY" > /tmp/compositor.log 2>&1 &
        ;;
    sway)
        export XDG_CURRENT_DESKTOP=sway XDG_SESSION_TYPE=wayland GDK_BACKEND=wayland
        export WLR_BACKENDS=headless WLR_HEADLESS_OUTPUTS=1 WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1
        printf 'xwayland disable\noutput * mode 1280x720\n' > /tmp/sway.conf
        sway --version
        sway --unsupported-gpu -c /tmp/sway.conf > /tmp/compositor.log 2>&1 &
        ;;
    niri)
        export XDG_CURRENT_DESKTOP=niri XDG_SESSION_TYPE=wayland GDK_BACKEND=wayland DISPLAY=:90
        Xvfb :90 -screen 0 1280x720x24 > /tmp/xvfb.log 2>&1 &
        sleep .5
        : > /tmp/niri.kdl
        niri --version
        niri --config /tmp/niri.kdl > /tmp/compositor.log 2>&1 &
        ;;
    x11)
        export XDG_CURRENT_DESKTOP=Openbox XDG_SESSION_TYPE=x11 GDK_BACKEND=x11 DISPLAY=:90
        unset WAYLAND_DISPLAY
        Xvfb :90 -screen 0 1280x720x24 > /tmp/xvfb.log 2>&1 &
        sleep .5
        openbox > /tmp/compositor.log 2>&1 &
        ;;
    *) exit 2 ;;
esac
compositor=$!
trap 'kill "$compositor" 2>/dev/null || true' EXIT
for _ in {1..100}; do
    kill -0 "$compositor" 2>/dev/null || { cat /tmp/compositor.log; exit 1; }
    if [[ $source_name == sway || $source_name == niri ]]; then
        for socket in "$XDG_RUNTIME_DIR"/sway-ipc.*.sock; do [[ ! -S $socket ]] || export SWAYSOCK=$socket; done
        for socket in "$XDG_RUNTIME_DIR"/niri.*.sock; do [[ ! -S $socket ]] || export NIRI_SOCKET=$socket; done
        for socket in "$XDG_RUNTIME_DIR"/wayland-*; do [[ ! -S $socket ]] || export WAYLAND_DISPLAY=${socket##*/}; done
        [[ -z ${WAYLAND_DISPLAY:-} ]] || break
    elif [[ $source_name == gnome ]]; then
        if gdbus call --session --dest org.openxlr.Focus --object-path /org/openxlr/Focus --method org.openxlr.Focus.GetFocusedProcess >/dev/null 2>&1; then break; fi
    elif xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | grep -q 'window id'; then break
    fi
    sleep .2
done
query() {
    case "$source_name" in
        gnome) gdbus call --session --dest org.openxlr.Focus --object-path /org/openxlr/Focus --method org.openxlr.Focus.GetFocusedProcess | sed -n 's/^(uint32 \([0-9]*\),)$/\1/p' ;;
        sway) swaymsg -r -t get_tree | python3 -c 'import json,sys
def walk(n):
 if n.get("focused") and n.get("pid"):print(n["pid"])
 for k in ("nodes","floating_nodes"):
  for c in n.get(k,[]):walk(c)
walk(json.load(sys.stdin))' ;;
        niri) niri msg --json focused-window | python3 -c 'import json,sys;print((json.load(sys.stdin) or {}).get("pid",0))' ;;
        x11) window=$(xprop -root -f _NET_ACTIVE_WINDOW 32x _NET_ACTIVE_WINDOW); xprop -id "${window##* }" _NET_WM_PID | sed -n 's/.* = \([0-9]*\)$/\1/p' ;;
    esac
}
accept() {
    local pid=$1
    for _ in {1..80}; do
        if [[ $(query 2>/dev/null || true) == "$pid" ]]; then
            OPENXLR_TEST_FOCUSED_PID=$pid dotnet vstest /repo/src/OpenXLR.Tests/bin/Release/net10.0/OpenXLR.Tests.dll \
                --TestCaseFilter:FullyQualifiedName~LiveCompositorFocusTests
            return
        fi
        sleep .2
    done
    echo "Expected focused PID $pid; actual $(query 2>/dev/null || true)"
    if [[ $source_name == x11 ]]; then
        xprop -root _NET_ACTIVE_WINDOW || true
        xwininfo -root -tree || true
    fi
    cat /tmp/compositor.log /tmp/one.log /tmp/two.log 2>/dev/null || true
    exit 1
}
FIXTURE_ID=one gjs -m "$fixture/client.mjs" > /tmp/one.log 2>&1 & first=$!
if [[ $source_name == gnome ]]; then sleep 2; gjs -m "$fixture/escape.mjs"; fi
accept "$first"
FIXTURE_ID=two gjs -m "$fixture/client.mjs" > /tmp/two.log 2>&1 & second=$!
accept "$second"
kill "$second"
wait "$second" 2>/dev/null || true
accept "$first"
if [[ $source_name == gnome ]]; then
    gnome-extensions disable openxlr-focus@emaspa.github.com
    for _ in {1..50}; do
        if ! query >/dev/null 2>&1; then break; fi
        sleep .1
    done
    if query >/dev/null 2>&1; then echo 'Disabled extension kept its focus service'; exit 1; fi
    gnome-extensions enable openxlr-focus@emaspa.github.com
    accept "$first"
fi
kill "$first"
echo "$source_name: real focus changes and desktop-service queries passed"
