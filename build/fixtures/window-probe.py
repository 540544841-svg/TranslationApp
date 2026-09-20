"""Runtime-harness debug probe (not part of the app): lists visible windows, optionally
filtered by pid, so verify-batch5-runtime.ps1 can match window titles exactly.
Usage: python window-probe.py [pid]   (no argument = every process)
"""
import ctypes
import sys

pid = int(sys.argv[1]) if len(sys.argv) > 1 else 0
EnumWindows = ctypes.windll.user32.EnumWindows
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
GetWindowText = ctypes.windll.user32.GetWindowTextW
GetWindowTextLength = ctypes.windll.user32.GetWindowTextLengthW
IsVisible = ctypes.windll.user32.IsWindowVisible
GetPid = ctypes.windll.user32.GetWindowThreadProcessId


def visit(hwnd, _):
    if not IsVisible(hwnd):
        return True
    target = ctypes.c_ulong()
    GetPid(hwnd, ctypes.byref(target))
    if pid and target.value != pid:
        return True
    length = GetWindowTextLength(hwnd)
    buf = ctypes.create_unicode_buffer(length + 1)
    GetWindowText(hwnd, buf, length + 1)
    if buf.value:
        print(target.value, repr(buf.value), hwnd)
    return True


EnumWindows(EnumWindowsProc(visit), 0)
