package com.yq.sentinel.affinity

import android.util.Log

/**
 * CPU 亲和性（绑核）封装。
 *
 * ★ 为什么必须由 App 自己绑，而不是从 PC 侧 `adb shell taskset`：
 *
 *   实测（某安卓真机 / Android 10）：
 *     adb shell taskset -p 0x0f <pid>  →  Permission denied
 *     adb root                          →  adbd cannot run as root in production builds
 *
 *   Linux 的 sched_setaffinity 对【其他进程】需要 CAP_SYS_NICE，
 *   Android 的 shell 用户和 production build 的 adbd 都没有。
 *   但线程【可以改自己的亲和性】，只要目标核在 cgroup cpuset 范围内。
 *
 * ★ 核编号不能硬编码：
 *   本机实测 cpu0-3 = A55 小核（CPU part 0xd05），cpu4-7 = A76 大核（0xd40）。
 *   但不同 ROM 的编号顺序可能不同，所以 [littleCoreMask] 从
 *   /sys/devices/system/cpu/cpuN/cpufreq/cpuinfo_max_freq 实读识别。
 */
object CpuAffinity {

    private const val TAG = "CpuAffinity"

    @Volatile
    private var loaded = false

    init {
        try {
            System.loadLibrary("sentinel_affinity")
            loaded = true
        } catch (e: UnsatisfiedLinkError) {
            Log.e(TAG, "加载 libsentinel_affinity.so 失败", e)
        }
    }

    /** native 层是否可用 */
    val available: Boolean get() = loaded

    /**
     * 自动识别小核（A55）并返回位掩码。
     * 例：cpu0-3 → 0x0f
     */
    fun littleCoreMask(): Int {
        if (!loaded) return -1
        return nativeLittleCoreMask()
    }

    /**
     * 把【当前线程】绑到指定 CPU 集合。
     *
     * ★ 必须在检测线程里调用，而不是在 UI 线程——
     *   绑的是「调用它的那个线程」。
     *
     * @param cpuMask 位掩码，bit0=cpu0。传 0 表示自动用小核。
     * @return 实际生效的掩码，失败返回 -1
     */
    fun pinCurrentThread(cpuMask: Int = 0): Int {
        if (!loaded) return -1
        val mask = if (cpuMask != 0) cpuMask else littleCoreMask()
        if (mask <= 0) {
            Log.e(TAG, "无法确定目标 CPU 掩码")
            return -1
        }
        return nativePinCurrentThread(mask)
    }

    /** 当前线程的亲和性掩码 */
    fun currentMask(): Int {
        if (!loaded) return -1
        return nativeGetAffinity()
    }

    private external fun nativePinCurrentThread(cpuMask: Int): Int
    private external fun nativeGetAffinity(): Int
    private external fun nativeLittleCoreMask(): Int
}
