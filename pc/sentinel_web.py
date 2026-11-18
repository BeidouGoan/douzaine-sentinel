#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sentinel_web.py — 工位哨兵 PC 端可视化控制台（本地 Web 界面）

★ 为什么用 Web 界面而不是 tkinter：
    1) 本机 Python 没编译 tkinter（ModuleNotFoundError），
       而 tkinter 是 C 扩展，不能用 pip 装。
    2) Web 界面只用标准库 http.server，零额外依赖，打包 exe 更小。
    3) HTML/CSS 能做得比 tkinter 好看得多——尤其是要做实时状态卡片。

★ 架构：
    后端（这个文件）         前端（内嵌的 HTML）
    ├─ 事件消费线程  ──────→  /api/state 轮询
    ├─ HTTP API             ├─ 配置读写
    └─ 状态聚合             └─ 日志流

用法：
    python pc/sentinel_web.py              # 启动后浏览器会自动打开
    python pc/sentinel_web.py --no-browser # 不自动开浏览器
    python pc/sentinel_web.py --port 8766
    python pc/sentinel_web.py --dry-run    # 演练模式

打包成 exe：
    python pc/build_exe.py
"""

import argparse
import json
import os
import queue
import socket
import sys
import threading
import time
import webbrowser
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sentinel_receiver import (  # noqa: E402
    Actions, EventHandler, connect_phone, detect_phone_ip, load_config,
    DEFAULT_CONFIG,
)

HERE = os.path.dirname(os.path.abspath(__file__))


def resource_dir() -> str:
    """
    资源目录。

    ★ 打包成 exe 后 __file__ 指向解包目录（sys._MEIPASS），
      而用户可编辑的配置文件应该在 exe 旁边。
      所以分开处理：
        - 静态资源（index.html）→ 解包目录
        - 配置文件            → exe 旁边
    """
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    return HERE


def config_path() -> str:
    """配置文件路径：优先 exe / 脚本旁边的那个（用户可编辑）。"""
    if getattr(sys, "frozen", False):
        beside = os.path.join(os.path.dirname(sys.executable), "sentinel_config.json")
        if os.path.exists(beside):
            return beside
        return os.path.join(os.path.dirname(sys.executable), "sentinel_config.json")
    return DEFAULT_CONFIG

# --------------------------------------------------------------------------- #
# 状态聚合
# --------------------------------------------------------------------------- #


class SentinelState:
    """全局状态 + 事件消费。HTTP 线程和事件线程都访问它。"""

    def __init__(self, cfg_path: str, dry_run: bool = False):
        self.lock = threading.Lock()
        self.cfg_path = cfg_path
        self.cfg = load_config(cfg_path)

        self.connected = False
        self.phone_info: dict = {}
        self.phone_config: dict = {}
        self.last_stats: dict = {}
        self.present: bool | None = None
        self.absent_since: float | None = None
        self.logs: list = []
        self.max_logs = 500

        self.conn: socket.socket | None = None
        self.running = False
        self.dry_run = dry_run

        self.actions = Actions(dry_run=dry_run, cooldown=30.0)
        # ★ 注入 logger：EventHandler 里的日志要进 Web 的日志列表，
        #   否则界面上看不到「动作执行成功/失败」这些关键信息。
        self.handler = EventHandler(self.actions, trigger="absent",
                                    absent_seconds=0.0, present_seconds=1.0,
                                    logger=self.log)

    # ---------------- 日志 ---------------- #

    def log(self, msg: str, tag: str = "") -> None:
        ts = datetime.now().strftime("%H:%M:%S")
        with self.lock:
            self.logs.append({"ts": ts, "msg": msg, "tag": tag})
            if len(self.logs) > self.max_logs:
                self.logs = self.logs[-self.max_logs:]

    # ---------------- 配置 ---------------- #

    def save_config(self, cfg: dict) -> None:
        with open(self.cfg_path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
        with self.lock:
            self.cfg = cfg

    def current_config(self) -> dict:
        with self.lock:
            return dict(self.cfg)

    def sync_handler(self, cfg: dict) -> None:
        """把配置应用到 handler（每次触发前调用）。"""
        acts = cfg.get("action") or ["notify"]
        self.handler.action_names = acts
        self.handler.trigger = cfg.get("trigger", "absent")
        self.handler.trigger_on_absent = self.handler.trigger in ("absent", "both")
        self.handler.trigger_on_present = self.handler.trigger in ("present", "both")
        self.handler.absent_seconds = float(cfg.get("absent_seconds", 0))
        self.handler.present_seconds = float(cfg.get("present_seconds", 1))
        self.actions.cooldown = float(cfg.get("cooldown", 30))
        self.actions.dry_run = bool(cfg.get("dry_run", self.dry_run))

    # ---------------- 状态快照 ---------------- #

    def snapshot(self) -> dict:
        with self.lock:
            return {
                "connected": self.connected,
                "phone": dict(self.phone_info),
                "phone_config": dict(self.phone_config),
                "stats": dict(self.last_stats),
                "present": self.present,
                "dry_run": self.actions.dry_run,
                "config": dict(self.cfg),
            }

    def recent_logs(self, since: int = 0) -> list:
        with self.lock:
            return self.logs[since:]

    # ---------------- 连接 ---------------- #

    def connect(self) -> bool:
        if self.running:
            return True
        host = self.current_config().get("phone_host") or detect_phone_ip("adb")
        if not host:
            self.log("探测不到手机地址，请用 adb 连接手机后重试", "err")
            return False
        self.log(f"正在连接 {host}:8765 …")

        def worker():
            for attempt in range(30):
                if not self.running:
                    return
                try:
                    conn = connect_phone(8765, host)
                    self.conn = conn
                    self.running = True
                    with self.lock:
                        self.connected = True
                    self.log(f"已连上手机（第 {attempt + 1} 次尝试）", "ok")
                    self._pump(conn)
                    return
                except Exception as e:  # noqa: BLE001
                    if attempt == 0:
                        self.log(f"连接失败（{type(e).__name__}），重试中…", "warn")
                    time.sleep(1.0)
            self.log("30 次重试后仍未连上手机", "err")

        self.running = True
        threading.Thread(target=worker, daemon=True).start()
        return True

    def disconnect(self) -> None:
        self.running = False
        try:
            if self.conn:
                self.conn.close()
        except OSError:
            pass
        self.conn = None
        with self.lock:
            self.connected = False
        self.log("已断开")

    def _pump(self, conn: socket.socket) -> None:
        buf = b""
        try:
            while self.running:
                chunk = conn.recv(4096)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        ev = json.loads(line.decode("utf-8", "replace"))
                    except json.JSONDecodeError:
                        continue
                    self._handle(ev)
        except Exception as e:  # noqa: BLE001
            self.log(f"连接断开：{e}", "warn")
        finally:
            with self.lock:
                self.connected = False
            self.conn = None

    def _handle(self, ev: dict) -> None:
        kind = ev.get("event")
        if kind == "hello":
            with self.lock:
                self.phone_info = ev
            self.log(f"手机端已连接：{ev.get('device')} (pid {ev.get('pid')})", "ok")
        elif kind == "stats":
            with self.lock:
                self.last_stats = ev
        elif kind == "config":
            with self.lock:
                self.phone_config = ev.get("config", {}) or {}
            c = self.phone_config
            self.log(f"手机端配置：离开 {c.get('absent_seconds')}s · "
                     f"阈值 {c.get('threshold')} · 绑核 {c.get('pin_cpu')} · "
                     f"{c.get('fps')}fps")
        elif kind == "presence":
            present = bool(ev.get("present"))
            with self.lock:
                changed = (self.present is not None
                           and self.present != present)
                self.present = present
            # ★ 只在状态【变化】时记日志。
            #   手机端每秒发一次 presence 快照（PC 端靠它推进计时），
            #   如果每次都记，日志会被「检测到人离开」刷屏，
            #   真正有用的信息全被淹没。
            if changed:
                self.log("检测到人回来" if present else "检测到人离开",
                         "ok" if present else "warn")
            try:
                self.sync_handler(self.current_config())
                self.handler.on_event(ev)
            except Exception as e:  # noqa: BLE001
                self.log(f"动作执行异常：{e}", "err")


# --------------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------------- #


def make_handler(state: SentinelState):
    index_html = load_index_html()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):  # 静音默认日志
            pass

        def _send(self, code: int, body: bytes, ctype: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def _json(self, obj) -> None:
            self._send(200, json.dumps(obj, ensure_ascii=False).encode("utf-8"),
                       "application/json; charset=utf-8")

        def do_GET(self):  # noqa: N802
            u = urlparse(self.path)
            if u.path in ("/", "/index.html"):
                self._send(200, index_html, "text/html; charset=utf-8")
            elif u.path == "/api/state":
                q = parse_qs(u.query)
                since = int(q.get("since", ["0"])[0])
                snap = state.snapshot()
                snap["logs"] = state.recent_logs(since)
                snap["log_cursor"] = len(state.logs)
                self._json(snap)
            else:
                self._send(404, b"not found", "text/plain")

        def do_POST(self):  # noqa: N802
            u = urlparse(self.path)
            n = int(self.headers.get("Content-Length", 0))
            raw = self.rfile.read(n) if n else b"{}"
            try:
                data = json.loads(raw.decode("utf-8", "replace"))
            except json.JSONDecodeError:
                self._json({"ok": False, "error": "JSON 解析失败"})
                return

            if u.path == "/api/config":
                state.save_config(data)
                state.sync_handler(data)
                state.log("配置已保存", "ok")
                self._json({"ok": True})
            elif u.path == "/api/connect":
                if data.get("action") == "disconnect":
                    state.disconnect()
                else:
                    state.connect()
                self._json({"ok": True})
            elif u.path == "/api/test":
                # 模拟一次完整的「人离开」，用来验证动作链路。
                # ★ 必须发两次：
                #   EventHandler 的计时是【事件驱动】的——第一条 presence=false
                #   只是开始计时，要等第二条才能检查是否超过阈值。
                #   只发一次的话永远不会触发，看起来像"按钮坏了"。
                cfg = state.current_config()
                state.sync_handler(cfg)
                state.log("手动触发一次「人离开」测试事件", "warn")
                # ★ 先重置：_fired_absent 是防重复触发的全局标志，
                #   如果之前已经因「人离开」触发过，不重置的话这次测试会被拦下，
                #   表现为「点了没反应」。
                state.handler.reset()
                # ★ 测试时绕过 cooldown：否则刚触发过一次就点测试，
                #   会显示「节流跳过」，让人以为坏了。
                #   注意不能用「临时改 cooldown」——手机端每秒的 presence 事件
                #   会调 sync_handler 把它重置回配置值。
                state.actions.bypass_cooldown = True
                ev = {"event": "presence", "present": False,
                      "confidence": 0.0, "ts": time.time() * 1000}
                state.handler.on_event(ev)
                wait = max(float(cfg.get("absent_seconds", 0)), 0.5)
                time.sleep(wait)
                ev2 = {"event": "presence", "present": False,
                       "confidence": 0.0, "ts": time.time() * 1000}
                state.handler.on_event(ev2)
                state.actions.bypass_cooldown = False
                self._json({"ok": True})
            else:
                self._json({"ok": False, "error": "未知接口"})

    return Handler


def load_index_html() -> bytes:
    """读前端页面；缺失时返回一段提示。"""
    p = os.path.join(resource_dir(), "web", "index.html")
    if os.path.exists(p):
        with open(p, "rb") as f:
            return f.read()
    return b"<h1>UI file missing: web/index.html</h1>"


# --------------------------------------------------------------------------- #


def main() -> int:
    ap = argparse.ArgumentParser(description="斗在呢 · PC 可视化控制台")
    ap.add_argument("--port", type=int, default=8766, help="本地界面端口")
    ap.add_argument("--config", default=DEFAULT_CONFIG)
    ap.add_argument("--dry-run", action="store_true", help="演练模式")
    ap.add_argument("--no-browser", action="store_true", help="不自动打开浏览器")
    args = ap.parse_args()

    state = SentinelState(args.config, dry_run=args.dry_run)
    state.log("斗在呢控制台已启动", "ok")

    srv = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(state))
    url = f"http://127.0.0.1:{args.port}"
    print(f"界面地址: {url}")
    print("按 Ctrl+C 退出")

    if not args.no_browser:
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n退出")
    finally:
        state.disconnect()
        srv.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
