package com.yq.sentinel.presence

/**
 * 有人/离开的状态机。
 *
 * ★ 这里有个反直觉但很重要的问题：为什么不直接「一帧检测不到人就触发」？
 *
 *   因为单帧检测天然是抖的。人稍微侧身、低头、光线变化，都会让某几帧
 *   检不到人。如果直接用单帧判断，你会收到一连串误报，
 *   PC 端就会疯狂锁屏——比不做还糟。
 *
 *   所以这里用两个不同维度的去抖：
 *   1) 连续帧计数（连续 N 帧没有人才算离开）—— 消除单帧抖动
 *   2) 时间阈值（离开持续 M 秒才触发）—— 消除短暂遮挡（比如人只是低头拿东西）
 *
 *   这两个参数是独立的，别混为一谈：
 *   - missingFrames 调小 → 反应快，但误报多
 *   - absentMillis 调小 → 反应快，但抗遮挡能力弱
 *
 *   对「斗在吗」这个场景，建议的起点是：
 *   missingFrames = 3（按 15fps 算，约 0.2 秒）
 *   absentMillis  = 8000（人真的走了 8 秒才触发）
 *   8 秒这个值不是拍脑袋：接水、去厕所、起身活动都在这个量级内，
 *   锁屏又不该在用户只是伸个懒腰时发生。
 */
class PresenceStateMachine(
    private val missingFrames: Int = 3,
    private val absentMillis: Long = 8000L
) {
    enum class State { PRESENT, ABSENT, UNCERTAIN }

    /** 连续未检出的帧数 */
    private var consecutiveMisses = 0

    /** 连续检出的帧数 */
    private var consecutiveHits = 0

    /** 首次进入「疑似无人」的时刻 */
    private var absentSince = 0L

    /** 上一次确认的状态 */
    var state: State = State.UNCERTAIN
        private set

    /**
     * 喂入一帧的检测结果。
     *
     * @param hasPerson 本帧是否检测到人
     * @param nowMillis 当前时间戳
     * @return 当前状态
     */
    fun update(hasPerson: Boolean, nowMillis: Long): State {
        if (hasPerson) {
            consecutiveHits++
            consecutiveMisses = 0
            if (state != State.PRESENT) {
                state = State.PRESENT
                absentSince = 0
            }
            return state
        }

        consecutiveHits = 0
        consecutiveMisses++

        // 还没连续丢够帧数 -> 保持原状态
        if (consecutiveMisses < missingFrames) {
            return state
        }

        // 连续丢够帧数了，但还要看离开持续了多久
        if (absentSince == 0L) {
            absentSince = nowMillis
        }
        val elapsed = nowMillis - absentSince
        state = if (elapsed >= absentMillis) State.ABSENT else State.UNCERTAIN
        return state
    }

    /** 是否已经确认无人（PC 端据此触发动作） */
    fun isConfirmedAbsent() = state == State.ABSENT

    /** 是否确认有人 */
    fun isConfirmedPresent() = state == State.PRESENT

    /** 距离下一次可能触发 ABSENT 还有多久（毫秒）；已 ABSENT 返回 0 */
    fun millisUntilAbsent(nowMillis: Long): Long {
        if (state == State.ABSENT) return 0
        if (absentSince == 0L) return absentMillis
        return maxOf(0, absentMillis - (nowMillis - absentSince))
    }

    /** 重置 */
    fun reset() {
        consecutiveMisses = 0
        consecutiveHits = 0
        absentSince = 0
        state = State.UNCERTAIN
    }
}
