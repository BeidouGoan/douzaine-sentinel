#!/usr/bin/env bash
# =============================================================================
# run_ab_test.sh — 斗在吗 A/B 对照实验
#
# ★ 这是整个项目的"结论"所在：
#   三组对照，回答「检测和串流能不能共存」。
#
#   A 组：串流中，不跑检测            → 基线
#   B 组：串流中，跑检测（不绑核）    → 检测对串流的干扰
#   C 组：串流中，跑检测（绑小核）    → 绑核能否消除干扰
#
# 前置条件：
#   1) 手机已安装 DouZaiMa-debug.apk
#   2) PC 上 Sunshine 在跑
#   3) ★ 手机上 Moonlight 已开始串流（脚本会检查，检测不到会提示）
#
# 用法：
#   bash bench/run_ab_test.sh
#   DURATION=30 bash bench/run_ab_test.sh
# =============================================================================
set -uo pipefail

ADB="${ADB:-adb}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DURATION="${DURATION:-20}"
OUT_DIR="$ROOT/reports"
mkdir -p "$OUT_DIR"
STAMP="$(date +%Y%m%d-%H%M%S)"
RESULT="$OUT_DIR/ab-$STAMP.json"

say() { printf '%s\n' "$*"; }
rule() { printf -- '--------------------------------------------------------------\n'; }

# ---------------------------------------------------------------- 环境自检
rule
say "[0] 环境自检"
DEV="$(adb devices | awk 'NR>1 && $2=="device"{print $1; exit}')"
if [ -z "$DEV" ]; then
  say "!! 没有设备连接。先确认：adb devices"
  exit 2
fi
say "  设备: $DEV"

# 串流活跃度检查（关键：Moonlight 挂着 ≠ 在串流）
check_stream() {
  local label="$1"
  local n1 n2 d
  n1="$(adb shell "cat /proc/net/dev 2>/dev/null" 2>/dev/null)"
  sleep 3
  n2="$(adb shell "cat /proc/net/dev 2>/dev/null" 2>/dev/null)"
  d="$(python3 -c "
import sys
def tot(t):
    s=0
    for line in t.splitlines():
        if ':' not in line: continue
        name,_,rest=line.partition(':')
        if name.strip()=='lo': continue
        f=rest.split()
        if f and f[0].isdigit(): s+=int(f[0])
    return s
a=tot('''$n1'''); b=tot('''$n2''')
print(max(0,b-a))
" 2>/dev/null)"
  local kb=$(( ${d:-0} / 3 / 1024 ))
  say "  [$label] 非回环网卡接收 ${kb} KB/s"
  if [ "${d:-0}" -gt 20000 ]; then
    say "         => ★ 串流中"
    return 0
  fi
  say "         => ⚠ 没有串流流量！请在手机上打开 Moonlight 并开始串流"
  return 1
}

# ---------------------------------------------------------------- 采样
sample() {
  local label="$1"
  python3 "$ROOT/bench/bench_stream_impact.py" --duration "$DURATION" --stage streaming 2>/dev/null \
    | sed -n '/=== streaming ===/,/^$/p'
}

# ---------------------------------------------------------------- 流程
rule
say "[1] 检查串流状态"
if ! check_stream "前置"; then
  say ""
  say "!! 串流未激活，后续数据无效。"
  say "   请在手机上打开 Moonlight，连接 PC，开始串流后重跑本脚本。"
  exit 3
fi

# A 组：停掉检测
rule
say "[2] A 组 · 串流中，不跑检测（基线）"
adb shell "am start -n com.yq.sentinel/.MainActivity --ez stop true" >/dev/null 2>&1
sleep 4
check_stream "A组" || true
SAMPLE_A="$(sample A)"

# B 组：启动检测，不绑核
rule
say "[3] B 组 · 串流中，跑检测（不绑核）"
adb shell "am start -n com.yq.sentinel/.MainActivity --ez autostart true" >/dev/null 2>&1
sleep 8
# 关闭绑核：先把 toggle 状态改掉（通过 intent 的 pin 参数）
adb shell "am start -n com.yq.sentinel/.MainActivity --ez autostart true --ez pin false" >/dev/null 2>&1
sleep 3
check_stream "B组" || true
SAMPLE_B="$(sample B)"

# C 组：绑小核
rule
say "[4] C 组 · 串流中，跑检测（绑小核 cpu0-3）"
adb shell "am start -n com.yq.sentinel/.MainActivity --ez autostart true --ez pin true" >/dev/null 2>&1
sleep 8
check_stream "C组" || true
SAMPLE_C="$(sample C)"

# ---------------------------------------------------------------- 汇总
rule
say "[5] 汇总"
say ""
say "A 组（不跑检测）:"
echo "$SAMPLE_A" | grep -E "cpu[0-9]|合计|抖动" | sed 's/^/    /'
say ""
say "B 组（检测，不绑核）:"
echo "$SAMPLE_B" | grep -E "cpu[0-9]|合计|抖动" | sed 's/^/    /'
say ""
say "C 组（检测，绑小核）:"
echo "$SAMPLE_C" | grep -E "cpu[0-9]|合计|抖动" | sed 's/^/    /'
say ""
say "原始采样已存: $RESULT"

# 保存原始 JSON（每组单独跑一次 bench 存 JSON 太慢，这里存文本）
{
  echo "{"
  echo "  \"stamp\": \"$STAMP\","
  echo "  \"duration\": $DURATION,"
  echo "  \"A_no_detection\": \"$(echo "$SAMPLE_A" | tr '\n' '|' | sed 's/"/\\"/g')\","
  echo "  \"B_detection_unpinned\": \"$(echo "$SAMPLE_B" | tr '\n' '|' | sed 's/"/\\"/g')\","
  echo "  \"C_detection_pinned\": \"$(echo "$SAMPLE_C" | tr '\n' '|' | sed 's/"/\\"/g')\""
  echo "}"
} > "$RESULT"

rule
say "完成。"
