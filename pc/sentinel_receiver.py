#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sentinel_receiver.py — PC 端「工位哨兵」事件消费器

手机端检测到「人离开工位」后，通过 TCP 把事件推给这个脚本，
本脚本负责在 PC 上执行动作（锁屏 / 暂停播放 / 弹通知 / 执行命令）。

为什么用 TCP 而不是「手机 push 到 PC」？
    手机主动连 PC 会被 Windows 防火墙拦、也会遇到 NAT 问题。
    反过来让 PC 通过 adb 反向隧道连手机，是最省事、零配置的做法：

        adb reverse tcp:8765 tcp:8765     # PC 的 8765 -> 手机的 8765
        python pc/sentinel_receiver.py --port 8765

    手机端只需在 localhost:8765 起一个 TCP 服务，PC 连过去读事件。
    本脚本默认同时支持这两种模式（--mode connect / --mode listen）。

协议：每行一个 JSON 对象，以 \\n 结尾。
    {"event": "presence", "present": false, "confidence": 0.93, "ts": 1730000000.12}
    {"event": "hello", "app": "sentinel", "model": "ssd_mobilenetv2", "backend": "ncnn-int8"}

用法：
    python pc/sentinel_receiver.py --adb            # 自动建立 adb reverse 并连接手机
    python pc/sentinel_receiver.py --listen --port 8765
    python pc/sentinel_receiver.py --action lock --action notify
"""

import argparse
import base64
import json
import os
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime
from typing import Dict, List, Optional

# --------------------------------------------------------------------------- #

def _config_path() -> str:
    """
    配置文件路径。

    ★ 打包成 exe 后，配置文件应该在 exe 旁边（用户要能编辑它），
      而不在 PyInstaller 的解包目录里（临时目录，重启就没了）。
    """
    if getattr(sys, "frozen", False):
        return os.path.join(os.path.dirname(sys.executable), "sentinel_config.json")
    return os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "sentinel_config.json")


# 配置文件路径（打包后指向 exe 旁边）
DEFAULT_CONFIG = _config_path()

BANNER = r"""
==================================================================
  工位哨兵 · PC 端事件消费器
  监听手机端推送的状态事件，执行本地动作。
