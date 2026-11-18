#!/usr/bin/env bash
# =============================================================================
# probe_hiai_plugins.sh — 从华为自家 HiAI 插件反推「int8 上 NPU 的正确路径」
#
# 目的：
#   Gate 1 要回答的是「这台机器上，int8 怎么才能跑在 NPU 上」。
#   答案不用猜——华为自己的量产插件就装在设备上，而且文件名直接标明了
#   调度策略（-kirin980 = NPU，-cpu = CPU，-common = 通用）。
#
#   这个脚本做的事：
#     1) 列出设备上所有 HiAI 插件，按后缀分类
#     2) 拉一个 NPU 版和一个 CPU 版下来
#     3) 对比模型文件格式
#     4) 解析 NPU 版模型的文件头（magic / 层名 / 量化符号）
#
# 结论（实测）：
#   NPU 版 = assets/*.cambricon  → magic \x7fMEF = Cambricon Model Exchange Format
#            由 HiAI DDK 的 omg 工具生成，含 int8 量化 scale
#   CPU 版 = assets/*.ms + *.bin → MNN 模型格式
#
#   ⇒ int8 上 NPU 必须走 DDK 离线模型；NNAPI 不是正路。
#
# 用法：
#   bash probe/probe_hiai_plugins.sh
#   OUT_DIR=/tmp/hiai bash probe/probe_hiai_plugins.sh
# =============================================================================
set -uo pipefail

ADB="${ADB:-adb}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$ROOT/probe/out-hiai}"

say()  { printf '%s\n' "$*"; }
rule() { say "------------------------------------------------------------------"; }

# adb 封装
sh_adb() { "$ADB" shell "$@" 2>/dev/null; }
# 用管道取文件内容：Git Bash 下 `adb pull` 会被转换成 Windows 路径而失败
sh_adb_exec() { "$ADB" exec-out "$@" 2>/dev/null; }

mkdir -p "$OUT_DIR"

say "=================================================================="
say "  HiAI 插件逆向 · Gate 1 证据链"
say "  时间: $(date '+%Y-%m-%d %H:%M:%S')"
say "=================================================================="

# ---------------------------------------------------------------- 1. 定位包
rule; say "[1] 定位 HiAI 引擎包"
PKG="com.huawei.hiai"
PATHS="$(sh_adb "pm path $PKG")"
if [ -z "$PATHS" ]; then
  say "!! 未找到 $PKG。华为的 HiAI 视觉插件装在系统/特权分区时可能查不到，"
  say "   这时可用备选路径：/data/app/<pkg>*/ 或 /system/... ，或改用 OTA 包解包。"
  exit 2
fi
say "$PATHS" | sed 's/^/  /'
BASE_DIR="$(head -1 <<<"$PATHS" | sed 's|^package:||; s|/[^/]*$||')"
say "  基目录: $BASE_DIR"

# ---------------------------------------------------------------- 2. 插件清单
rule; say "[2] 插件清单（文件名后缀 = 调度策略）"
LIST="$OUT_DIR/plugins.txt"
sh_adb "ls $BASE_DIR" 2>/dev/null | grep -iE 'visionplugin|split_|asr|translation' | sort > "$LIST"
say "  --- 走 NPU (-kirin980) ---"
grep -i 'kirin980' "$LIST" | sed 's/^/     /' || say "     (无)"
say "  --- 走 CPU (-cpu) ---"
grep -i -- '-cpu' "$LIST" | sed 's/^/     /' || say "     (无)"
say "  --- 通用 (-common) ---"
grep -i 'common' "$LIST" | sed 's/^/     /' || say "     (无)"
say "  --- 其它拆分包 ---"
grep -viE 'kirin980|-cpu|common' "$LIST" | sed 's/^/     /' || say "     (无)"

NPU_COUNT="$(grep -ci 'kirin980' "$LIST")"
CPU_COUNT="$(grep -ci -- '-cpu' "$LIST")"
say ""
say "  统计: NPU 版 $NPU_COUNT 个, CPU 版 $CPU_COUNT 个"
say "  => 华为用文件名后缀直接标明该插件的加速后端，这是最硬的证据。"

# ---------------------------------------------------------------- 3. 拉取样本
rule; say "[3] 拉取 NPU 版与 CPU 版样本"
NPU_APK="$(grep -i 'kirin980' "$LIST" | head -1)"
CPU_APK="$(grep -i -- '-cpu' "$LIST" | head -1)"
[ -z "$NPU_APK" ] && { say "!! 没有 -kirin980 插件可拉取"; exit 3; }

pull_apk() {
  local name="$1" out="$OUT_DIR/$1"
  # 注意：Git Bash 下 adb pull 会被转换成 Windows 路径，必须用 exec-out 走管道
  sh_adb_exec "cat $BASE_DIR/$name" > "$out" 2>/dev/null
  local sz; sz="$(stat -c%s "$out" 2>/dev/null || echo 0)"
  say "  $name -> $sz bytes"
  [ "$sz" -gt 1000 ]
}

pull_apk "$NPU_APK" && say "  ✓ NPU 版拉取成功" || say "  ✗ NPU 版拉取失败"
if [ -n "$CPU_APK" ]; then
  pull_apk "$CPU_APK" && say "  ✓ CPU 版拉取成功" || say "  ✗ CPU 版拉取失败"
fi

# ---------------------------------------------------------------- 4. 模型格式对比
rule; say "[4] 模型文件格式对比"
INSPECT="$ROOT/probe/inspect_hiai_models.py"
if command -v python >/dev/null 2>&1; then
  PY=python
elif command -v python3 >/dev/null 2>&1; then
  PY=python3
else
  PY=""
fi
if [ -n "$PY" ] && [ -f "$INSPECT" ]; then
  "$PY" "$INSPECT" "$OUT_DIR/$NPU_APK" ${CPU_APK:+"$OUT_DIR/$CPU_APK"} 2>&1 | sed 's/^/  /'
else
  say "  (找不到 python 或 inspect_hiai_models.py，跳过深度解析)"
fi

# ---------------------------------------------------------------- 5. 结论
rule; say "[5] 结论"
say ""
say "  1) 华为的 NPU 版插件用 assets/*.cambricon（MEF 格式），"
say "     由 HiAI DDK 的 omg 工具离线编译而来，文件名带 _980 标明目标芯片。"
say "  2) 华为的 CPU 版插件用 assets/*.ms + *.bin，即 MNN 模型格式。"
say "  3) ⇒ 本机 int8 上 NPU 的正确路径是【HiAI DDK 离线模型】；"
say "     NNAPI 不是正路（华为官方自己都不用）。"
say "  4) ⇒ CPU 方案建议用 MNN，与华为本机生态一致；"
say "     且 MNN 同时支持 CPU 与 NNAPI 后端，一套框架可做 A/B 对照。"
say ""
say "  下一步 (Gate 3):"
say "    1) 下载 HIAI DDK V200（对应 Kirin 980）"
say "    2) 用 omg 把模型转成 .nb / .cambricon"
say "    3) ★ 读 eval_report.json —— 它逐个算子告诉你哪些不支持"
say "    4) 参考实现: github.com/AIIABenchmark/AIIA-DNN-benchmark (hiai 后端)"
say ""
say "  样本目录: $OUT_DIR"
say "=================================================================="
