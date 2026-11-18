#!/usr/bin/env bash
# =============================================================================
# probe_device.sh — 麒麟980 一次性事实探测（决策门，10 分钟内出结论）
#
# 目的：把「这台手机上 NPU 到底能不能被第三方 App 用上」这件事，
#       拆成可以逐条读、逐条判的检查项，而不是靠猜。
#
# 用法：
#   adb devices                     # 先确认手机在线
#   bash probe/probe_device.sh
#   ADB="C:/path/to/adb.exe" bash probe/probe_device.sh
#
# 输出：
#   reports/probe-<timestamp>.txt   人类可读全文
#   reports/probe-<timestamp>.kv    机器可读 KEY=VALUE 摘要（供 bench 脚本消费）
#
# 依赖：adb（Windows 上建议用官方 platform-tools）。不需要 root。
# =============================================================================
set -uo pipefail

ADB="${ADB:-adb}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$ROOT/reports}"
STAMP="$(date +%Y%m%d-%H%M%S)"
TXT="$OUT_DIR/probe-$STAMP.txt"
KV="$OUT_DIR/probe-$STAMP.kv"

mkdir -p "$OUT_DIR"
: > "$TXT"
: > "$KV"

# ---------------------------------------------------------------- 输出helpers
say()  { printf '%s\n' "$*" | tee -a "$TXT"; }
rule() { say "------------------------------------------------------------------"; }
kv()   { printf '%s=%s\n' "$1" "${2-}" >> "$KV"; }
kvq()  { # 带引号的值，避免下游解析歧义
         local v="${2-}"; v="${v//\"/\\\"}"
         printf '%s="%s"\n' "$1" "$v" >> "$KV"; }
has()  { grep -qiE "$1" <<<"$2" && echo 1 || echo 0; }

# ---------------------------------------------------------------- adb 封装
sh() { "$ADB" shell "$@" 2>/dev/null; }
shraw() { "$ADB" shell "$@" 2>&1; }
# 注意：devices 是 adb 自身的子命令，不是 shell 命令，不能走 shraw
adbdev() { "$ADB" devices -l 2>&1; }

say "=================================================================="
say "  Kirin 980 / NPU 设备事实探测"
say "  时间: $(date '+%Y-%m-%d %H:%M:%S %Z')"
say "  adb : $("$ADB" version 2>/dev/null | head -1)"
say "=================================================================="

# ---------------------------------------------------------------- 0. 连通性
rule; say "[0] 设备连通性"
DEV_LINES="$(adbdev)"
say "$DEV_LINES"
# 只从 "List of devices attached" 之后解析设备行，避免被 daemon 噪声行干扰。
# 注意：grep -E 不支持 \t 转义，所以这里统一用 awk 处理分隔。
DEV_STATUS="$(awk '/^List of devices attached/{f=1;next} f&&NF>=2{print $1" "$2}' <<<"$DEV_LINES")"
DEV_SERIAL="$(awk '/^List of devices attached/{f=1;next} f&&$2=="device"{print $1; exit}' <<<"$DEV_LINES")"
if [ -z "$DEV_SERIAL" ]; then
  say ""
  say "!! 没有处于 device 状态的设备。后续所有探测都无法进行。"
  say "!! 请检查：USB 线是否为数据线、手机是否开启「USB 调试」、是否已授权本机 RSA 指纹。"
  kv "device_connected" "0"
  kvq "adb_devices_raw" "$DEV_LINES"
  say ""
  say "报告仍已写入：$TXT / $KV"
  exit 2
fi
kv "device_connected" "1"
kvq "device_serial" "$DEV_SERIAL"
say "  -> 使用设备 serial: $DEV_SERIAL"

# ---------------------------------------------------------------- 1. 身份
rule; say "[1] 设备身份与系统版本"
for p in ro.product.model ro.product.brand ro.product.manufacturer \
         ro.build.version.emui ro.build.version.release ro.build.version.sdk \
         ro.build.display.id ro.build.version.security_patch \
         ro.hardware ro.board.platform ro.product.board ro.soc.model \
         ro.hardware.egl ro.sf.lcd_density; do
  v="$(sh "getprop $p")"
  [ -n "$v" ] && { printf '  %-38s = %s\n' "$p" "$v" | tee -a "$TXT"; }
