#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
inspect_hiai_models.py — 解析华为 HiAI 插件 APK 里的模型文件。

与 probe_hiai_plugins.sh 配合使用，单独跑也可以：

    python probe/inspect_hiai_models.py <apk1> [apk2 ...]

它要回答的问题是：**NPU 版插件和 CPU 版插件的模型格式有什么不同？**

已知答案（实测）：
    NPU 版 (visionplugin-xxx-kirin980-...)  → assets/*.cambricon
        magic = b'\\x7fMEF' = Cambricon Model Exchange Format
        由 HiAI DDK 的 omg 工具离线编译，含 int8 量化 scale
    CPU 版 (visionplugin-xxx-cpu-...)        → assets/*.ms + *.bin
        MNN 模型格式
"""

import os
import re
import sys
import zipfile

# 模型文件扩展名
MODEL_EXTS = (".cambricon", ".nb", ".om", ".ms", ".model")
# 值得关注的其他资产
ASSET_EXTS = (".so",)

MEF_MAGIC = b"\x7fMEF"


def describe_magic(head: bytes, name: str) -> str:
    if head[:4] == MEF_MAGIC:
        return "MEF (Cambricon Model Exchange Format) —— DDK 离线模型"
    if name.lower().endswith(".ms"):
        return "MNN 模型 (.ms + .bin)"
    if head[:4] == b"PK\x03\x04":
        return "ZIP/APK"
    return f"未知 (magic={head[:4]!r})"


def inspect(apk_path: str) -> None:
    print(f"\n  === {os.path.basename(apk_path)} ===")
    try:
        z = zipfile.ZipFile(apk_path)
    except Exception as e:
        print(f"    打不开: {e}")
        return

    names = z.namelist()

    # 1) 模型文件
    models = [n for n in names if n.lower().endswith(MODEL_EXTS)]
    if models:
        print("    [模型文件]")
        for m in sorted(models, key=lambda x: -z.getinfo(x).file_size):
            size = z.getinfo(m).file_size
            try:
                head = z.read(m)[:64]
            except Exception:
                head = b""
            print(f"      {size:>10}  {m}")
            print(f"                 -> {describe_magic(head, m)}")
    else:
        print("    [模型文件] 无")

    # 2) native 库
    libs = [n for n in names if n.startswith("lib/") and n.lower().endswith(ASSET_EXTS)]
    if libs:
        print("    [native 库]")
        for l in sorted(libs, key=lambda x: -z.getinfo(x).file_size)[:6]:
            print(f"      {z.getinfo(l).file_size:>10}  {l}")

    # 3) 对 NPU 版模型做深度解析
    for m in models:
        if not m.lower().endswith(".cambricon"):
            continue
        try:
            data = z.read(m)
        except Exception:
            continue
        print(f"\n    [深度解析] {m}  ({len(data)} bytes)")
        print("      --- 头部 64 字节 ---")
        for i in range(0, 64, 16):
            chunk = data[i:i + 16]
            hexs = " ".join(f"{b:02x}" for b in chunk)
            asci = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
            print(f"      {i:04x}  {hexs:<48}  {asci}")

        # 提取可见符号：层名、算子名、量化相关
        syms = re.findall(rb"[ -~]{4,80}", data[:65536])
        uniq = []
        for s in syms:
            u = s.decode("ascii", "replace")
            if u not in uniq:
                uniq.append(u)

        quant_hits = [u for u in uniq
                      if re.search(r"scale|quant|int8|zero_point|round|clip", u, re.I)]
        struct_hits = [u for u in uniq
                       if re.search(r"subnet|stage|unit|block|conv|pool|relu|bn_", u, re.I)]
        hiaihits = [u for u in uniq
                    if re.search(r"saveModel|execution|cambricon|omg", u, re.I)]

        if quant_hits:
            print("      --- 量化相关符号 ---")
            for u in quant_hits[:10]:
                print(f"        {u}")
        if struct_hits:
            print("      --- 网络结构符号 ---")
            for u in struct_hits[:12]:
                print(f"        {u}")
        if hiaihits:
            print("      --- DDK 工具符号 ---")
            for u in hiaihits[:8]:
                print(f"        {u}")


def main() -> int:
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        print("用法: python inspect_hiai_models.py <apk1> [apk2 ...]")
        return 1
    for a in args:
        if os.path.exists(a):
            inspect(a)
        else:
            print(f"  !! 文件不存在: {a}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
