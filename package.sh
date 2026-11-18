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
# 本机环境覆盖（不入库）：把 SDK / gradle 路径写进 .workbuddy/local_env.sh
# 仓库里不写死任何本机路径；新机器若已配好 ANDROID_HOME 且 gradle 在 PATH，可不建该文件
if [ -f ".workbuddy/local_env.sh" ]; then
    # shellcheck disable=SC1091
    source ".workbuddy/local_env.sh"
fi
export ANDROID_HOME="${ANDROID_HOME:-}"
GRADLE="${GRADLE:-gradle}"   # 默认用 PATH 里的 gradle；本机可在 local_env.sh 里覆盖为绝对路径

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