==================================================================
"""


def load_config(path: str) -> Dict:
    """
    读取 JSON 配置文件。不存在或格式错误时返回空 dict（不报错中断）。

    配置文件格式（所有字段可选）：
        {
          "action": ["notify", "lock"],
          "trigger": "absent",
          "absent_seconds": 8.0,
          "present_seconds": 2.0,
          "cooldown": 30.0,
          "mode": "direct",
          "phone_host": "192.168.42.129",
          "dry_run": false
        }
    """
    if not path or not os.path.exists(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        if not isinstance(cfg, dict):
            print(f"配置文件格式错误（应为 JSON 对象）: {path}", file=sys.stderr)
            return {}
        return cfg
    except (json.JSONDecodeError, OSError) as e:
        print(f"配置文件读取失败: {path} ({e})", file=sys.stderr)
        return {}


def log(msg: str, tag: str = "") -> None:
    """打印一条带时间戳的日志。

    ★ tag 是可选的日志分类（ok / warn / err），
      ActionHandler 的默认 logger 会以 (msg, tag) 两参形式调用本函数，
      所以第二个参数必须存在，否则一收到手机端事件就会抛 TypeError。
    """
    ts = datetime.now().strftime("%H:%M:%S")
    prefix = f"[{tag}] " if tag else ""
    print(f"[{ts}] {prefix}{msg}", flush=True)


def adb(adb_path: str, args: list, timeout: int = 30) -> str:
    """执行 adb 命令并返回 stdout（失败返回空串，不抛异常）。"""
    try:
        p = subprocess.run([adb_path] + args, capture_output=True, timeout=timeout)
        return p.stdout.decode("utf-8", "replace")
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return ""


# --------------------------------------------------------------------------- #
# 动作实现
# --------------------------------------------------------------------------- #

class Actions:
    """一组本地动作。每个动作返回 (是否执行成功, 人类可读结果)。"""

    def __init__(self, dry_run: bool = False, cooldown: float = 3.0):
        self.dry_run = dry_run
        self.cooldown = cooldown
        self._last_fired: Dict[str, float] = {}
        # ★ 绕过冷却（仅手动测试用）。
        #   不能靠临时改 cooldown —— 手机端每秒的 presence 事件会调
        #   sync_handler 把 cooldown 重置回配置值，改了也会被覆盖。
        self.bypass_cooldown = False

    def _throttle(self, key: str) -> bool:
        """同 key 在 cooldown 内只允许触发一次（防抖）。"""
        if self.bypass_cooldown:
            return True
        now = time.time()
        if now - self._last_fired.get(key, 0.0) < self.cooldown:
            return False
        self._last_fired[key] = now
        return True

    def lock(self) -> tuple:
        if self.dry_run:
            return True, "dry-run: 未真正锁屏"
        # Windows 锁定工作站
        ok = subprocess.call(
            ["rundll32.exe", "user32.dll,LockWorkStation"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0
        return ok, "已锁定工作站" if ok else "LockWorkStation 调用失败"

    def pause_media(self) -> tuple:
        """
        暂停当前播放。

        ★ 为什么不用全局媒体键（keybd_event）：
          实测无效。发完 VK_MEDIA_PLAY_PAUSE，网易云仍在播放
          （进程 CPU 只从 0.516s 降到 0.359s —— 那 30% 是音频解码开销，
           剩下的是 UI 线程 / 歌词轮询这些后台活动，本来就在跑）。
          网易云没注册全局媒体快捷键，全局键发过去它不理。

        ★ 现在用 WM_APPCOMMAND 定向发送：
          网易云有个窗口类叫 "MediaPlayer SMTC window - {GUID}"，
          说明它支持 SMTC 媒体会话。往这个窗口发
          WM_APPCOMMAND(APPCOMMAND_MEDIA_PLAY_PAUSE) 就能控制它，
          不需要焦点，也不需要它注册全局快捷键。实测有效。

        兜底：找不到 SMTC 窗口时，退回全局媒体键
        （对注册了全局快捷键的应用还是管用的）。
        """
        if self.dry_run:
            return True, "dry-run: 未真正暂停"

        try:
            import ctypes
            import ctypes.wintypes as w

            u32 = ctypes.windll.user32
            k32 = ctypes.windll.kernel32

            WM_APPCOMMAND = 0x0319
            APPCOMMAND_MEDIA_PLAY_PAUSE = 14

            # --- 找目标进程 ---
            class PE32(ctypes.Structure):
                _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD),
                            ("th32ProcessID", w.DWORD),
                            ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                            ("th32ModuleID", w.DWORD), ("cntThreads", w.DWORD),
                            ("th32ParentProcessID", w.DWORD),
                            ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                            ("szExeFile", ctypes.c_char * 260)]

            k32.CreateToolhelp32Snapshot.restype = w.HANDLE
            u32.EnumWindows.restype = w.BOOL
            EnumProc = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)

            def find_smtc(process_names):
                pids = set()
                snap = k32.CreateToolhelp32Snapshot(0x2, 0)
                if not snap or snap == ctypes.c_void_p(-1).value:
                    return []
                try:
                    pe = PE32()
                    pe.dwSize = ctypes.sizeof(PE32)
                    if k32.Process32First(snap, ctypes.byref(pe)):
                        while True:
                            name = pe.szExeFile.decode("ascii", "replace")
                            for pn in process_names:
                                if name.lower().startswith(pn.lower()):
                                    pids.add(pe.th32ProcessID)
                            if not k32.Process32Next(snap, ctypes.byref(pe)):
                                break
                finally:
                    k32.CloseHandle(snap)

                hits = []

                def cb(h, lp):
                    pid = w.DWORD()
                    u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
                    if pid.value in pids:
                        cls = ctypes.create_unicode_buffer(256)
                        u32.GetClassNameW(h, cls, 256)
                        if "SMTC" in cls.value.upper():
                            hits.append(h)
                    return True

                u32.EnumWindows(EnumProc(cb), 0)
                return hits

            handles = find_smtc(["cloudmusic", "cloudmusicX", "qqmusic"])
            if handles:
                for h in handles:
                    lparam = (APPCOMMAND_MEDIA_PLAY_PAUSE << 16)
                    u32.PostMessageW(h, WM_APPCOMMAND, 0, lparam)
                return True, f"已向 SMTC 会话窗口发送 APPCOMMAND_MEDIA_PLAY_PAUSE（{len(handles)} 个）"

            # --- 兜底：全局媒体键 ---
            VK_MEDIA_PLAY_PAUSE = 0xB3
            KEYEVENTF_KEYUP = 0x0002
            u32.keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0)
            time.sleep(0.06)
            u32.keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_KEYUP, 0)
            return True, "未找到 SMTC 会话窗口，已退回全局媒体键"

        except Exception as e:  # noqa: BLE001
            return False, f"发送媒体指令失败: {e}"

    def notify(self, title: str, message: str) -> tuple:
        """
        弹出 Windows 通知。

        ★ 必须直接构造 XML 字符串，不能用 GetTemplateContent + GetElementsByTagName：

          实测报错：
            在枚举集合时出错: 集合已修改；可能无法执行枚举操作。
            不能对 Null 值表达式调用方法。
          原因：XmlDocument.GetElementsByTagName 返回的是【实时 NodeList】，
                AppendChild 会修改 DOM，NodeList 立刻失效，
                第二次索引拿到 null。结果只有第一行填上了，第二行空着——
                通知看起来「没有语义」（标题和正文都是同一句，或正文是空的）。

        ★ 还要注意：PowerShell 的非终止错误【不改变退出码】，
          所以用 subprocess.call 判断成败会得到假成功。
          这里加 -ErrorAction Stop 让错误变成终止错误，退出码才可信。
        """
        if self.dry_run:
            return True, f"dry-run: 未真正弹出通知"

        def esc(x: str) -> str:
            return (str(x).replace("&", "&amp;").replace("<", "&lt;")
                    .replace(">", "&gt;").replace('"', "&quot;"))

        # ToastGeneric 是 Windows 10/11 推荐的模板：
        # text[0] = 标题，text[1] = 正文
        xml = (
            "<toast><visual><binding template=\"ToastGeneric\">"
            f"<text>{esc(title)}</text>"
            f"<text>{esc(message)}</text>"
            "</binding></visual></toast>"
        )
        # Base64 传 XML，避开所有引号转义问题
        b64 = base64.b64encode(xml.encode("utf-8")).decode("ascii")

        ps = (
            "$ErrorActionPreference='Stop'; "
            "[Windows.UI.Notifications.ToastNotificationManager, "
            "Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null; "
            f"$xml=[System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{b64}')); "
            # ★ 必须用 WinRT 的 Windows.Data.Xml.Dom.XmlDocument，
            #   不是 System.Xml.XmlDocument —— 类型不匹配会报
            #   "无法将 System.Xml.XmlDocument 转换为 Windows.Data.Xml.Dom.XmlDocument"
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, "
            "ContentType=WindowsRuntime] | Out-Null; "
            "$doc=New-Object Windows.Data.Xml.Dom.XmlDocument; "
            "$doc.LoadXml($xml); "
            "$n=New-Object Windows.UI.Notifications.ToastNotification -ArgumentList $doc; "
            "[Windows.UI.Notifications.ToastNotificationManager]::"
            "CreateToastNotifier('Microsoft.Windows.SystemToast.Sentinel').Show($n)"
        )
        p = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", ps],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=25)
        if p.returncode != 0:
            err = (p.stderr or b"").decode("utf-8", "replace").strip()
            return False, (f"通知失败（exit={p.returncode}）"
                           + (f"：{err[:140]}" if err else ""))
        return True, f"已弹出通知：{title} / {message}"

    def exec_cmd(self, cmd: str) -> tuple:
        if self.dry_run:
            return True, f"dry-run: 未执行 {cmd}"
        try:
            p = subprocess.run(cmd, shell=True, capture_output=True, timeout=20)
            out = (p.stdout or b"").decode("utf-8", "replace")[:300]
            return p.returncode == 0, f"exit={p.returncode} {out}"
        except subprocess.TimeoutExpired:
            return False, "命令超时"

    def run(self, names: List[str], event: Dict) -> List[str]:
        msgs = []
        # ★ 标题和正文要分开，且各自说清一件事。
        #   之前标题和正文都是同一句，通知里就变成两行一样的字，
        #   看着像"没有语义"。
        title, body = self._title_body(event)
        for n in names:
            if not self._throttle(n):
                msgs.append(f"{n}: 节流跳过")
                continue
            try:
                if n == "lock":
                    ok, m = self.lock()
                elif n == "pause":
                    ok, m = self.pause_media()
                elif n == "notify":
                    ok, m = self.notify(title, body)
                elif n.startswith("exec:"):
                    ok, m = self.exec_cmd(n[5:])
                else:
                    ok, m = False, f"未知动作 {n}"
            except Exception as e:  # noqa: BLE001
                ok, m = False, f"异常: {e}"
            msgs.append(f"{n}: {'OK' if ok else 'FAIL'} {m}")
        return msgs

    @staticmethod
    def _title_body(event: Dict) -> tuple:
        """
        把事件翻译成 (通知标题, 通知正文)。

        ★ 标题说【发生了什么】，正文说【细节】。
          通知栏里会显示成：
            工位哨兵            ← 标题（应用名）
            人已离开工位         ← 正文第一行
            置信度 0.00 · 14:23  ← 正文第二行
        """
        kind = event.get("event")
        if kind == "presence":
            present = bool(event.get("present"))
            title = "人已回到工位" if present else "人已离开工位"
            bits = []
            conf = event.get("confidence")
            if conf is not None:
                try:
                    bits.append(f"置信度 {float(conf):.2f}")
                except (TypeError, ValueError):
                    pass
            ts = event.get("ts")
            if ts:
                try:
                    bits.append(datetime.fromtimestamp(
                        float(ts) / 1000.0).strftime("%H:%M:%S"))
                except (TypeError, ValueError, OSError):
                    pass
            body = "  ·  ".join(bits) if bits else "工位哨兵检测"
            return title, body

        if kind == "hello":
            return "工位哨兵已连接", f"手机 {event.get('device', '?')}"

        return "工位哨兵", json.dumps(event, ensure_ascii=False)[:180]


# --------------------------------------------------------------------------- #
# 事件处理
# --------------------------------------------------------------------------- #

class EventHandler:
    """
    把 presence 事件转成动作触发。

    ★ 触发模式（--trigger）：
        absent  = 人离开持续 absent_seconds 后触发（默认）
        present = 人回来持续 present_seconds 后触发
        both    = 两者都触发

    ★ 为什么 present 也要一个时间阈值：
        人回来时摄像头刚对上，往往先有几帧误检（置信度低）。
        用 present_seconds 过滤掉这种抖动，避免「人回来了→立刻又走→再回来」
        这种反复触发把 PC 折腾死。
    """

    def __init__(self, actions: Actions, trigger: str = "absent",
                 absent_seconds: float = 5.0, present_seconds: float = 2.0,
                 logger=None):
        self.actions = actions
        self.trigger = trigger
        self.trigger_on_absent = trigger in ("absent", "both")
        self.trigger_on_present = trigger in ("present", "both")
        self.absent_seconds = absent_seconds
        self.present_seconds = present_seconds
        # ★ 日志出口可注入。
        #   默认是模块级 log()（只 print 到 stdout），
        #   但 Web 控制台需要把日志写进它的日志列表——
        #   否则界面上看不到「动作执行成功/失败」这些关键信息。
        self.logger = logger or (lambda msg, tag="": log(msg, tag))
        self._absent_since: Optional[float] = None
        self._present_since: Optional[float] = None
        self._last_present: Optional[bool] = None
        # ★ 已因「人离开」触发过动作。
        #   没有这个标志的话：触发后 _absent_since 被清空，
        #   下一条 presence=false 事件又重新计时，1 秒后又判定「超过阈值」，
        #   于是每 2 秒重复触发一次。
        #   cooldown 只是掩盖了这个问题——一旦 cooldown 设为 0，
        #   就会真的疯狂锁屏。
        #   正确语义：一次「离开事件」只触发一次，人回来才重置。
        self._fired_absent = False
        self._fired_present = False
        # 上一次事件的状态（用于「只在变化时记日志」）
        self._was_absent = False
        # 手机端 PID（A/B 实验要用来绑核和统计）
        self.phone_pid: Optional[int] = None
        # 最近一次推理性能统计
        self.last_stats: Dict = {}
        # 手机端上报的检测参数（离开延迟两边是叠加的，必须知道对方配了什么）
        self.phone_config: Dict = {}

    def reset(self) -> None:
        """
        重置触发状态（重新武装）。

        ★ 用途：手动测试。
          `_fired_absent` 是全局的——如果之前已经因「人离开」触发过，
          后续的 presence=false 事件会被它拦下（这是防重复触发的机制）。
          手动测试时需要清掉它，否则点了没反应。
        """
        self._absent_since = None
        self._present_since = None
        self._fired_absent = False
        self._fired_present = False
        self._was_absent = False

    def on_event(self, event: Dict) -> None:
        kind = event.get("event")
        if kind == "hello":
            self.logger(f"手机端已连接: {json.dumps(event, ensure_ascii=False)}")
            # 记录 PID，A/B 实验要用它做绑核和统计
            self.phone_pid = event.get("pid")
            return
        if kind == "ping":
            return
        if kind == "stats":
            # 推理性能统计（A/B 实验数据源）
            self.last_stats = {
                "fps": event.get("fps"),
                "infer_ms": event.get("infer_ms"),
                "device": event.get("device"),
                "ts": event.get("ts"),
            }
            return
        if kind == "config":
            # 手机端把检测参数报上来。
            # PC 端必须知道手机端配的是什么，否则算错实际触发延迟
            # （两边的延迟是【叠加】关系，不是覆盖关系）。
            self.phone_config = event.get("config") or {}
            self.logger(f"手机端配置已同步: {json.dumps(self.phone_config, ensure_ascii=False)}")
            return
        if kind != "presence":
            self.logger(f"未知事件类型，忽略: {kind}")
            return

        present = bool(event.get("present"))
        now = time.time()
        self._last_present = present

        if present:
            self._absent_since = None
            # ★ 只在状态【变化】时记日志。
            #   注意顺序：必须先用旧的 _was_absent 判断，再把它清掉。
            #   （之前反过来了，导致「检测到人回来」永远打不出来）
            if self._was_absent:
                self.logger("检测到人回来")
            self._was_absent = False
            # 人回来了 → 重置「已触发」标志，允许下一次离开再触发
            if self._fired_absent:
                self._fired_absent = False
                self.logger("人回来了，重置离开触发状态")
            if self._present_since is None:
                self._present_since = now
                return
            elapsed = now - self._present_since
            if elapsed < self.present_seconds:
                return
            self._present_since = None
            if self.trigger_on_present and not self._fired_present:
                self._fired_present = True
                self.logger(f"人已回来 {elapsed:.1f}s（超过阈值 {self.present_seconds}s），触发动作")
                for m in self.actions.run(self.action_names, event):
                    self.logger("  " + m)
            return

        # 人不在
        self._present_since = None
        if self._fired_present:
            self._fired_present = False
            self.logger("人离开了，重置回来触发状态")
        # ★ 同上：只在状态变化时打，然后立刻标记为「已离开」，
        #   这样后续每秒的事件就不会重复打了。
        if not self._was_absent:
            self.logger("检测到人离开")
        self._was_absent = True
        if self._fired_absent:
            # 本次「离开」已经触发过了，静默等待状态变化
            self._absent_since = None
            return
        if self._absent_since is None:
            self._absent_since = now
            return

        elapsed = now - self._absent_since
        if elapsed < self.absent_seconds:
            return

        # 达到持续离开阈值
        self._absent_since = None
        if self.trigger_on_absent:
            self._fired_absent = True
            self.logger(f"人已离开 {elapsed:.1f}s（超过阈值 {self.absent_seconds}s），触发动作")
            for m in self.actions.run(self.action_names, event):
                self.logger("  " + m)
        else:
            self.logger(f"人已离开 {elapsed:.1f}s（trigger={self.trigger}，不触发动作）")


# --------------------------------------------------------------------------- #
# 传输层
# --------------------------------------------------------------------------- #

def adb_reverse(adb_path: str, serial: Optional[str], pc_port: int, phone_port: int) -> bool:
    """
    建立反向隧道：PC 的 pc_port -> 手机的 phone_port。

    ★ 端口不能相同：
      手机上的 phone_port 已经被 App 的 EventServer 占用了，
      如果 reverse 也绑同一个端口，会报 "cannot bind listener: Address already in use"。
      所以 PC 侧用另一个端口（默认 18765），转发到手机的 8765。

    ★ 真机实测（Android 10）：
      App 的 ServerSocket 默认绑 IPv6 通配 (::)，adb reverse 转发的是
      127.0.0.1（IPv4），两者对不上。所以【直连手机地址】更可靠，
      本脚本默认走 direct 模式。
    """
    cmd = [adb_path]
    if serial:
        cmd += ["-s", serial]
    cmd += ["reverse", f"tcp:{pc_port}", f"tcp:{phone_port}"]
    p = subprocess.run(cmd, capture_output=True)
    ok = p.returncode == 0
    if ok:
        log(f"adb reverse 建立成功: PC:{pc_port} -> 手机:{phone_port}")
    else:
        log(f"adb reverse 失败: {p.stderr.decode('utf-8', 'replace').strip()}")
    return ok


def detect_phone_ip(adb_path: str) -> str:
    """
    自动探测手机在 USB 共享网里的地址。

    ★ 关键：不能取 wlan0 —— 本机场景是 USB 共享网络（rndis0），
      手机没有连 WiFi 也没有插卡。必须取 rndis0/eth0/usb0 等非 wlan 接口。
    """
    raw = adb(adb_path, ["shell", "ip -o addr show 2>/dev/null"])
    for line in raw.splitlines():
        parts = line.split()
        if len(parts) < 4:
            continue
        iface = parts[1]
        if iface in ("lo", "wlan0"):
            continue
        if "inet" in parts:
            return parts[parts.index("inet") + 1].split("/")[0]
    return ""


def connect_phone(port: int, host: str = "127.0.0.1", timeout: float = 10.0):
    """连到手机上的 EventServer。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((host, port))
    s.settimeout(None)
    return s