done
EMUI="$(sh 'getprop ro.build.version.emui')"
REL="$(sh 'getprop ro.build.version.release')"
SDK="$(sh 'getprop ro.build.version.sdk')"
kvq "emui" "$EMUI"; kvq "android_release" "$REL"; kvq "sdk" "$SDK"

# ---------------------------------------------------------------- 2. SoC 身份
rule; say "[2] SoC 身份核验（确认确实是 Kirin 980 而不是 810/970）"
SOC="$(sh 'getprop ro.soc.model')"
[ -z "$SOC" ] && SOC="$(sh 'getprop ro.board.platform')"
say "  soc.model / board.platform = $SOC"
case "$SOC" in
  *980*|*kirin980*|*kirin980*) kv "is_kirin980" "1"; say "  => 判定: Kirin 980 家族" ;;
  *810*|*710*) kv "is_kirin980" "0"; say "  => 判定: Kirin 810/710 家族（达芬奇 NPU，路线完全不同）" ;;
  *) kv "is_kirin980" "unknown"; say "  => 判定: 未知，请人工核对" ;;
esac

# ---------------------------------------------------------------- 3. CPU 拓扑
rule; say "[3] CPU 拓扑与调频（big.LITTLE 绑核实验的基础）"
CPUINFO="$(shraw 'cat /proc/cpuinfo')"
say "  processor 数 = $(grep -c '^processor' <<<"$CPUINFO")"
say "  Hardware      = $(grep -m1 '^Hardware' <<<"$CPUINFO" | cut -d: -f2- | sed 's/^ *//')"
say "  part(SoC)     = $(grep -m1 -iE '^\s*(Hardware|part)\s*:' <<<"$CPUINFO" | head -1)"
say ""
say "  idx  max_freq  min_freq  governor   part"
MAXF=""
CORTYPE=""
for i in 0 1 2 3 4 5 6 7; do
  d="/sys/devices/system/cpu/cpu$i/cpufreq"
  # 注意：路径必须到设备上判断，PC 上不存在这些目录
  if [ "$(sh "test -d $d && echo yes")" != "yes" ]; then
    continue
  fi
  mx="$(sh "cat $d/cpuinfo_max_freq 2>/dev/null")"
  mn="$(sh "cat $d/cpuinfo_min_freq 2>/dev/null")"
  gv="$(sh "cat $d/scaling_governor 2>/dev/null")"
  # CPU part: 0xd05 = Cortex-A55(小核), 0xd40 = Cortex-A76(大核)
  pt="$(sh "cat /proc/cpuinfo | grep -A1 '^processor[[:space:]]*:$i' | grep 'CPU part' | cut -d: -f2" | tr -d ' \r')"
  case "$pt" in
    0xd05) cname="A55-little" ;;
    0xd40) cname="A76-big" ;;
    0xd41) cname="A77-big" ;;
    *)     cname="" ;;
  esac
  say "  $i    ${mx:-?}         ${mn:-?}       ${gv:-?}    ${pt:-?} $cname"
  MAXF="$MAXF$i=$mx:$cname "
  CORTYPE="$CORTYPE$i:$cname "
done
say ""
say "  ★ 实际核型（从 /proc/cpuinfo 的 CPU part 读出，不靠猜）:"
say "    $CORTYPE"
say "  Kirin 980 规格: 2xA76@2.6G + 2xA76@1.92G + 4xA55@1.8G"
say "  ★ 某次真机实测: cpu0-3 = A55 小核, cpu4-7 = A76 大核"
say "    —— 注意核编号顺序因 ROM 而异，务必以上面读到的实际映射为准。"
say "    小核（A55）是检测负载的理想落点；把大核留给串流解码。"
say "    Android 的 taskset 是 toybox 版，掩码用十六进制："
say "      cpu0-3 = 0x0f   cpu4-7 = 0xf0   全部 = 0xff"
kvq "cpu_freq_map" "$(echo "$MAXF" | sed 's/ $//')"
kvq "cpu_core_types" "$(echo "$CORTYPE" | sed 's/ $//')"
kv "cpu_core_count" "$(grep -c '^processor' <<<"$CPUINFO")"

