package com.yq.sentinel.detect

/**
 * 一次检测的结果。
 *
 * 只关心「有没有人」这一个问题的项目，把置信度和框保留下来是为了：
 * 1) 让你能调阈值，而不是靠猜；
 * 2) 让你能区分「模型没跑起来」（框全空、置信度全 0）和「确实没人」。
 */
data class DetectionResult(
    val boxes: List<Box>,
    /** 纯推理耗时（毫秒），不含预处理 */
    val inferMs: Long,
    /** 实际执行位置描述 */
    val device: String
) {
    data class Box(
        /** 归一化坐标 [x0, y0, x1, y1]，取值 0~1 */
        val rect: FloatArray,
        val labelId: Int,
        val score: Float
    )

    /** 是否检测到人 */
    fun hasPerson(personLabelId: Int = PersonDetector.LABEL_PERSON): Boolean {
        return boxes.any { it.score >= PersonDetector.scoreThreshold && it.labelId == personLabelId }
    }

    /** 人形的最高置信度，没有则返回 0 */
    fun bestPersonScore(personLabelId: Int = PersonDetector.LABEL_PERSON): Float {
        return boxes.filter { it.labelId == personLabelId }.maxOfOrNull { it.score } ?: 0f
    }

    companion object {
        fun empty(inferMs: Long, device: String) =
            DetectionResult(emptyList(), inferMs, device)
    }
}