def serve_listen(port: int, handler: EventHandler):
    """PC 端监听，等手机连进来。"""
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", port))
    srv.listen(1)
    log(f"监听 0.0.0.0:{port}，等待手机连接…")
    conn, addr = srv.accept()
    log(f"手机已连接: {addr}")
    pump(conn, handler)


def pump(conn: socket.socket, handler: EventHandler):
    buf = b""
    while True:
        try:
            chunk = conn.recv(4096)
        except (ConnectionResetError, OSError):
            log("连接断开")
            return
        if not chunk:
            log("连接已关闭")
            return
        buf += chunk
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line.decode("utf-8", "replace"))
            except json.JSONDecodeError:
                log(f"非 JSON 行，忽略: {line[:120]!r}")
                continue
            handler.on_event(event)


def main() -> int:
    ap = argparse.ArgumentParser(description="工位哨兵 · PC 端事件消费器")
    ap.add_argument("--adb", default=os.environ.get("ADB", "adb"))
    ap.add_argument("--serial", default=None)
    ap.add_argument("--port", type=int, default=8765,
                    help="手机端 EventServer 监听的端口")
    ap.add_argument("--pc-port", type=int, default=18765,
                    help="PC 侧监听端口（adb reverse 用）。"
                         "★ 不能和 --port 相同：手机上的端口已被 App 占用")
    ap.add_argument("--mode", choices=["direct", "adb", "listen"], default="direct",
                    help="direct=直连手机(USB共享网，推荐); "
                         "adb=经 adb reverse 隧道; listen=PC 监听等手机连")
    ap.add_argument("--phone-host", default=os.environ.get("PHONE_HOST", ""),
                    help="手机在 USB 共享网里的地址，如 192.168.42.129。"
                         "留空则自动探测")
    ap.add_argument("--action", action="append", default=None,
                    metavar="ACT",
                    help="可重复。取值: lock | pause | notify | "
                         "exec:<命令>（如 exec:taskkill /IM notepad.exe）。"
                         "默认: notify")
    ap.add_argument("--trigger", choices=["absent", "present", "both"], default="absent",
                    help="什么状态触发动作。"
                         "absent=人离开(默认) / present=人回来 / both=两者都触发")
    ap.add_argument("--absent-seconds", type=float, default=0.0,
                    help="PC 端额外的「人离开」缓冲秒数。"
                         "默认 0 = 信任手机端的 8 秒状态机确认。"
                         "调大可更保守（总延迟 = 手机端 8s + 此值）")
    ap.add_argument("--present-seconds", type=float, default=2.0,
                    help="人回来持续多久才触发 present 动作")
    ap.add_argument("--cooldown", type=float, default=10.0,
                    help="同一动作的最小间隔秒数")
    ap.add_argument("--dry-run", action="store_true", help="只打印不执行")
    ap.add_argument("--config", default=DEFAULT_CONFIG,
                    help=f"配置文件路径（JSON）。命令行参数会覆盖它。"
                         f"默认: {DEFAULT_CONFIG}")
    args = ap.parse_args()

    # ★ 配置文件 → 命令行覆盖
    cfg = load_config(args.config)
    if cfg:
        if args.action is None and "action" in cfg:
            args.action = cfg["action"]
        if "--trigger" not in sys.argv and "trigger" in cfg:
            args.trigger = cfg["trigger"]
        if "--absent-seconds" not in sys.argv and "absent_seconds" in cfg:
            args.absent_seconds = float(cfg["absent_seconds"])
        if "--present-seconds" not in sys.argv and "present_seconds" in cfg:
            args.present_seconds = float(cfg["present_seconds"])
        if "--cooldown" not in sys.argv and "cooldown" in cfg:
            args.cooldown = float(cfg["cooldown"])
        if not args.dry_run and cfg.get("dry_run"):
            args.dry_run = True
        if cfg.get("mode"):
            args.mode = cfg["mode"]
        if cfg.get("phone_host"):
            args.phone_host = cfg["phone_host"]
        log(f"已加载配置文件: {args.config}")

    print(BANNER)

    actions = Actions(dry_run=args.dry_run, cooldown=args.cooldown)
    action_names = args.action or ["notify"]

    # ★ 动作名校验：只允许 lock / pause / notify / exec:<命令>
    #   不带命令的 "exec" 视为配置错误，直接报错——
    #   否则会变成空命令 `exec:`，执行 `subprocess.run("")` 静默成功，
    #   用户还以为动作跑了。
    cleaned: List[str] = []
    for n in action_names:
        if n == "exec":
            print("动作 'exec' 必须带命令，例如：--action 'exec:taskkill /IM notepad.exe'",
                  file=sys.stderr)
            return 2
        if n.startswith("exec:"):
            if not n[5:].strip():
                print("动作 'exec:' 后面的命令为空", file=sys.stderr)
                return 2
            cleaned.append(n)
            continue
        if n not in ("lock", "pause", "notify"):
            print(f"未知动作: {n}（可用: lock / pause / notify / exec:<命令>）",
                  file=sys.stderr)
            return 2
        cleaned.append(n)
    action_names = cleaned

    handler = EventHandler(actions,
                           trigger=args.trigger,
                           absent_seconds=args.absent_seconds,
                           present_seconds=args.present_seconds)
    handler.action_names = action_names  # type: ignore[attr-defined]

    log(f"动作集: {action_names}")
    log(f"触发条件: trigger={args.trigger} "
        f"离开>={args.absent_seconds}s" +
        (f" 回来>={args.present_seconds}s" if args.trigger in ("present", "both") else ""))
    if args.dry_run:
        log("DRY-RUN 模式：不执行真实动作")

    try:
        if args.mode == "direct":
            # ★ 直连模式（推荐）。
            #   实测：手机在 USB 共享网里的地址（rndis0）可直接连上
            #   App 的 EventServer，不需要任何隧道。
            host = args.phone_host or detect_phone_ip(args.adb)
            if not host:
                log("!! 探测不到手机地址。请用 --phone-host 指定"
                    "（手机端跑 `ip -o addr show` 查看 rndis0 地址）")
                return 2
            log(f"直连模式: {host}:{args.port}")
            for attempt in range(30):
                try:
                    conn = connect_phone(args.port, host)
                    log(f"已连上手机 (第 {attempt + 1} 次尝试)")
                    pump(conn, handler)
                    return 0
                except (ConnectionRefusedError, socket.timeout, OSError) as e:
                    if attempt == 0:
                        log(f"连接失败({e.__class__.__name__})，重试中…"
                            f"（确认手机端 App 已启动）")
                    time.sleep(1.0)
            log("30 次重试后仍未连上手机。")
            return 1

        elif args.mode == "adb":
            # ★ 建立隧道：PC 的 pc_port -> 手机的 port
            if not adb_reverse(args.adb, args.serial, args.pc_port, args.port):
                log("adb reverse 失败，改为直连模式")
                host = args.phone_host or detect_phone_ip(args.adb)
                if not host:
                    log("!! 探测不到手机地址，无法直连。")
                    return 2
                log(f"直连模式: {host}:{args.port}")
                for attempt in range(30):
                    try:
                        conn = connect_phone(args.port, host)
                        log(f"已连上手机 (第 {attempt + 1} 次尝试)")
                        pump(conn, handler)
                        return 0
                    except (ConnectionRefusedError, socket.timeout, OSError):
                        time.sleep(1.0)
                log("30 次重试后仍未连上手机。")
                return 1
            # 连 PC 本地的 pc_port，隧道另一端就是手机的 EventServer
            for attempt in range(30):
                try:
                    conn = connect_phone(args.pc_port)
                    log(f"已连上手机 (第 {attempt + 1} 次尝试)")
                    pump(conn, handler)
                    return 0
                except (ConnectionRefusedError, socket.timeout, OSError):
                    time.sleep(1.0)
            log("30 次重试后仍未连上手机。请确认手机端 App 已启动且服务已开启。")
            return 1
        else:
            serve_listen(args.pc_port, handler)
            return 0
    except KeyboardInterrupt:
        log("\n收到 Ctrl+C，退出")
        return 0


if __name__ == "__main__":
    sys.exit(main())
