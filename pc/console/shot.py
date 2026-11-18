"""最终截图：启动「斗在呢」窗口版并截图保存到 docs/img/。"""
import ctypes
import ctypes.wintypes as w
import os
import struct
import subprocess
import time
import zlib

EXE = r"C:\Users\Admin\WorkBuddy\HWWorksheet\pc\dist\DouZaiNe\DouZaiNe.exe"
OUT = r"C:\Users\Admin\WorkBuddy\HWWorksheet\docs\img\console.png"

os.makedirs(os.path.dirname(OUT), exist_ok=True)

subprocess.run(["taskkill", "/F", "/IM", "DouZaiNe.exe"], capture_output=True)
time.sleep(1)

si = subprocess.STARTUPINFO()
si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
si.wShowWindow = 1
subprocess.Popen([EXE], cwd=os.path.dirname(EXE), startupinfo=si,
                 creationflags=0x00000008 | 0x00000200, close_fds=True)
time.sleep(7)

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.FindWindowW.restype = w.HWND
user32.FindWindowW.argtypes = [w.LPCWSTR, w.LPCWSTR]
user32.GetWindowRect.restype = w.BOOL
user32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
user32.GetDC.restype = w.HDC
user32.GetDC.argtypes = [w.HWND]
gdi32.CreateCompatibleDC.restype = w.HDC
gdi32.CreateCompatibleDC.argtypes = [w.HDC]
gdi32.CreateCompatibleBitmap.restype = w.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = w.HGDIOBJ
gdi32.SelectObject.argtypes = [w.HDC, w.HGDIOBJ]
gdi32.BitBlt.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         ctypes.c_int, w.HDC, ctypes.c_int, ctypes.c_int, w.DWORD]
gdi32.GetDIBits.argtypes = [w.HDC, w.HBITMAP, w.UINT, w.UINT,
                            ctypes.c_void_p, ctypes.c_void_p, w.UINT]
gdi32.DeleteObject.argtypes = [w.HGDIOBJ]
gdi32.DeleteDC.argtypes = [w.HDC]
user32.ReleaseDC.argtypes = [w.HWND, w.HDC]

hwnd = user32.FindWindowW(None, "斗在呢 · 工位哨兵 · 离座检测")
if not hwnd:
    EnumProc = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    hits = []

    def cb(h, lp):
        n = user32.GetWindowTextLengthW(h)
        b = ctypes.create_unicode_buffer(n + 1)
        if n:
            user32.GetWindowTextW(h, b, n + 1)
            if "斗在呢" in b.value:
                hits.append((h, b.value))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    if not hits:
        raise SystemExit("没找到窗口")
    hwnd = hits[0][0]

user32.ShowWindow(hwnd, 9)
user32.SetForegroundWindow(hwnd)
time.sleep(1.5)

rect = w.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
width, height = rect.right - rect.left, rect.bottom - rect.top
x, y = rect.left, rect.top

src = user32.GetDC(0)
dst = gdi32.CreateCompatibleDC(src)
bmp = gdi32.CreateCompatibleBitmap(src, width, height)
gdi32.SelectObject(dst, bmp)
gdi32.BitBlt(dst, 0, 0, width, height, src, x, y, 0x00CC0020)


class BIH(ctypes.Structure):
    _fields_ = [("biSize", w.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                ("biPlanes", w.WORD), ("biBitCount", w.WORD), ("biCompression", w.DWORD),
                ("biSizeImage", w.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", w.DWORD),
                ("biClrImportant", w.DWORD)]


bi = BIH()
bi.biSize = ctypes.sizeof(BIH)
bi.biWidth = width
bi.biHeight = -height
bi.biPlanes = 1
bi.biBitCount = 32
bi.biCompression = 0
buf = ctypes.create_string_buffer(width * height * 4)
gdi32.GetDIBits(dst, bmp, 0, height, buf, ctypes.byref(bi), 0)
gdi32.DeleteObject(bmp)
gdi32.DeleteDC(dst)
user32.ReleaseDC(0, src)

raw = bytearray()
stride = width * 4
for i in range(height):
    row = buf.raw[i * stride:(i + 1) * stride]
    line = bytearray()
    for j in range(width):
        line += bytes((row[j * 4 + 2], row[j * 4 + 1], row[j * 4]))
    raw += b"\x00" + line


def chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff))


png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
png += chunk(b"IEND", b"")

with open(OUT, "wb") as f:
    f.write(png)
print("已保存:", OUT, f"{width}x{height}")
