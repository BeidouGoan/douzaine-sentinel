#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
cleanup_load.py — 清理手机上的残留 CPU 负载进程。

★ 为什么需要这个（实测踩坑，很重要）：
    bench_stream_impact.py 注入的负载是 `sh -c 'while :; do :; done'`。
    kill 掉这个 shell **不会**杀掉真正在跑的循环子进程，
    于是它们会**一直留在设备上各占满一个核**，污染后续所有测量。

    实测中一次实验残留 3 个进程（各 ~99% CPU），
    导致后续测得的「串流占用」从 8.7% 跳到 42% —— 数据直接失真。

★ 为什么不用 `top | grep` 找：
    top 的 CMDLINE 列里，**执行 grep 的那个 shell 自己的命令行也包含
    "while :"**，于是 grep 会匹配到自己 → kill 掉自己的父 shell → SIGTERM。
    本机实测因此出现过命令被莫名中断。

    正确做法：直接遍历 /proc/<pid>/cmdline，完全不依赖文本匹配管道。
"""

import argparse
import os
import re
import subprocess
import sys
import time

DEFAULT_ADB = os.environ.get("ADB", "adb")

# 负载进程的特征串（bench 脚本注入的）
LOAD_PATTERNS = ("while :", "while :; do")


def adb(adb_path: str, args: list, timeout: int = 30) -> str:
    try:
        p = subprocess.run([adb_path] + args, capture_output=True, timeout=timeout)
        return p.stdout.decode("utf-8", "replace")
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return ""


def find_load_pids(adb_path: str) -> list:
    """
    通过遍历 /proc 找出所有负载进程。

    原理：adb shell 里跑一段 sh 循环，遍历 /proc/[0-9]*/cmdline，
    匹配特征串后输出 PID。不经过管道文本匹配，不会自匹配。
    """
    script = (
        'PAT="while"; '
        'PAT2=":"; '
        'for d in /proc/[0-9]*; do '
        'p=${d#/proc/}; '
        '[ "$p" = "$$" ] && continue; '
        'c=$(cat $d/cmdline 2>/dev/null | tr "\\000" " "); '
        'case "$c" in '
        '*"$PAT $PAT2"*) echo "$p";; '
        'esac; '
        'done'
    )
    raw = adb(adb_path, ["shell", script], timeout=60)
    pids = []
    for tok in raw.split():
        if tok.isdigit():
            pid = int(tok)
            # 排除自己这个扫描进程的 PID
            if pid != os.getpid():
                pids.append(pid)
    return sorted(set(pids))


def cmdline_of(adb_path: str, pid: int) -> str:
    raw = adb(adb_path, ["shell", f"cat /proc/{pid}/cmdline 2>/dev/null"])
    return raw.replace("\x00", " ").strip()


def main() -> int:
    ap = argparse.ArgumentParser(description="清理手机上的残留 CPU 负载进程")
    ap.add_argument("--adb", default=DEFAULT_ADB)
    ap.add_argument("--list", action="store_true", help="只列出不删除")
    ap.add_argument("--pattern", action="append", default=None,
                    help="额外的匹配串，可重复")
    args = ap.parse_args()

    patterns = list(LOAD_PATTERNS) + (args.pattern or [])
    pids = find_load_pids(args.adb)
    # 二次过滤：确保确实是负载命令，而不是恰好包含关键词的其它进程
    confirmed = []
    for p in pids:
        c = cmdline_of(args.adb, p)
        if any(pat in c for pat in patterns):
            confirmed.append((p, c))

    if not confirmed:
        print("没有发现残留负载进程。")
        return 0

    print(f"发现 {len(confirmed)} 个残留负载进程:")
    for p, c in confirmed:
        print(f"  {p}: {c}")
    if args.list:
        return 0

    killed = 0
    for p, _ in confirmed:
        adb(args.adb, ["shell", f"kill -9 {p} 2>/dev/null"])
        killed += 1
        time.sleep(0.1)

    time.sleep(2)
    remain = find_load_pids(args.adb)
    print(f"已 kill {killed} 个，剩余 {len(remain)} 个 {remain}")
    if remain:
        print("⚠ 仍有残留，再试一次或用更高权限。")
        return 1
    print("✓ 设备已干净。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
