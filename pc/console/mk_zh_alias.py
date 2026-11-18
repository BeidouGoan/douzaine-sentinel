#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mk_zh_alias.py — 编译后生成两个 exe 副本（build.cmd 每次都会调它）。

    DouZaiNe-zh.exe   英文名 + zh，方便命令行 / 快捷方式 / 杀毒软件识别
    斗在呢.exe        中文名，用户双击认识的就是它

★ 为什么不用 build.cmd 直接 copy：
    cmd.exe 按 GBK 读 .cmd 文件，UTF-8 的中文会变成乱码，
    结果复制出一个乱码文件名的 exe。
    Python 走 Win32 API，中文文件名完全没问题。

用法：
    python pc/console/mk_zh_alias.py
"""
import os
import shutil
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(BASE, "..", "dist", "DouZaiNe")

SRC = os.path.join(DIST, "DouZaiNe.exe")

# ★ 两个副本都要，缺一个用户就找不到入口
COPIES = ("DouZaiNe-zh.exe", "斗在呢.exe")

if not os.path.exists(SRC):
    print("找不到源文件，先编译：", SRC)
    sys.exit(1)

for name in COPIES:
    dst = os.path.join(DIST, name)
    try:
        shutil.copy2(SRC, dst)
    except OSError as e:
        print("复制失败 (%s)：%s" % (name, e))
        sys.exit(1)
    print("OK: %s  (%.1f KB)" % (dst, os.path.getsize(dst) / 1024.0))
