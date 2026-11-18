package com.yq.sentinel.detect

import android.content.Context
import android.graphics.Bitmap
import android.util.Log
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.MPImage
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.objectdetector.ObjectDetector
import com.google.mediapipe.tasks.vision.objectdetector.ObjectDetector.ObjectDetectorOptions

/**
 * MediaPipe Tasks ObjectDetector（CPU）。
 *
 * ★ 为什么用 MediaPipe Tasks 而不是裸 TFLite：
 *
 *   一开始我用的是裸 TFLite + MediaPipe 的 ssd_mobilenet_v2 模型，
 *   结果推理直接崩了：
 *     "Cannot copy from a TensorFlowLite tensor with shape [1, 12276, 4]
 *      to a Java object with shape [1, 10, 4]"
 *
 *   原因：MediaPipe 的模型【故意不含 NMS 与 anchor 解码】
 *   （官方导出时 add_postprocessing_op=False），
 *   12276 个框是原始 anchor 编码输出，需要自己实现 anchor 生成 + box 解码 + NMS。
 *
 *   MediaPipe Tasks 库把这些全包了：
 *     - anchor 生成（ssd_anchors_calculator）
 *     - box 解码（tflite_tensors_to_detections_calculator）
 *     - NMS（non_max_suppression_calculator）
 *   输出直接就是最终检测结果，API 干净。
 *
 * ★ 兼容性注意：
 *   - tasks-vision 的 minSdk 是 21，我们定 24，OK。
 *   - 它自带 jni/arm64-v8a/libmediapipe_tasks_vision_jni.so，**不需要 NDK**。
 *   - 首次运行会解压 native 库到 app 私有目录，有一次性开销（几秒）。
 *
 * ★ 人形类别：
 *   COCO 80 类，person = 0。
 *   （注意：裸 TFLite 那个 MediaPipe 模型的 labels.txt 第 0 行是 "background"，
 *     person 是 1；Tasks 库读的是模型 metadata 里的 COCO 标签，person = 0。）
 */
class PersonDetector(private val context: Context) {

    companion object {
        private const val TAG = "PersonDetector"

        /** COCO 80 类中 person 的索引 */
        const val LABEL_PERSON = 0

        /**
         * ★ 用标签字符串判定人形，不用索引。
         *   MediaPipe Tasks 的 Category.index() 是标签在列表中的序号，
         *   顺序随模型 metadata 变化；而 categoryName() 是稳定的字符串。
         */
        const val PERSON_LABEL_NAME = "person"

        /** 置信度阈值，低于此值不认为有人
         *
         *  ★ 由 MainActivity 在启动检测前写入 settings.threshold。
         *    不同步的话，设置页调的「多像才算人」其实一直是 0.4，
         *    模型加载和 hasPerson() 都用的是这个默认值。 */
        @Volatile
        var scoreThreshold: Float = 0.4f

        /** 最多返回几个检测框 */
        private const val MAX_RESULTS = 10
    }

    private var detector: ObjectDetector? = null

    /** 上一次的推理耗时（毫秒），只统计纯推理 */
    @Volatile
    var lastInferMs: Long = 0
        private set

    /** 最近一帧最高的检测置信度（不限类别），用于调阈值 */
    @Volatile
    var lastBestScore: Float = 0f
        private set

    /** 最近一帧最高分对应的类别名（调试用） */
    @Volatile
    var lastBestLabel: String = ""
        private set

    /** 预热是否完成 */
    @Volatile
    var warmedUp: Boolean = false
        private set

    /**
     * 加载模型。
     * @param modelPath assets 中的 tflite 文件路径
     */
    fun load(modelPath: String) {
        val options = ObjectDetectorOptions.builder()
            .setBaseOptions(
                BaseOptions.builder()
                    .setModelAssetPath(modelPath)
                    // CPU 后端。用 GPU 会跟串流抢 GPU，反而不划算——
                    // Gate 0 已证明 CPU 侧有足够余量。
                    .build()
            )
            .setRunningMode(RunningMode.IMAGE)
            .setMaxResults(MAX_RESULTS)
            .setScoreThreshold(scoreThreshold)
            .build()

        detector = ObjectDetector.createFromOptions(context, options)
        Log.i(TAG, "MediaPipe ObjectDetector 加载完成")
    }

    /**
     * 对一帧图像做检测。
     *
     * @param bitmap 已缩放到模型输入尺寸的位图
     */
    fun detect(bitmap: Bitmap): DetectionResult {
        val d = detector ?: return DetectionResult.empty(0, "uninit")

        val mpImage: MPImage = BitmapImageBuilder(bitmap).build()
        val t0 = System.nanoTime()
        // ★ 注意：try/catch 的两个分支类型必须一致。
        //   早先这里 catch 分支返回 DetectionResult.empty(...)，Kotlin 推断出的
        //   公共父类型变成了 Any，于是 result.detections() 解析不到，
        //   报一堆 "Unresolved reference: detections"。
        val result = try {
            d.detect(mpImage)
        } catch (e: Exception) {
            Log.e(TAG, "推理失败", e)
            null
        }
        val inferMs = (System.nanoTime() - t0) / 1_000_000L
        lastInferMs = inferMs

        if (result == null) {
            return DetectionResult.empty(inferMs, "error")
        }

        val boxes = ArrayList<DetectionResult.Box>()
        var best = 0f
        var bestName = ""
        for (det in result.detections()) {
            val top = det.categories().maxByOrNull { it.score() }
            val score = top?.score() ?: 0f
            if (score > best) {
                best = score
                bestName = top?.categoryName() ?: ""
            }
            if (score < scoreThreshold) continue
            // ★ 用 categoryName() 判定，而不是 index()：
            //   MediaPipe Tasks 的 index() 是标签在列表中的序号，
            //   不同模型/不同 metadata 的顺序可能不同，
            //   而 categoryName() 直接给字符串，稳。
            val labelId = if (top?.categoryName() == PERSON_LABEL_NAME) {
                LABEL_PERSON
            } else {
                -1
            }
            val b = det.boundingBox()
            boxes.add(
                DetectionResult.Box(
                    floatArrayOf(b.left, b.top, b.right, b.bottom),
                    labelId,
                    score
                )
            )
        }
        lastBestScore = best
        lastBestLabel = bestName
        return DetectionResult(boxes, inferMs, "cpu(MediaPipe)")
    }

    /** 预热：跑一次空图，消掉首次推理的初始化开销 */
    fun warmUp() {
        if (warmedUp) return
        // 用 int[] 构造，保证是软件位图（理由见 YuvConverter 的注释）
        val bmp = Bitmap.createBitmap(
            IntArray(256 * 256) { 0xFF000000.toInt() }, 256, 256, Bitmap.Config.ARGB_8888
        )
        detect(bmp)
        bmp.recycle()
        warmedUp = true
        Log.i(TAG, "预热完成")
    }

    fun close() {
        detector?.close()
        detector = null
    }
}
