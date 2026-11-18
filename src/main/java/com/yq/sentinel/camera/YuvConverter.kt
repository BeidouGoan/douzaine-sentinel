package com.yq.sentinel.camera

import android.graphics.Bitmap
import android.media.Image

/**
 * YUV_420_888 -> ARGB_8888 的最小实现。
 *
 * 只依赖 NDK-free 的纯 Kotlin，不引 OpenCV——这个项目的图像处理极简单
 * （通道转换 + 1:1 映射），引入 OpenCV 会让 APK 膨胀十几 MB，
 * 对「插着 USB 的副屏」场景毫无必要。
 *
 * ★ 注意 YUV_420_888 的 stride 不一定等于 width：
 *   要按 rowStride / pixelStride 逐行取样，不能当成连续数组直接算。
 *   这是 90% 的 YUV 转换 bug 来源。
 */
object YuvConverter {

    /**
     * 把一帧 YUV_420_888 转成 ARGB_8888 的 Bitmap。
     *
     * ★ 关键：必须保证是【软件位图】。
     *
     *   实测崩溃：
     *     Fatal signal 11 (SIGSEGV) in nativeCreateRgbaImage
     *     Cause: null pointer dereference
     *
     *   原因：某些 ROM（EMUI 尤其）上 `Bitmap.createBitmap` 可能返回
     *   【硬件位图】(HW Bitmap)，它的像素存在 GPU 里，
     *   native 侧 lockPixels 拿不到 CPU 指针 → 空指针崩溃。
     *
     *   解法：用 `Bitmap.createBitmap(int[] colors, ...)` 这个纯 Java 路径，
     *   它一定产生软件位图。虽然慢一点，但转换本来就要逐像素算，
     *   差别可以忽略。
     *
     * @param outWidth 输出宽（做最近邻缩放；本项目用 1:1 所以等于输入宽）
     * @param outHeight 输出高
     */
    fun toBitmap(image: Image, outWidth: Int, outHeight: Int): Bitmap? {
        val planes = image.planes
        if (planes.size < 3) return null

        val yBuf = planes[0].buffer
        val uBuf = planes[1].buffer
        val vBuf = planes[2].buffer

        val yRowStride = planes[0].rowStride
        val yPixelStride = planes[0].pixelStride
        val uRowStride = planes[1].rowStride
        val uPixelStride = planes[1].pixelStride
        val vRowStride = planes[2].rowStride
        val vPixelStride = planes[2].pixelStride

        val imgW = image.width
        val imgH = image.height

        val argb = IntArray(outWidth * outHeight)

        for (outY in 0 until outHeight) {
            val srcY = outY * imgH / outHeight
            for (outX in 0 until outWidth) {
                val srcX = outX * imgW / outWidth

                val yIndex = srcY * yRowStride + srcX * yPixelStride
                val y = if (yIndex < yBuf.limit()) (yBuf.get(yIndex).toInt() and 0xFF) else 0

                // UV 每 2x2 共享一组
                val uvX = srcX / 2
                val uvY = srcY / 2
                val uIndex = uvY * uRowStride + uvX * uPixelStride
                val vIndex = uvY * vRowStride + uvX * vPixelStride
                val u = if (uIndex < uBuf.limit()) (uBuf.get(uIndex).toInt() and 0xFF) - 128 else 0
                val v = if (vIndex < vBuf.limit()) (vBuf.get(vIndex).toInt() and 0xFF) - 128 else 0

                // BT.601 转换
                val r = clamp((y * 298 + 409 * v + 128) shr 8)
                val g = clamp((y * 298 - 100 * u - 208 * v + 128) shr 8)
                val b = clamp((y * 298 + 516 * u + 128) shr 8)

                argb[outY * outWidth + outX] = (0xFF shl 24) or (r shl 16) or (g shl 8) or b
            }
        }

        // ★ 用 int[] 这个重载，保证是软件位图（不经过 GPU 纹理）
        return Bitmap.createBitmap(argb, outWidth, outHeight, Bitmap.Config.ARGB_8888)
    }

    private fun clamp(v: Int) = if (v < 0) 0 else if (v > 255) 255 else v
}