# ---------------------------------------------------------------- 4. NNAPI
rule; say "[4] NNAPI 驱动与框架（关键一格：Int8 是否会被派发到 NPU）"
say "  -- /vendor/lib64 中与 nnapi/hiai/neural 相关的库 --"
VLIB="$(shraw 'ls /vendor/lib64 2>/dev/null')"
echo "$VLIB" | grep -iE 'nnapi|hiai|neural|cambricon' | sed 's/^/     /' | tee -a "$TXT"
[ -z "$(echo "$VLIB" | grep -iE 'nnapi|hiai|neural')" ] && say "     (无匹配)"
say ""
say "  -- /system/lib64 中相关库 --"
SLIB="$(shraw 'ls /system/lib64 2>/dev/null')"
echo "$SLIB" | grep -iE 'nnapi|hiai|neural|cambricon' | sed 's/^/     /' | tee -a "$TXT"
[ -z "$(echo "$SLIB" | grep -iE 'nnapi|hiai|neural')" ] && say "     (无匹配)"
say ""
say "  -- dumpsys 中 nnapi / neuralnetworks 片段 --"
DS="$(shraw 'dumpsys 2>/dev/null | grep -iE -A6 "nnapi|neuralnetworks" | head -60')"
[ -n "$DS" ] && printf '%s\n' "$DS" | tee -a "$TXT" || say "     (dumpsys 无 nnapi/neuralnetworks 片段)"

NNAPI_LIB_FOUND="$(has 'nnapi|hiai' "$VLIB$SLIB")"
kv "nnapi_lib_present" "$NNAPI_LIB_FOUND"
kv "nnapi_has_hiai_named_lib" "$(has 'hiai' "$VLIB$SLIB")"

# ---------------------------------------------------------------- 5. NPU 设备节点
rule; say "[5] NPU 设备节点 / devfreq / 中断（判断 NPU 是否真的在活动）"
say "  -- /dev 下的 NPU 节点 --"
DEV="$(shraw 'ls /dev 2>/dev/null | grep -iE "^(hiai|npu|mlucore|cambricon)[a-z0-9_]*$"')"
[ -n "$DEV" ] && printf '%s\n' "$DEV" | tee -a "$TXT" || say "     (无 hiai/npu/cambricon 节点)"
kv "npu_device_node_present" "$([ -n "$DEV" ] && echo 1 || echo 0)"

say ""
say "  -- /proc/devices 中是否注册了 NPU 字符设备 --"
PD="$(shraw 'cat /proc/devices 2>/dev/null | grep -iE "hiai|npu|cambricon"')"
[ -n "$PD" ] && printf '%s\n' "$PD" | tee -a "$TXT" || say "     (无)"
kv "npu_char_device_registered" "$([ -n "$PD" ] && echo 1 || echo 0)"

say ""
say "  -- /sys/devices/platform 下的 NPU 平台设备 ★ 比 /dev 更可靠的判据 --"
PLAT="$(shraw 'ls /sys/devices/platform 2>/dev/null | grep -iE "npu|hiai|cambricon|mlucore"')"
[ -n "$PLAT" ] && printf '%s\n' "$PLAT" | sed 's/^/     /' | tee -a "$TXT" || say "     (无)"
kv "npu_platform_device_present" "$([ -n "$PLAT" ] && echo 1 || echo 0)"
NPU_CORES="$(shraw 'ls /sys/devices/platform 2>/dev/null | grep -icE "^npu-core"')"
say "     NPU 核心数（npu-core*）= $NPU_CORES"
kv "npu_core_count" "$NPU_CORES"

say ""
say "  -- /sys/class/devfreq 下的加速器 --"
DF="$(shraw 'ls /sys/class/devfreq 2>/dev/null')"
echo "$DF" | sed 's/^/     /' | tee -a "$TXT"
kv "npu_devfreq_present" "$(has 'npu|hiai|dsp' "$DF")"

say ""
say "  -- /proc/interrupts 中与 NPU 相关的行（跑推理时计数会增长） --"
INT="$(shraw 'cat /proc/interrupts 2>/dev/null | grep -iE "hiai|npu|cambricon"')"
[ -n "$INT" ] && printf '%s\n' "$INT" | tee -a "$TXT" || say "     (无 /proc/interrupts 不可读或无匹配)"
kv "npu_irq_line_present" "$([ -n "$INT" ] && echo 1 || echo 0)"

