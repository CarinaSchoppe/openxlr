"""X11 acceptance gesture for the test compressor; never moves the user pointer.

The node property identifies the exact test instance. Events target its canvas
directly, avoiding XWayland/compositor focus stealing. Callers assert the g_out
callback and measured audio change; an incompatible layout fails the test.
"""
import ctypes as c
import ctypes.util


class Button(c.Structure):
    _fields_ = [("type", c.c_int), ("serial", c.c_ulong), ("send_event", c.c_int),
                ("display", c.c_void_p), ("window", c.c_ulong), ("root", c.c_ulong),
                ("subwindow", c.c_ulong), ("time", c.c_ulong), ("x", c.c_int), ("y", c.c_int),
                ("x_root", c.c_int), ("y_root", c.c_int), ("state", c.c_uint),
                ("button", c.c_uint), ("same_screen", c.c_int)]


class Event(c.Union):
    _fields_ = [("button", Button), ("padding", c.c_long * 24)]


def wheel_compressor_output(node="OpenXLR_ins_ch_qa-channel_in_lv2_0"):
    x = c.CDLL(ctypes.util.find_library("X11"))
    ptr, win = c.c_void_p, c.c_ulong
    x.XOpenDisplay.argtypes, x.XOpenDisplay.restype = [c.c_char_p], ptr
    x.XDefaultRootWindow.argtypes, x.XDefaultRootWindow.restype = [ptr], win
    x.XQueryTree.argtypes = [ptr, win, c.POINTER(win), c.POINTER(win), c.POINTER(c.POINTER(win)), c.POINTER(c.c_uint)]
    x.XInternAtom.argtypes, x.XInternAtom.restype = [ptr, c.c_char_p, c.c_int], win
    x.XGetWindowProperty.argtypes = [ptr, win, win, c.c_long, c.c_long, c.c_int, win, c.POINTER(win),
                                    c.POINTER(c.c_int), c.POINTER(c.c_ulong), c.POINTER(c.c_ulong), c.POINTER(ptr)]
    x.XFree.argtypes = [ptr]
    x.XGetGeometry.argtypes = [ptr, win, c.POINTER(win), c.POINTER(c.c_int), c.POINTER(c.c_int),
                              c.POINTER(c.c_uint), c.POINTER(c.c_uint), c.POINTER(c.c_uint), c.POINTER(c.c_uint)]
    x.XTranslateCoordinates.argtypes = [ptr, win, win, c.c_int, c.c_int, c.POINTER(c.c_int), c.POINTER(c.c_int), c.POINTER(win)]
    x.XSendEvent.argtypes = [ptr, win, c.c_int, c.c_long, c.POINTER(Event)]
    x.XKeysymToKeycode.argtypes, x.XKeysymToKeycode.restype = [ptr, win], c.c_ubyte
    x.XSync.argtypes = [ptr, c.c_int]
    x.XCloseDisplay.argtypes = [ptr]
    display = x.XOpenDisplay(None)
    assert display, "native UI test needs an X11/XWayland display"
    root = x.XDefaultRootWindow(display)
    property_id = x.XInternAtom(display, b"_OPENXLR_NODE", 0)

    def find(window):
        actual, count, remaining, bits, value = win(), c.c_ulong(), c.c_ulong(), c.c_int(), ptr()
        x.XGetWindowProperty(display, window, property_id, 0, 256, 0, 0,
                             c.byref(actual), c.byref(bits), c.byref(count), c.byref(remaining), c.byref(value))
        if value:
            matched = bits.value == 8 and c.string_at(value, count.value).decode() == node
            x.XFree(value)
            if matched:
                return window
        children = c.POINTER(win)()
        parent, tree_root, size = win(), win(), c.c_uint()
        if x.XQueryTree(display, window, c.byref(tree_root), c.byref(parent), c.byref(children), c.byref(size)):
            try:
                for i in range(size.value):
                    result = find(children[i])
                    if result:
                        return result
            finally:
                if children:
                    x.XFree(children)
        return None

    try:
        window = find(root)
        assert window, "test instance's native window not found"
        gx, gy, rx, ry = (c.c_int() for _ in range(4))
        width, height, border, depth = (c.c_uint() for _ in range(4))
        child, geom_root = win(), win()
        x.XGetGeometry(display, window, c.byref(geom_root), c.byref(gx), c.byref(gy),
                       c.byref(width), c.byref(height), c.byref(border), c.byref(depth))
        assert width.value > 800 and height.value > 450, "unexpected LSP layout"
        px, py = round(width.value * 900 / 945), round(height.value * 441 / 546)
        x.XTranslateCoordinates(display, window, root, px, py, c.byref(rx), c.byref(ry), c.byref(child))
        # Descend from the host parent into the plugin's actual toolkit canvas.
        while True:
            x.XTranslateCoordinates(display, window, window, px, py, c.byref(gx), c.byref(gy), c.byref(child))
            if not child.value:
                break
            target = child.value
            x.XTranslateCoordinates(display, window, target, px, py, c.byref(gx), c.byref(gy), c.byref(child))
            window, px, py = target, gx.value, gy.value
        event = Event()
        event.button = Button(2, 0, 1, display, window, root, 0, 0, px, py, rx.value, ry.value, 0, 0, 1)
        # A fresh LSP profile opens a modal greeting after one second. Its
        # documented Escape shortcut dismisses it; the toolkit routes these
        # key events to that dialog. XKeyEvent and XButtonEvent share layout.
        event.button.button = x.XKeysymToKeycode(display, 0xff1b)  # XK_Escape
        for kind, mask in ((2, 1), (3, 2)):
            event.button.type = kind
            assert x.XSendEvent(display, window, 0, mask, c.byref(event))
        event.button.type, event.button.button = 6, 0
        x.XSendEvent(display, window, 0, 1 << 6, c.byref(event))
        for kind, mask in ((4, 1 << 2), (5, 1 << 3)):
            event.button.type, event.button.button = kind, 4
            assert x.XSendEvent(display, window, 0, mask, c.byref(event))
        x.XSync(display, 0)
    finally:
        x.XCloseDisplay(display)
