#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bench_stream_impact.py — 证伪/证实「串流会抢走 CPU，检测必须上 NPU」这个前提。

为什么先做这个，而不是先做 NPU：
    「斗在吗」这个项目的全部价值，都挂在一句话上——
    「Moonlight 串流时 CPU 被解码占着，检测跑 CPU 会拖垮串流延迟」。

    但这句话是个假设，不是事实。980 的 CPU 本身能跑 60~100fps 级检测
    （TNN 实测 MobileNet v2 fp16 只要 14.81ms）。如果串流其实只占了
    大核的一部分、或者占了小核，那么：
        - 方案 A（纯 CPU + 绑小核）就足够，项目当天能落地
        - 方案 C（DDK/NPU）就是过度工程

    所以这个脚本做三组对照：
        baseline   空载（不串流）
        streaming  串流中，无额外负载
        streaming+load  串流中，注入可控 CPU 负载

    每组都记录：手机端 CPU 占用（分核）、Moonlight 侧端到端延迟/帧率、
    PC 侧 ping RTT 抖动（从手机打 PC，与串流同路径，最接近真实体感）。

用法:
    python bench/bench_stream_impact.py --duration 30
    python bench/bench_stream_impact.py --duration 20 --load-workers 2
    python bench/bench_stream_impact.py --stage baseline --stage streaming

依赖:
    adb（PATH 或 --adb）。不需要 root。
    Moonlight 需已在手机端连接 PC 并开始串流（脚本无法自动判断，
    会提示你确认）。