say ""
say "  -- NPU 内核线程（★ 本机可用的可观测点） --"
# 本机实测存在 [npu_mntn_rdr_wq] 这类 NPU 内核工作队列线程。
# 它存在说明 NPU 驱动已加载且在工作；跑推理时看它是否被唤醒。
NPUTH="$(shraw 'ps -A 2>/dev/null | grep -iE "npu|hiai|cambricon"')"
[ -n "$NPUTH" ] && printf '%s\n' "$NPUTH" | sed 's/^/     /' | tee -a "$TXT" || say "     (无 NPU 相关线程)"
kv "npu_thread_present" "$([ -n "$NPUTH" ] && echo 1 || echo 0)"

# ---------------------------------------------------------------- 6. HiAI 运行时
rule; say "[6] HiAI 运行时与守护进程"
say "  -- 相关进程 --"
PS="$(shraw 'ps -A 2>/dev/null | grep -iE "hiai|npu|cambricon|nnapi"')"
[ -n "$PS" ] && printf '%s\n' "$PS" | tee -a "$TXT" || say "     (无)"
say ""
say "  -- HiAI 相关配置/固件目录 --"
for d in /vendor/etc/hiai /system/etc/hiai /vendor/lib64/hiai /data/vendor/hiai; do
  o="$(shraw "ls $d 2>/dev/null | head -8")"
  [ -n "$o" ] && { say "     $d/:"; printf '%s\n' "$o" | sed 's/^/       /' | tee -a "$TXT"; }
done
kv "hiai_runtime_hint" "$([ -n "$PS" ] && echo 1 || echo 0)"

# ---------------------------------------------------------------- 7. 相机 / 供电
rule; say "[7] 相机与供电（斗在吗的前置条件）"
CAMCNT="$(shraw 'dumpsys media.camera 2>/dev/null | grep -iE "number of camera devices|Device .* information" | head -5')"
[ -n "$CAMCNT" ] && printf '%s\n' "$CAMCNT" | tee -a "$TXT" || say "  相机信息不可读"
kv "camera_probe_ok" "$([ -n "$CAMCNT" ] && echo 1 || echo 0)"

say ""
BAT="$(shraw 'dumpsys battery 2>/dev/null | grep -iE "level|status|powered"')"
[ -n "$BAT" ] && printf '%s\n' "$BAT" | sed 's/^/  /' | tee -a "$TXT" || say "  电池信息不可读"
kvq "battery" "$(echo "$BAT" | tr '\n' ' ')"

say ""
THERM="$(shraw 'for z in /sys/class/thermal/thermal_zone*; do [ -r "$z/temp" ] && echo "$(basename $z)=$(cat $z/temp)"; done 2>/dev/null | head -12')"
[ -n "$THERM" ] && printf '%s\n' "$THERM" | sed 's/^/  /' | tee -a "$TXT"

# ---------------------------------------------------------------- 7.5 串流活跃度
rule; say "[7.5] 串流是否真的在跑（★ 极易踩坑）"
say "  Moonlight 进程可能存在、窗口存在，但**并没有在串流**。"
say "  此时 dumpsys cpuinfo 里的历史百分比会误导你。"
say ""
MLP="$(shraw 'ps -A 2>/dev/null | grep -iE "limelight|moonlight"')"
if [ -n "$MLP" ]; then
  say "  Moonlight 进程:"
  printf '%s\n' "$MLP" | sed 's/^/     /' | tee -a "$TXT"
  kv "moonlight_running" "1"
else
  say "  未发现 Moonlight 进程"
  kv "moonlight_running" "0"
fi
say ""
say "  网卡流量采样（4 秒间隔，判断是否有真实数据流）:"
say "  ★ 注意：串流可能走 wlan0，也可能走 USB 共享网卡(rndis0/usb0/eth0)，"
say "    所以必须统计【所有】非 lo 接口，不能只看 wlan0。"
NET1="$(shraw 'cat /proc/net/dev 2>/dev/null')"
sleep 4
NET2="$(shraw 'cat /proc/net/dev 2>/dev/null')"
# 逐接口算接收增量（第 2 列是接收字节），用临时文件避免进程替换的引号地狱
echo "$NET1" > "$OUT_DIR/.net1"
echo "$NET2" > "$OUT_DIR/.net2"
DELTA_STR="$(awk '
  FNR==NR { a[$1]=$2; next }
  { if ($1 in a) { d=$2-a[$1]; if (d>0) printf "%s=%d ", $1, d } }
