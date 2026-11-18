#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
measure_detect.py — 测量斗在吗在【三种配置】下的 CPU 占用与推理速度。

三组配置（对应 A/B 对照实验）：

    A  无检测          —— 基线：串流自己占多少 CPU
    B  检测·不绑核     —— 检测跑在大核上，看它抢了多少
    C  检测·绑小核     —— 检测绑到 cpu0-3，看是否让出大核

★ 本脚本【不负责启停串流】，只负责测量。
   串流由人在手机上操作，脚本会检测流量来确认是否在串流。

用法：
    python bench/measure_detect.py --mode all
    python bench/measure_detect.py --mode detect --duration 15
"""

import argparse
import json
import os
import re
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

DEFAULT_ADB = os.environ.get("ADB", "adb")


# --------------------------------------------------------------------------- #
# adb 封装
# --------------------------------------------------------------------------- #

def adb(adb_path: str, args: list, timeout: int = 30) -> str:
    try:
        p = subprocess.run([adb_path] + args, capture_output=True, timeout=timeout)
        return p.stdout.decode("utf-8", "replace")
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return ""


def sh(adb_path: str, cmd: str, timeout: int = 30) -> str:
    return adb(adb_path, ["shell", cmd], timeout)


# --------------------------------------------------------------------------- #
# 采样
# --------------------------------------------------------------------------- #

@dataclass
class Sample:
    label: str = ""
    cpu_per_core: List[float] = field(default_factory=list)
    cpu_total: float = 0.0
    net_rx_kbps: float = 0.0
    infer_ms: List[float] = field(default_factory=list)
    det_fps: List[float] = field(default_factory=list)
    has_person: Optional[bool] = None
    stream_active: bool = False
    app_cpu_pct: float = 0.0


def cpu_times(adb_path: str) -> Optional[tuple]:
    """
    读 /proc/stat，返回 (每核 busy tick 列表, 全局总 tick)。

    ★ 必须同时拿到 busy 和 total：
      busy = sum(all) - idle - iowait
      total = sum(all)
      利用率 = Δbusy / Δtotal
    之前只返回 busy，用 Δbusy/Δbusy 去算，永远等于 100%。这是真 bug。
    """
    raw = sh(adb_path, "cat /proc/stat")
    per_core = []
    total_all = 0
    for line in raw.splitlines():
        m = re.match(r"^cpu(\d+)\s+(.*)$", line.strip())
        if not m:
            continue
        f = [int(x) for x in m.group(2).split() if x.isdigit()]
        if len(f) < 4:
            continue
        per_core.append(sum(f) - f[3] - f[4])   # busy
        total_all += sum(f)                        # total
    return (per_core, total_all) if per_core else None


def net_rx_total(adb_path: str) -> Optional[int]:
    raw = sh(adb_path, "cat /proc/net/dev")
    total = 0
    found = False
    for line in raw.splitlines():
        if ":" not in line:
            continue
        name, _, rest = line.partition(":")
        if name.strip() == "lo":
            continue
        f = rest.split()
        if f and f[0].isdigit():
            total += int(f[0])
            found = True
    return total if found else None


def app_cpu_pct(adb_path: str) -> float:
    """
    取 App 进程的 CPU 占用（%）。

    ★ 同样不用 `top | grep`：管道在多层 shell 传递里不可靠。
      改成取 top 全量输出，在 Python 侧过滤。
    """
    raw = adb(adb_path, ["shell", "top", "-b", "-n", "1",
                        "-o", "PID,%CPU,CMDLINE", "-n", "400"], timeout=25)
    for line in raw.splitlines():
        if "com.yq.sentinel" not in line:
            continue
        # 排除 top 自己和 grep 自己的命令行
        if "grep" in line or line.strip().startswith("top"):
            continue
        m = re.search(r"\s(\d+(?:\.\d+)?)\s+com\.yq\.sentinel", line)
        if m:
            return float(m.group(1))
    return 0.0


def app_running(adb_path: str) -> bool:
    raw = sh(adb_path, "ps -A", timeout=15)
    return "com.yq.sentinel" in raw


def logcat_stats(adb_path: str) -> tuple:
    """
    从 logcat 里取最近一段的推理耗时与检测 FPS。

    ★ 不要用 `logcat -d -t N`：
      相机 HAL 的 EnvImpl 日志刷得极快（每秒几十条），
      -t 300 的窗口里全是它的日志，我们的行被挤出去了。
      改成按 tag 精确过滤（logcat -s TAG:V），由设备端做筛选。
    """
    raw = adb(adb_path, [
        "shell", "logcat", "-d", "-t", "2000",
        "-s", "Sentinel:V", "CpuAffinity:V", "PersonDetector:V",
        "CameraFrame:V", "SentinelSvc:V",
    ], timeout=25)
    infer = [int(m) for m in re.findall(r"推理=(\d+)ms", raw)]
    fps = [float(m) for m in re.findall(r"FPS ([\d.]+)", raw)]
    person = None
    if "有人=true" in raw:
        person = True
    elif "有人=false" in raw:
        person = False
    return infer, fps, person


def measure(adb_path: str, label: str, duration: int) -> Sample:
    s = Sample(label=label)
    print(f"\n>>> 采样: {label} ({duration}s)")

    n1 = net_rx_total(adb_path)
    prev = cpu_times(adb_path)
    deadline = time.time() + duration
    while time.time() < deadline:
        time.sleep(1.0)
        cur = cpu_times(adb_path)
        if prev and cur and len(prev[0]) == len(cur[0]):
            d_total = cur[1] - prev[1]
            if d_total > 0:
                s.cpu_per_core = [round((b - a) * 100.0 / d_total, 1)
                                 for a, b in zip(prev[0], cur[0])]
                s.cpu_total = round(
                    sum(b - a for a, b in zip(prev[0], cur[0])) * 100.0 / d_total, 1)
        prev = cur

    n2 = net_rx_total(adb_path)
    if n1 is not None and n2 is not None:
        s.net_rx_kbps = round((n2 - n1) / max(1, duration) / 1024.0, 1)
        s.stream_active = (n2 - n1) > 20000

    s.app_cpu_pct = app_cpu_pct(adb_path)
    s.infer_ms, s.det_fps, s.has_person = logcat_stats(adb_path)
    if not app_running(adb_path):
        print("  [警告] App 未在运行！")
    return s


# --------------------------------------------------------------------------- #
# App 控制
# --------------------------------------------------------------------------- #

def app_start(adb_path: str, autostart: bool, pin: bool) -> None:
    """
    全新启动 App（先 force-stop）。

    ★ 必须先 force-stop 再启动，不能只发 am start：
      launchMode=singleTask 下，第二次 am start 只送 onNewIntent，
      进程不重建，pin 参数虽然被接收但绑核不会重新生效，
      测出来的两组数据就没有区别。
    """
    app_stop(adb_path)
    time.sleep(2)
    sh(adb_path, "logcat -c", timeout=15)
    out = adb(adb_path, [
        "shell", "am", "start", "-n", "com.yq.sentinel/.MainActivity",
        "--ez", "autostart", "true" if autostart else "false",
        "--ez", "pin", "true" if pin else "false",
    ], timeout=25)
    if "Error" in out or "Exception" in out:
        print(f"  [警告] am start 返回异常: {out.strip()[:120]}")
    time.sleep(2)


def app_stop(adb_path: str) -> None:
    adb(adb_path, ["shell", "am", "force-stop", "com.yq.sentinel"], timeout=20)


def wait_ready(adb_path: str, seconds: int = 20) -> bool:
    """
    等待 App 完成模型加载。

    ★ 不用 `logcat | grep`：
      adb shell 会把整串命令交给设备 shell，
      管道里的引号在 Windows → bash → adb shell 多层传递中会被吃掉，
      结果 grep 匹配不到任何东西。
      这里改成先取 logcat 全文，再在 Python 侧做字符串匹配。
    """
    deadline = time.time() + seconds
    while time.time() < deadline:
        raw = adb(adb_path, ["shell", "logcat", "-d", "-t", "2000",
                            "-s", "PersonDetector:V"], timeout=20)
        if "预热完成" in raw:
            return True
        time.sleep(1)
    print("  [警告] 等待 App 就绪超时（可能启动失败）")
    return False


# --------------------------------------------------------------------------- #
# 报告
# --------------------------------------------------------------------------- #

def report(samples: List[Sample], out_path: str) -> None:
    print("\n" + "=" * 72)
    print("  斗在吗 · 检测开销实测")
    print("=" * 72)
    hdr = f"{'配置':<22}{'整机CPU':>9}{'AppCPU':>9}{'推理ms':>10}{'FPS':>7}{'码率KB/s':>10}"
    print(hdr)
    print("-" * 72)
    for s in samples:
        inf = f"{statistics.mean(s.infer_ms):.0f}" if s.infer_ms else "-"
        fps = f"{statistics.mean(s.det_fps):.1f}" if s.det_fps else "-"
        print(f"{s.label:<22}{s.cpu_total:>8.1f}%{s.app_cpu_pct:>8.0f}%"
              f"{inf:>10}{fps:>7}{s.net_rx_kbps:>10.1f}")
    print("-" * 72)

    # 分核明细
    for s in samples:
        if s.cpu_per_core:
            print(f"\n  {s.label} 分核占用:")
            for i, v in enumerate(s.cpu_per_core):
                bar = "#" * int(min(v, 100) / 3)
                print(f"    cpu{i}: {v:5.1f}%  {bar}")

    # 判定
    if len(samples) >= 2:
        a = samples[0]
        print("\n判定:")
        for s in samples[1:]:
            d = s.cpu_total - a.cpu_total
            big = sum(s.cpu_per_core[4:8]) if len(s.cpu_per_core) >= 8 else 0
            big_a = sum(a.cpu_per_core[4:8]) if len(a.cpu_per_core) >= 8 else 0
            print(f"  {s.label} vs {a.label}: 整机 {d:+.1f}%, "
                  f"大核(cpu4-7) {big_a:.1f}% -> {big:.1f}%")
            if s.infer_ms and a.infer_ms:
                ia, ib = statistics.mean(a.infer_ms), statistics.mean(s.infer_ms)
                print(f"    推理耗时 {ia:.0f}ms -> {ib:.0f}ms "
                      f"({'快' if ib < ia else '慢'} {abs(ib-ia)/max(ia,1)*100:.0f}%)")

    payload = {
        "samples": [
            {
                "label": s.label,
                "cpu_per_core": s.cpu_per_core,
                "cpu_total": s.cpu_total,
                "app_cpu_pct": s.app_cpu_pct,
                "infer_ms_avg": statistics.mean(s.infer_ms) if s.infer_ms else None,
                "det_fps_avg": statistics.mean(s.det_fps) if s.det_fps else None,
                "net_rx_kbps": s.net_rx_kbps,
                "stream_active": s.stream_active,
                "has_person": s.has_person,
            } for s in samples
        ]
    }
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    print(f"\n原始数据: {out_path}")


# --------------------------------------------------------------------------- #

def main() -> int:
    ap = argparse.ArgumentParser(description="测量检测的 CPU 占用与推理速度")
    ap.add_argument("--adb", default=DEFAULT_ADB)
    ap.add_argument("--duration", type=int, default=12)
    ap.add_argument("--mode", choices=["all", "detect", "baseline"], default="all",
                    help="all=三组; detect=只测两种检测配置; baseline=只测基线")
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    if not adb(args.adb, ["devices"]).strip().endswith("\tdevice"):
        devs = [l for l in adb(args.adb, ["devices"]).splitlines() if l.endswith("\tdevice")]
        if not devs:
            print("!! 没有设备连接")
            return 2

    print("设备:", adb(args.adb, ["devices"]).splitlines()[1:2])
    print("\n注意：串流需在手机上手动开始。脚本会检测流量来确认。")
    print("      若未检测到串流流量，结论仅代表「检测自身开销」，不代表干扰。")

    out_path = args.out or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "reports", f"detect-{time.strftime('%Y%m%d-%H%M%S')}.json")
    samples: List[Sample] = []

    # A 组：无检测
    if args.mode in ("all", "baseline"):
        app_stop(args.adb)
        time.sleep(3)
        samples.append(measure(args.adb, "A 无检测(基线)", args.duration))

    # B 组：检测，不绑核
    if args.mode in ("all", "detect"):
        app_start(args.adb, True, False)
        wait_ready(args.adb)
        time.sleep(4)   # 让检测循环稳定下来
        samples.append(measure(args.adb, "B 检测·不绑核", args.duration))

        # C 组：检测，绑小核
        app_start(args.adb, True, True)
        wait_ready(args.adb)
        time.sleep(4)
        samples.append(measure(args.adb, "C 检测·绑小核", args.duration))

    report(samples, out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
