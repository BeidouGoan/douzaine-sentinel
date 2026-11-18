package com.yq.sentinel

import android.content.Context
import android.content.SharedPreferences

/**
 * 手机端配置（持久化到 SharedPreferences）。
 *
 * ★ 职责划分（避免和 PC 端配置打架）：
 *
 *   手机端管【检测相关】：
 *     - 离开确认秒数：状态机的去抖窗口，防「低头/侧身被误判成离开」
 *     - 缺失帧数：连续几帧检不到才算离开
 *     - 置信度阈值：多低算没人
 *     - 绑小核：是否把检测固定在 cpu0-3
 *     - 检测帧率：15fps 够用，再高只是白烧电
 *
 *   PC 端管【动作相关】：
 *     - 执行哪些动作（通知/锁屏/暂停/命令）
 *     - 触发条件（离开/回来/两者）
 *     - 额外的离开缓冲秒数
 *
 *   两边的「离开延迟」是【叠加】的，不是覆盖关系。
 *   手机端默认 8 秒（必需），PC 端默认 0（信任手机端）。
 */
class Settings(context: Context) {

    private val prefs: SharedPreferences =
        context.getSharedPreferences("sentinel_settings", Context.MODE_PRIVATE)

    companion object {
        const val KEY_ABSENT_SECONDS = "absent_seconds"
        const val KEY_MISSING_FRAMES = "missing_frames"
        const val KEY_THRESHOLD = "threshold"
        const val KEY_PIN_CPU = "pin_cpu"
        const val KEY_FPS = "fps"
        const val KEY_LAST_PHONE_IP = "last_phone_ip"

        // 默认值
        const val DEF_ABSENT_SECONDS = 8
        const val DEF_MISSING_FRAMES = 3
        const val DEF_THRESHOLD = 0.4f
        const val DEF_PIN_CPU = true
        const val DEF_FPS = 15
    }

    /** 离开确认秒数：连续检测不到人这么久，才判定为「已离开」 */
    var absentSeconds: Int
        get() = prefs.getInt(KEY_ABSENT_SECONDS, DEF_ABSENT_SECONDS)
        set(v) = prefs.edit().putInt(KEY_ABSENT_SECONDS, v).apply()

    /** 连续多少帧检不到人，才开始计时 */
    var missingFrames: Int
        get() = prefs.getInt(KEY_MISSING_FRAMES, DEF_MISSING_FRAMES)
        set(v) = prefs.edit().putInt(KEY_MISSING_FRAMES, v).apply()

    /** 置信度阈值：低于此值不算有人 */
    var threshold: Float
        get() = prefs.getFloat(KEY_THRESHOLD, DEF_THRESHOLD)
        set(v) = prefs.edit().putFloat(KEY_THRESHOLD, v).apply()

    /** 是否把检测线程绑到小核 cpu0-3 */
    var pinToLittleCores: Boolean
        get() = prefs.getBoolean(KEY_PIN_CPU, DEF_PIN_CPU)
        set(v) = prefs.edit().putBoolean(KEY_PIN_CPU, v).apply()

    /** 检测帧率 */
    var fps: Int
        get() = prefs.getInt(KEY_FPS, DEF_FPS)
        set(v) = prefs.edit().putInt(KEY_FPS, v).apply()

    /** 上次连过的手机 IP，方便 PC 端重连 */
    var lastPhoneIp: String
        get() = prefs.getString(KEY_LAST_PHONE_IP, "") ?: ""
        set(v) = prefs.edit().putString(KEY_LAST_PHONE_IP, v).apply()

    /**
     * 当前配置的快照。
     *
     * ★ 用途：设置页返回时对比前后快照，判断参数是否真的变了。
     *   没变就不重启检测——否则每次从设置页返回都会打断一次检测，
     *   对正在盯梢的场景是不可接受的。
     */
    fun snapshot(): String = "$absentSeconds|$missingFrames|$threshold|$pinToLittleCores|$fps"

    /** 导出为 JSON 字符串，用于上报给 PC 端显示 */
    fun toJson(): String = """
        {"absent_seconds":$absentSeconds,
         "missing_frames":$missingFrames,
         "threshold":$threshold,
         "pin_cpu":$pinToLittleCores,
         "fps":$fps}
    """.trimIndent()
}