' "$OUT_DIR/.net1" "$OUT_DIR/.net2")"
rm -f "$OUT_DIR/.net1" "$OUT_DIR/.net2"
say "     4 秒内各接口接收增量:$DELTA_STR"
RX_TOTAL=0
for kv in $DELTA_STR; do
  name="${kv%%=*}"
  v="${kv#*=}"
  [ "$name" = "lo" ] && continue
  RX_TOTAL=$((RX_TOTAL + v))
done
say "     非回环接口总接收增量 = $RX_TOTAL 字节"
if [ "$RX_TOTAL" -gt 20000 ]; then
  say "     => 判定: ★ 正在串流（流量活跃）"
  kv "stream_active" "1"
elif [ -n "$MLP" ]; then
  say "     => 判定: Moonlight 在后台但【没有串流】。"
  say "        开始串流后重跑本脚本，否则会测到错误的基线。"
  kv "stream_active" "0"
else
  say "     => 判定: 未串流"
  kv "stream_active" "0"
fi
rule; say "[8] root / 调试能力（决定后面能不能抓更多证据）"
if [ "$(sh 'id -u')" = "0" ]; then
  say "  adb shell 已是 root（可能是 magisk/工程机）"; kv "root_shell" "1"
else
  SU="$(sh 'command -v su 2>/dev/null')"
  if [ -n "$SU" ]; then say "  发现 su: $SU（是否免密未知）"; kv "su_present" "1"; kv "root_shell" "0"
  else say "  无 root（正常，绝大多数方案不需要 root）"; kv "su_present" "0"; kv "root_shell" "0"; fi
fi
kvq "sdk_int" "$SDK"

# ---------------------------------------------------------------- 9. 结论
rule; say "[9] 结论表（配合 docs/02-决策树.md 阅读）"
say ""
say "  A. 路线选择的关键在 NNAPI 的 Int8 派发能力，而不在「有没有 NNAPI」。"
say "     已装机的 980（EMUI 8.1+）的 NNAPI 驱动基于 HiAI 实现，"
say "     但公开实测一致显示：FP16 模型会被派发到 NPU，Int8 量化模型不会（回落 CPU/GPU）。"
say "     官方对标口径是「Kirin 980 只支持浮点 AI 模型加速」。"
say "     => 若用 NNAPI/MNN 这条捷径，你拿到的是 FP16 NPU，不是 Int8 NPU。"
say ""
say "  B. 想要 Int8 NPU，必须走 HiAI DDK 离线模型（.nb / .om）这条路，"
say "     且 DDK 版本要与 Kirin 980 匹配（官方表：Kirin980 -> HIAI DDK V200）。"
say ""
say "  C. 在你决定投入之前，还有一个更便宜、也更可能改变结论的实验："
say "     先测「串流时 CPU 到底被占了多少、占了哪几个核」。"
say "     因为 980 的 CPU 本身能跑 60-100fps 级检测，"
say "     真正的问题可能是绑核而不是缺 NPU。"
say "     => bash bench/bench_stream_impact.py"
say ""
say "  D. 本次探测的关键读数："
kv "nnapi_lib_present" "$NNAPI_LIB_FOUND"
say "     nnapi/hiai 库存在        = $(cat "$KV" | grep '^nnapi_lib_present=' | cut -d= -f2)"
say "     NPU 设备节点存在          = $(cat "$KV" | grep '^npu_device_node_present=' | cut -d= -f2)"
say "     NPU 字符设备已注册        = $(cat "$KV" | grep '^npu_char_device_registered=' | cut -d= -f2)"
say "     NPU devfreq 节点          = $(cat "$KV" | grep '^npu_devfreq_present=' | cut -d= -f2)"
say "     NPU 中断行可见            = $(cat "$KV" | grep '^npu_irq_line_present=' | cut -d= -f2)"
say ""
say "  若以上 4 项中「NPU 字符设备已注册」或「NPU 中断行可见」为 1，"
say "  说明 NPU 硬件与驱动都在，问题只剩「怎么把模型送进去」。"
say "  若全为 0，再去翻 /system/lib64 与 /vendor/lib64，可能设备走了裁剪 ROM。"
say ""
say "=================================================================="
say "  完整报告: $TXT"
say "  摘要 KV : $KV"
say "=================================================================="

printf '\n%s\n' "$KV"
