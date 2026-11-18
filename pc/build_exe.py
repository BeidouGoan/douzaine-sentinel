#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_exe.py — 把 PC 控制台打包成 Windows exe

用法：
    python pc/build_exe.py              # 打包（输出到 pc/dist/）
    python pc/build_exe.py --clean      # 先清理再打包
    python pc/build_exe.py --onefile    # 单文件版（启动慢一点，但只有一个 exe）

★ 打包说明：
    - 用 PyInstaller，把 Python 运行时一起打进去，目标机器不用装 Python。
    - 只依赖标准库（http.server / json / socket），不需要额外 dll。
    - web/index.html 会作为数据文件一起打包，运行时释放到临时目录。

★ 注意：
    Windows 上打出来的 exe 只能在 Windows 上跑。
    要在别的系统跑就在那个系统上打包（PyInstaller 不支持交叉编译）。
"""

import argparse
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def main() -> int:
    ap = argparse.ArgumentParser(description="打包工位哨兵 PC 控制台")
    ap.add_argument("--clean", action="store_true", help="先清理 build/dist")
    ap.add_argument("--onefile", action="store_true", help="打成单个 exe")
    ap.add_argument("--name", default="SentinelConsole", help="exe 名字")
    args = ap.parse_args()

    # ---- 检查 PyInstaller ----
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("!! 没装 PyInstaller。先运行：")
        print("   python -m pip install pyinstaller")
        return 1

    # ---- 清理 ----
    if args.clean:
        for d in ("build", "dist"):
            p = os.path.join(HERE, d)
            if os.path.exists(p):
                shutil.rmtree(p)
                print(f"已清理 {p}")

    # ---- 组装 PyInstaller 参数 ----
    web_html = os.path.join(HERE, "web", "index.html")
    if not os.path.exists(web_html):
        print(f"!! 找不到前端文件: {web_html}")
        return 1

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm", "--clean",
        "--name", args.name,
        "--distpath", os.path.join(HERE, "dist"),
        "--workpath", os.path.join(HERE, "build"),
        "--specpath", HERE,
        # 前端 HTML 作为数据文件
        "--add-data", f"{web_html}{os.pathsep}web",
        # 排除用不到的大依赖，减小体积。
        # ★ 注意：别排除 email / http.client —— http.server 内部依赖它们，
        #   排除后打包能过，但运行时直接 ModuleNotFoundError 崩掉。
        "--exclude-module", "tkinter",
        "--exclude-module", "unittest",
        "--exclude-module", "pydoc",
        "--exclude-module", "doctest",
        "--exclude-module", "xmlrpc",
        "--exclude-module", "test",
        "--console",
        os.path.join(HERE, "sentinel_web.py"),
    ]
    if args.onefile:
        cmd.insert(-1, "--onefile")
    else:
        cmd.insert(-1, "--onedir")

    print("执行:", " ".join(cmd[:8]), "...")
    print("(首次打包约 1-3 分钟)\n")

    r = subprocess.run(cmd, cwd=HERE)
    if r.returncode != 0:
        print("\n!! 打包失败")
        return r.returncode

    # ---- 结果 ----
    if args.onefile:
        exe = os.path.join(HERE, "dist", f"{args.name}.exe")
    else:
        exe = os.path.join(HERE, "dist", args.name, f"{args.name}.exe")

    print("\n" + "=" * 60)
    if os.path.exists(exe):
        size = os.path.getsize(exe) / 1024 / 1024
        print(f"打包成功: {exe}")
        print(f"体积: {size:.1f} MB")
        print("\n直接双击运行即可，浏览器会自动打开 http://127.0.0.1:8766")
        print("（目标机器不需要装 Python）")
    else:
        print(f"打包结束，但没找到 exe: {exe}")
        print("看看 dist/ 目录里有什么")
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())
