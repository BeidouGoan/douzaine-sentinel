#!/usr/bin/env bash
# ===========================================================================
#  package.sh — 打包移动端 APK 并覆盖到项目根目录
#
#  用法：bash package.sh
#  （Windows 上也可以双击 package.cmd，效果一样）
#
#  产物：./DouZaiMa-v1.0.apk
#  ★ versionName 改了的话，记得同步改下面 APK_NAME 和 package.cmd 里的名字。
# ===========================================================================
set -euo pipefail

cd "$(dirname "$0")"

APK_NAME="DouZaiMa-v1.0.apk"
# 按需设置这两个环境变量指向你本机的 SDK / Gradle；此处不写死任何本机路径
export ANDROID_HOME="${ANDROID_HOME:-<ANDROID_SDK>}"
GRADLE="${GRADLE:-gradle}"   # 默认用 PATH 里的 gradle，也可传绝对路径

if [ ! -x "$GRADLE" ] && [ ! -f "$GRADLE" ]; then
    echo "[ERROR] 找不到 gradle：$GRADLE"
    exit 1
fi

echo "[1/2] 编译 debug APK…"
"$GRADLE" assembleDebug --no-daemon --console=plain | tail -6

SRC="build/outputs/apk/debug/DouZaiMa-debug.apk"
if [ ! -f "$SRC" ]; then
    echo "[ERROR] 编译产物不存在：$SRC"
    exit 1
fi

echo "[2/2] 覆盖到项目根目录…"
cp -f "$SRC" "$APK_NAME"

# 打印大小，方便确认真的换了新包
SIZE=$(du -h "$APK_NAME" | cut -f1)
echo
echo "OK: $(pwd)/$APK_NAME  ($SIZE)"