"""

import argparse
import json
import os
import re
import shlex
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

# --------------------------------------------------------------------------- #
# 基础工具
# --------------------------------------------------------------------------- #

def run(cmd: List[str], timeout: int = 30) -> str:
    """执行命令并返回 stdout（失败返回空串，不抛异常）。"""
    try:
        p = subprocess.run(cmd, capture_output=True, timeout=timeout)
        return p.stdout.decode("utf-8", "replace")
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return ""


class Adb:
    def __init__(self, adb: str = "adb", serial: Optional[str] = None):
        self.adb = adb
        self.serial = serial

    def _base(self) -> List[str]:
        c = [self.adb]
        if self.serial:
            c += ["-s", self.serial]
        return c

    def shell(self, cmd: str, timeout: int = 30) -> str:
        return run(self._base() + ["shell", cmd], timeout)

    def devices(self) -> List[str]:
        out = run(self._base() + ["devices"], timeout=15)
        return [l.split("\t")[0] for l in out.splitlines()
                if l.endswith("\tdevice") and not l.startswith("*")]

    def ok(self) -> bool:
        return bool(self.devices())

    def getprop(self, key: str) -> str:
        return self.shell(f"getprop {key}").strip()

    def cpu_times(self) -> Optional[List[int]]:
        """
        读取 /proc/stat 的 cpuN 行，返回每个核的累计 busy tick。
        用于计算分核占用率。
        """
        raw = self.shell("cat /proc/stat")
        res: List[int] = []
        for line in raw.splitlines():
            m = re.match(r"^cpu(\d+)\s+(.*)$", line.strip())
            if not m:
                continue
            fields = [int(x) for x in m.group(2).split() if x.isdigit()]
            if len(fields) < 4:
                continue
            # fields: user nice system idle iowait irq softirq steal ...
            busy = sum(fields) - fields[3] - fields[4]  # 减去 idle 与 iowait
            res.append(busy)
        return res or None

    def total_cpu(self) -> Optional[int]:
        raw = self.shell("cat /proc/stat")
        for line in raw.splitlines():
            if line.startswith("cpu "):
                f = [int(x) for x in line.split()[1:] if x.isdigit()]
                return sum(f) if f else None
        return None

    def loadavg(self) -> str:
        return self.shell("cat /proc/loadavg").strip()

    def rndis_ip(self) -> str:
        """
        找手机在 USB 共享网络里的地址（rndis0/eth0/usb0 等非 wlan0 接口）。
        这个地址最适合用来做 ping 抖动测量——同一条 RNDIS 链路。
        """
        out = self.shell("ip -o addr show 2>/dev/null")
        for line in out.splitlines():
            parts = line.split()
            if len(parts) < 4:
                continue
            iface = parts[1]
            if iface in ("lo", "wlan0"):
                continue
            if "inet" in parts:
                return parts[parts.index("inet") + 1].split("/")[0]
        return ""

    def net_rx_total(self) -> Optional[int]:
        """
        所有非回环接口的累计接收字节。
        注意：串流可能走 wlan0，也可能走 USB 共享网卡(rndis0/usb0)，
        所以必须统计所有接口，不能只看 wlan0。
        """
        raw = self.shell("cat /proc/net/dev")
        total = 0
        found = False
        for line in raw.splitlines():
            if ":" not in line:
                continue
            name, _, rest = line.partition(":")
            name = name.strip()
            if name == "lo":
                continue
            fields = rest.split()
            if len(fields) >= 1 and fields[0].isdigit():
                total += int(fields[0])
                found = True
        return total if found else None

    def net_rx_delta(self, seconds: int = 3) -> Optional[int]:
        """非回环接口在 seconds 内接收了多少字节。"""
        a = self.net_rx_total()
        if a is None:
            return None
        time.sleep(seconds)
        b = self.net_rx_total()
        if b is None:
            return None
        return max(0, b - a)

    def start_load(self, workers: int, cpus: Optional[str] = None) -> List[int]:
        """
        在手机上启动 N 个 CPU 密集型后台进程作为可控负载。

        ★ Android 的 taskset 是 toybox 版，语法和 GNU 版完全不同：
            GNU:   taskset -c 4,5,6,7 cmd
            toybox: taskset <hex_mask> cmd      # 掩码是十六进制！
          0x0f = cpu0-3（小核 A55）, 0xf0 = cpu4-7（大核 A76）, 0xff = 全部
          用 GNU 写法会报 "bad mask" 或 "Unknown option c"。

        注意 Android 的 sh 可能没有 /dev/null 之外的管道，这里只用 nohup + &。
        """
        pids: List[int] = []
        mask = self._cpu_list_to_mask(cpus)
        for _ in range(max(0, workers)):
            cmd = "while :; do :; done"
            if mask:
                cmd = f"taskset {mask} sh -c '{cmd}'"
            full = f"nohup sh -c \"{cmd}\" >/dev/null 2>&1 & echo $!"
            out = self.shell(full)
            found = 0
            for tok in out.split():
                if tok.isdigit():
                    pids.append(int(tok))
                    found = 1
                    break
            if not found:
                print(f"  [警告] 未能取得负载进程 PID（输出: {out.strip()[:80]}）")
            time.sleep(0.3)
        return pids

    @staticmethod
    def _cpu_list_to_mask(cpus: Optional[str]) -> str:
        """
        把 "4,5,6,7" 转成 toybox taskset 需要的十六进制掩码 "f0"。
        """
        if not cpus:
            return ""
        try:
            idx = [int(x) for x in cpus.replace(" ", "").split(",") if x != ""]
        except ValueError:
            return ""
        if not idx:
            return ""
        mask = 0
        for i in idx:
            mask |= (1 << i)
        return format(mask, "x")

    def load_alive(self, pids: List[int]) -> List[int]:
        """检查负载进程是否还活着（绑核失败时进程会立刻退出）。"""
        alive = []
        for p in pids:
            # /proc/<pid>/stat 的第 4 个字段是 comm，后面跟状态字符。
            # 进程存在时该行至少有 ')' 且状态是 R/S/D 等。
            out = self.shell(f"cat /proc/{p}/stat 2>/dev/null")
            if out and ")" in out:
                alive.append(p)
        return alive

    def stop_load(self, pids: List[int]) -> None:
        """
        清理负载进程。

        ★ 实测踩坑（很重要）：
          注入的是 `sh -c 'while :; do :; done'`。
          kill 掉这个 shell **不会**杀掉真正在跑的死循环子进程——
          它们会留在设备上各占满一个核，持续污染后续所有测量。

          实测中一次实验残留 3 个进程（各 ~98% CPU），
          导致后续测得的「串流占用」从 8.7% 跳到 42.7%，数据直接失真。

        所以这里改成：按 cmdline 特征扫出所有负载进程再 kill，
        而不是只 kill 我们记住的那几个 PID。
        """
        for p in pids:
            self.shell(f"kill -9 {p} 2>/dev/null")
        time.sleep(0.5)
        # 扫尾：干掉所有匹配的残留（含子进程）
        leftover = self.find_load_pids()
        if leftover:
            print(f"  [清理] 发现 {len(leftover)} 个残留负载进程，正在清除")
            for p in leftover:
                self.shell(f"kill -9 {p} 2>/dev/null")
            time.sleep(1.5)
            still = self.find_load_pids()
            if still:
                print(f"  [警告] 仍有残留 {still}，"
                      f"可手动执行: python probe/cleanup_load.py")
            else:
                print("  [清理] 残留已清空")

    def find_load_pids(self) -> List[int]:
        """
        扫描设备上所有负载进程。

        ★ 不用 `top | grep` —— 那个 grep 自己的命令行里也含 "while :"，
          会匹配到自己，导致 kill 时误杀自己的父 shell（实测被 SIGTERM 打断）。
          这里直接遍历 /proc/<pid>/cmdline，并用变量拼接模式串避免自匹配。
        """
        script = (
            'PAT="while"; PAT2=":"; '
            'for d in /proc/[0-9]*; do '
            'p=${d#/proc/}; [ "$p" = "$$" ] && continue; '
            'c=$(cat $d/cmdline 2>/dev/null | tr "\\000" " "); '
            'case "$c" in *"$PAT $PAT2"*) echo "$p";; esac; '
            'done'
        )
        raw = self.shell(script, timeout=60)
        pids = []
        for tok in raw.split():
            if tok.isdigit():
                pids.append(int(tok))
        return sorted(set(pids))

    def top_snapshot(self) -> List[Dict]:
        """解析 top -n 1 -b -o %CPU 的输出，取前若干进程。"""
        raw = self.shell("top -n 1 -b -o %CPU 2>/dev/null | head -30")
        rows = []
        for line in raw.splitlines():
            parts = line.split()
            if len(parts) < 9:
                continue
            if parts[0].upper() in ("PID", "USER", "CPU%"):
                continue
            try:
                pid = int(parts[0])
            except ValueError:
                continue
            rows.append({
                "pid": pid,
                "user": parts[1],
                "proc": parts[-1],
                "cpu": parts[-5] if len(parts) >= 5 else "?",
                "mem": parts[-4] if len(parts) >= 4 else "?",
            })
        return rows[:12]


# --------------------------------------------------------------------------- #
# PC 侧：Moonlight 延迟 + ping RTT
# --------------------------------------------------------------------------- #

def moonight_stats() -> Dict:
    """
    Moonlight（Android 客户端）会把统计打到 logcat，标签通常是 Moonlight / moonlight。
    统计项：latency（平均延迟）、fps（渲染帧率）、frames dropped。
    """
    adb = Adb(os.environ.get("ADB", "adb"))
    raw = adb.shell(
        "logcat -d -t 400 2>/dev/null | grep -iE 'Moonlight|ml.*fps|dropped' "
        "| tail -60"
    )
    out: Dict = {}
    lat = re.findall(r"(?:Average|avg)?\s*latency[^0-9\-]{0,12}(\d{1,4})\s*ms", raw, re.I)
    fps = re.findall(r"(\d{1,3}(?:\.\d)?)\s*fps", raw, re.I)
    drop = re.findall(r"(\d{1,5})\s*frames?\s*dropped", raw, re.I)
    if lat:
        out["latency_ms"] = [int(x) for x in lat]
    if fps:
        out["fps"] = [float(x) for x in fps]
    if drop:
        out["dropped"] = [int(x) for x in drop]
    return out


def ping_rtt(target_ip: str, count: int = 20, timeout: int = 40) -> Dict:
    """
    从手机 ping 目标，测 RTT 抖动。

    ★ target_ip 是【手机要 ping 的地址】。
      在 USB 共享网络下，正确做法是 ping 手机自己所在的 rndis0 地址
      （本机 192.168.42.129），这样测的是同一条 RNDIS 链路的往返特性。

    ★ 注意：Windows RNDIS 共享默认不透传 ICMP 到 PC 侧，
      所以 ping PC 的地址会 100% 丢包。ping 手机自己的地址则正常。
    """
    adb = Adb(os.environ.get("ADB", "adb"))
    raw = adb.shell(f"ping -c {count} -i 0.2 -W 2 {target_ip} 2>/dev/null", timeout=timeout)
    rtt = [float(x) for x in re.findall(r"time=([\d.]+)\s*ms", raw)]
    loss = 0
    m = re.search(r"(\d+)% packet loss", raw)
    if m:
        loss = int(m.group(1))
    if not rtt:
        return {"ok": False, "raw": raw[-200:], "loss_pct": loss,
                "hint": "ICMP 不通时改用 tcp_rtt 或看 Moonlight 自身统计"}
    return {
        "ok": True,
        "rtt_min": min(rtt),
        "rtt_avg": statistics.mean(rtt),
        "rtt_max": max(rtt),
        "rtt_stdev": statistics.pstdev(rtt) if len(rtt) > 1 else 0.0,
        "loss_pct": loss,
        "n": len(rtt),
    }


def tcp_rtt(pc_ip: str, port: int, count: int = 15, timeout: int = 60) -> Dict:
    """
    TCP 往返抖动测量（ICMP 被 USB 共享网络挡住时的替代方案）。

    ★ 方向很关键（实测踩坑）：
      Windows 防火墙默认拦截【入站】。实测手机主动连 PC 的常见端口
      （445 / 22 / 47984 / 47989 / 47990）全部失败。
      反过来【PC 主动连手机】是出站，允许通过。

      这恰好就是我们项目事件通道的方向（手机起 TCP server，PC 连过去），
      所以这个测量同时验证了通道可行性——一举两得。

    做法：手机侧用 toybox nc 监听，PC 反复连接并测 connect 往返耗时。
    """
    import socket as _socket
    import threading as _threading

    adb = Adb(os.environ.get("ADB", "adb"))

    # 1) 手机侧起监听
    adb.shell(f"nohup toybox nc -l -p {port} -w 30 >/dev/null 2>&1 & echo started",
              timeout=10)
    time.sleep(1.5)

    samples = []
    ok = 0
    for _ in range(count):
        t0 = time.perf_counter()
        try:
            s = _socket.create_connection((pc_ip, port), timeout=3)
            s.close()
            dt = (time.perf_counter() - t0) * 1000
            samples.append(dt)
            ok += 1
        except OSError:
            pass
        time.sleep(0.15)

    # 清理手机侧监听
    adb.shell(f"pkill -f 'nc -l -p {port}' 2>/dev/null")

    if ok == 0:
        return {"ok": False,
                "hint": "PC 未能连上手机（手机侧可能无 nc，或端口被拦）。"
                        "此时请依赖 Moonlight 自身的统计数字。"}
    return {
        "ok": True,
        "rtt_avg": statistics.mean(samples),
        "rtt_min": min(samples),
        "rtt_max": max(samples),
        "rtt_stdev": statistics.pstdev(samples) if len(samples) > 1 else 0.0,
        "n": ok,
        "method": "PC->手机 TCP connect 往返",
    }


# --------------------------------------------------------------------------- #
# 采样
# --------------------------------------------------------------------------- #

@dataclass
class Stage:
    name: str
    cpu_per_core: List[float] = field(default_factory=list)
    cpu_total: float = 0.0
    loadavg: str = ""
    top: List[Dict] = field(default_factory=list)
    rtt: Dict = field(default_factory=dict)
    moon: Dict = field(default_factory=dict)
    net_rx_rate: float = 0.0     # 串流码率 KB/s


def check_stream_active(adb: Adb, label: str) -> bool:
    """
    检查手机当前是否真的在串流。

    ★ 这个检查极其重要，踩过坑：
      Moonlight 的进程和窗口可以一直存在，但**并没有在串流**。
      此时 dumpsys cpuinfo 里的百分比是历史累计值，会严重误导基线。

    判据：所有非回环网卡的接收流量。
    注意串流可能走 wlan0，也可能走 USB 共享网卡(rndis0)，
    所以要统计全部接口，不能只看 wlan0。
    """
    delta = adb.net_rx_delta(seconds=3)
    if delta is None:
        print(f"  [{label}] 无法读取网卡流量")
        return False
    kbps = delta / 3.0 / 1024.0
    active = delta > 20000      # 3 秒内 >20KB 视为有数据流
    print(f"  [{label}] 非回环网卡 3 秒接收 {delta} 字节 ({kbps:.1f} KB/s) "
          f"-> {'★ 串流中' if active else '未串流'}")
    return active


def measure(adb: Adb, name: str, duration: int, pc_ip: str,
            interval: float = 1.0, tcp_port: int = 18765) -> Stage:
    """
    正确实现的采样：每秒取一次 /proc/stat，算出每个核的利用率（%）。
    """
    st = Stage(name=name)
    print(f"\n>>> 采样阶段: {name}  ({duration}s)")

    prev_c = adb.cpu_times()
    n_cores = len(prev_c) if prev_c else 0
    # 每核的 jiffies 总量需要单独记录，这里改用 top 的分核信息不现实，
    # 采用「busy 增量 / 全局总 tick 增量」近似为该核占整机算力的比例。
    prev_t = adb.total_cpu()

    deadline = time.time() + duration
    while time.time() < deadline:
        time.sleep(interval)
        cur_c = adb.cpu_times()
        cur_t = adb.total_cpu()
        if prev_c and cur_c and cur_t and prev_t and len(cur_c) == len(prev_c):
            d_total = cur_t - prev_t
            if d_total > 0:
                per_core = []
                for a, b in zip(prev_c, cur_c):
                    # 该核 busy 增量占整机总 tick 的比例 -> 折算成「占一个核的百分比」
                    per_core.append(round((b - a) * 100.0 / d_total, 1))
                st.cpu_per_core = per_core
                st.cpu_total = round(
                    sum((b - a) for a, b in zip(prev_c, cur_c)) * 100.0 / d_total, 1)
        prev_c, prev_t = cur_c, cur_t

    st.loadavg = adb.loadavg()
    st.top = adb.top_snapshot()
    # 串流码率（取样 3 秒）
    d = adb.net_rx_delta(seconds=3)
    if d is not None:
        st.net_rx_rate = round(d / 3.0 / 1024.0, 1)
    if pc_ip:
        print("    ping PC ...")
        st.rtt = ping_rtt(pc_ip)
        if not st.rtt.get("ok"):
            # USB 共享网络不透传 ICMP，改用 TCP 往返
            print("    ICMP 不通，改用 TCP 往返测量 ...")
            st.rtt = tcp_rtt(pc_ip, tcp_port)
            st.rtt["icmp_unreachable"] = True
    st.moon = moonight_stats()
    return st


# --------------------------------------------------------------------------- #
# 报告
# --------------------------------------------------------------------------- #

def fmt_stage(st: Stage) -> str:
    L = []
    L.append(f"\n=== {st.name} ===")
    if st.cpu_per_core:
        L.append("  分核占用（占整机总算力 %, 8 核合计应≈100%）:")
        for i, v in enumerate(st.cpu_per_core):
            bar = "#" * int(min(v, 100) / 4)
            L.append(f"    cpu{i}: {v:6.1f}%  {bar}")
        L.append(f"  合计: {st.cpu_total}%")
    if st.loadavg:
        L.append(f"  loadavg: {st.loadavg}")
    L.append(f"  串流接收速率: {st.net_rx_rate} KB/s")
    if st.top:
        L.append("  top 进程:")
        for p in st.top[:8]:
            L.append(f"    pid={p['pid']:<7} cpu={p['cpu']:>6}  {p['proc']}")
    if st.rtt.get("ok"):
        L.append(f"  ping PC: avg={st.rtt['rtt_avg']:.1f}ms  min={st.rtt['rtt_min']:.1f}  "
                 f"max={st.rtt['rtt_max']:.1f}  jitter(stdev)={st.rtt['rtt_stdev']:.1f}  "
                 f"loss={st.rtt['loss_pct']}%")
    else:
        L.append("  ping PC: 无数据（检查 PC_IP 是否可达 / 手机是否同网段）")
    if st.moon:
        if "latency_ms" in st.moon and st.moon["latency_ms"]:
            L.append(f"  Moonlight latency: {st.moon['latency_ms'][-1]} ms  "
                     f"(采样 {len(st.moon['latency_ms'])} 次)")
        if "fps" in st.moon and st.moon["fps"]:
            L.append(f"  Moonlight fps: {st.moon['fps'][-1]}")
        if "dropped" in st.moon and st.moon["dropped"]:
            L.append(f"  Moonlight dropped frames: {st.moon['dropped'][-1]}")
    return "\n".join(L)


def judge(stages: Dict[str, Stage]) -> str:
    """给出判定：串流到底抢了多少 CPU。"""
    L = ["", "=" * 66, "判定", "=" * 66]
    base = stages.get("baseline")
    strm = stages.get("streaming")
    load = stages.get("streaming+load")

    if not strm:
        L.append("  缺少 streaming 阶段，无法判定。")
        return "\n".join(L)

    used = strm.cpu_total
    L.append(f"  串流时整机 CPU 占用: {used}%")
    if base:
        L.append(f"  空载时整机 CPU 占用: {base.cpu_total}%")
        delta = used - base.cpu_total
        L.append(f"  串流净增:             {delta:+.1f}%")
    else:
        delta = None

    L.append("")
    if used < 35:
        L.append("  => 串流只用了不到三分之一的算力。")
        L.append("     CPU 侧完全有空间跑检测，NPU 不是必需品。")
        L.append("     下一步：做绑核实验（检测固定在 cpu0-3 小核，掩码 0x0f），")
        L.append("             验证延迟抖动是否真的可控。")
    elif used < 60:
        L.append("  => 串流占用了三分之一到六成的算力。")
        L.append("     CPU 检测仍有空间，但需要认真做绑核与降频策略。")
        L.append("     下一步：绑核实验 + 降低检测分辨率/帧率。")
    else:
        L.append("  => 串流吃掉了大部分算力。")
        L.append("     此时 NPU 才真正具有不可替代性，")
        L.append("     但要记住：Int8 走 NNAPI 不会到 NPU，需 DDK 离线模型。")
    if delta is not None and delta < 10:
        L.append("")
        L.append("  [注] 串流净增不足 10%，说明「串流抢 CPU」这个前提")
        L.append("        在你的实际配置下并不成立，方案 C 属于过度工程。")
    if load:
        L.append("")
        L.append(f"  注入负载阶段: 整机 CPU {load.cpu_total}%")
        if strm.rtt.get("ok") and load.rtt.get("ok"):
            d_avg = load.rtt["rtt_avg"] - strm.rtt["rtt_avg"]
            d_jit = load.rtt["rtt_stdev"] - strm.rtt["rtt_stdev"]
            L.append(f"    RTT   {strm.rtt['rtt_avg']:.1f} -> {load.rtt['rtt_avg']:.1f} ms ({d_avg:+.1f})")
            L.append(f"    抖动  {strm.rtt['rtt_stdev']:.1f} -> {load.rtt['rtt_stdev']:.1f} ms ({d_jit:+.1f})")
            if abs(d_jit) < 3 and abs(d_avg) < 5:
                L.append("    => 负载对网络路径影响很小，瓶颈不在 CPU。")
            else:
                L.append("    => 负载确实影响了延迟，NPU 方案有实证价值。")
    return "\n".join(L)


# --------------------------------------------------------------------------- #

def main() -> int:
    ap = argparse.ArgumentParser(description="串流 vs CPU 占用基准测试")
    ap.add_argument("--adb", default=os.environ.get("ADB", "adb"))
    ap.add_argument("--serial", default=None, help="多设备时指定 serial")
    ap.add_argument("--duration", type=int, default=20, help="每阶段采样秒数")
    ap.add_argument("--interval", type=float, default=1.0, help="采样间隔秒")
    ap.add_argument("--ping-target", default=os.environ.get("PING_TARGET", ""),
                    help="手机要 ping 的地址（用于测 RTT 抖动）。"
                         "留空则自动用手机在 USB 共享网里的 rndis0 地址")
    ap.add_argument("--load-workers", type=int, default=2,
                    help="注入负载阶段启用的 CPU 忙循环进程数")
    ap.add_argument("--load-cpus", default="0,1,2,3",
                    help="忙循环绑定的核。★ 本机实测 cpu0-3 = A55 小核，"
                         "cpu4-7 = A76 大核；检测负载应绑小核")
    ap.add_argument("--stage", action="append", default=None,
                    choices=["baseline", "streaming", "streaming+load"],
                    help="只跑指定阶段，可重复")
    ap.add_argument("--out", default="", help="结果 JSON 输出路径")
    args = ap.parse_args()

    adb = Adb(args.adb, args.serial)
    if not adb.ok():
        print("!! adb 未发现设备。请确认手机已连接并开启 USB 调试。")
        print("!! 可用设备:", adb.devices() or "(无)")
        return 2

    print("设备信息:")
    for k in ("ro.product.model", "ro.build.version.emui", "ro.build.version.release"):
        print(f"  {k} = {adb.getprop(k)}")

    # ping 目标：默认用手机自己的 rndis0 地址（测同一条 RNDIS 链路）
    ping_target = args.ping_target or adb.rndis_ip()
    if ping_target:
        print(f"  ping 目标 = {ping_target}（USB 共享网地址，用于测同链路抖动）")
    else:
        print("\n提示: 未能自动确定 ping 目标，RTT 抖动指标会缺失。")
        print("      可用 --ping-target <addr> 手动指定。")
        print("      手机端查看：adb shell 'ip -o addr show'")

    want = args.stage or ["baseline", "streaming", "streaming+load"]
    stages: Dict[str, Stage] = {}

    for name in want:
        if name == "streaming+load":
            print("\n[准备] 注入 CPU 负载 ...")
            pids = adb.start_load(args.load_workers, args.load_cpus)
            time.sleep(1.5)
            alive = adb.load_alive(pids)
            if not alive:
                print("  ⚠ 负载进程全部退出，绑核很可能失败。")
                print("    Android toybox 的 taskset 用十六进制掩码，不是 GNU 的 -c 列表。")
                print("    继续采样，但『注入负载』这一阶段的结论无效。")
            else:
                print(f"  已在手机启动 {len(alive)}/{len(pids)} 个忙循环 "
                      f"(cpus={args.load_cpus})")
            stages[name] = measure(adb, name, args.duration, ping_target, args.interval)
            adb.stop_load(pids)
            print(f"  已清理负载 {pids}")
        else:
            if name == "streaming":
                print("\n[提示] 请确保 Moonlight 已开始串流，保持 30 秒左右不要切走。")
                time.sleep(3)
            # ★ 采样前校验：这一阶段到底有没有在串流？
            active = check_stream_active(adb, name)
            if name == "streaming" and not active:
                print("  ⚠ 警告: 检测不到串流流量！")
                print("    可能原因：Moonlight 没在串流 / 串流走的是别的网卡 / 画面静止。")
                print("    这个阶段的数字不能作为『串流占用』的证据。")
                print("    提示：若走 USB 共享网络，流量在 rndis0 上（脚本已统计全部接口）。")
            stages[name] = measure(adb, name, args.duration, ping_target, args.interval)

    print("\n" + "=" * 66)
    for st in stages.values():
        print(fmt_stage(st))
    verdict = judge(stages)
    print(verdict)

    if args.out:
        payload = {
            "stages": {
                k: {
                    "cpu_per_core": v.cpu_per_core,
                    "cpu_total": v.cpu_total,
                    "loadavg": v.loadavg,
                    "top": v.top,
                    "rtt": v.rtt,
                    "moonlight": v.moon,
                } for k, v in stages.items()
            },
            "verdict": verdict,
        }
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        print(f"\n结果已写入: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
