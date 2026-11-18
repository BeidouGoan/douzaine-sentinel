package com.yq.sentinel.camera

import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageFormat
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.Image
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Size
import android.view.Surface

/**
 * Camera2 取帧器。
 *
 * 只做一件事：把预览流以 YUV_420_888 格式交给检测线程。
 * 帧率是刻意压低的（默认 15fps）——理由见下方「为什么 15fps 就够」。
 *
 * ★ 为什么 15fps 就够：
 *
 *   这个项目的判定目标是「人还在不在工位」，不是「人做了什么动作」。
 *   人的离开动作本身要持续好几秒，15fps 采样完全够用。
 *
 *   更重要的是：帧率直接决定 CPU 占用。如果你要的是 30fps，
 *   那 CPU 侧的负载和功耗都会翻倍，你的「不抢串流 CPU」论点就先自证失败了。
 *
 *   30fps → 15fps 的收益在 980 上是实打实的：
 *   按 MobileNet v2 fp16 实测 14.81ms/帧算，30fps 会吃掉接近半个大核。
 *   15fps 才是这个规模模型该有的档位。
 *
 * ★ 关于输出尺寸：
 *   直接让 Camera2 采集 [inputSize] 分辨率的帧，这样 YUV→RGB 转换时
 *   1:1 映射，不需要额外的缩放步骤，省一次内存拷贝和 CPU 周期。
 */
class CameraFrameSource(
    private val context: Context,
    private val inputSize: Int,
    private val targetFps: Int,
    private val listener: FrameListener
) {
    fun interface FrameListener {
        fun onFrame(bitmap: Bitmap, timestampMs: Long)
    }

    companion object {
        private const val TAG = "CameraFrame"
    }

    private var cameraDevice: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var imageReader: ImageReader? = null
    private var bgThread: HandlerThread? = null
    private var bgHandler: Handler? = null

    @Volatile
    private var running = false
    private var lastFrameTime = 0L
    private val frameIntervalNanos = 1_000_000_000L / targetFps

    fun start() {
        running = true
        val t = HandlerThread("CameraFrame").also { it.start() }
        bgThread = t
        bgHandler = Handler(t.looper)
        try {
            openCamera()
        } catch (e: Exception) {
            Log.e(TAG, "打开相机失败", e)
        }
    }

    private fun openCamera() {
        val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        // 优先用前摄：斗在吗对着的是自己，前摄位置更合适
        var cameraId: String? = null
        for (id in manager.cameraIdList) {
            val facing = manager.getCameraCharacteristics(id)
                .get(CameraCharacteristics.LENS_FACING)
            if (facing == CameraCharacteristics.LENS_FACING_FRONT) {
                cameraId = id
                break
            }
        }
        if (cameraId == null && manager.cameraIdList.isNotEmpty()) {
            cameraId = manager.cameraIdList[0]
        }
        if (cameraId == null) throw IllegalStateException("没有可用相机")

        val reader = ImageReader.newInstance(
            inputSize, inputSize, ImageFormat.YUV_420_888, 2
        )
        imageReader = reader
        reader.setOnImageAvailableListener({ r ->
            val image = r.acquireNextImage() ?: return@setOnImageAvailableListener
            try {
                val now = System.nanoTime()
                if (now - lastFrameTime < frameIntervalNanos) {
                    return@setOnImageAvailableListener   // 丢帧，控 CPU
                }
                lastFrameTime = now
                val bmp = YuvConverter.toBitmap(image, inputSize, inputSize)
                if (bmp != null) {
                    listener.onFrame(bmp, System.currentTimeMillis())
                }
            } finally {
                image.close()
            }
        }, bgHandler)

        manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
            override fun onOpened(camera: CameraDevice) {
                cameraDevice = camera
                createSession()
            }

            override fun onDisconnected(camera: CameraDevice) {
                camera.close()
            }

            override fun onError(camera: CameraDevice, error: Int) {
                camera.close()
                Log.e(TAG, "相机错误: $error")
            }
        }, bgHandler)
    }

    private fun createSession() {
        val camera = cameraDevice ?: return
        val reader = imageReader ?: return
        try {
            val surface = reader.surface
            val builder = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                addTarget(surface)
                set(
                    CaptureRequest.CONTROL_AF_MODE,
                    CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO
                )
                // 帧率范围锁到目标值，避免驱动自己拉高。
                // ★ 注意：CONTROL_AE_TARGET_FPS_RANGE 的类型是 Range<Int>，
                //   不是 IntArray —— 传错类型编译期就会报 "expected Range<Int>"。
                set(
                    CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                    android.util.Range(targetFps, targetFps)
                )
            }
            camera.createCaptureSession(
                listOf(surface),
                object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(s: CameraCaptureSession) {
                        session = s
                        try {
                            s.setRepeatingRequest(builder.build(), null, bgHandler)
                            Log.i(TAG, "预览就绪 ${inputSize}x$inputSize@$targetFps")
                        } catch (e: CameraAccessException) {
                            Log.e(TAG, "设置重复请求失败", e)
                        }
                    }

                    override fun onConfigureFailed(s: CameraCaptureSession) {
                        Log.e(TAG, "会话配置失败")
                    }
                },
                bgHandler
            )
        } catch (e: CameraAccessException) {
            Log.e(TAG, "创建会话失败", e)
        }
    }
    fun stop() {
        running = false
        try {
            session?.stopRepeating()
            session?.close()
        } catch (_: Exception) {}
        cameraDevice?.close()
        imageReader?.close()
        bgThread?.quitSafely()
        bgThread = null
    }
}
