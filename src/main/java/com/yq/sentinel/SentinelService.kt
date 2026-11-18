package com.yq.sentinel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat

/**
 * 检测前台服务。
 *
 * ★ 为什么必须有它（Android 10 / API 29 的硬限制）：
 *
 *   实测报错：
 *     "Caller com.yq.sentinel cannot open camera 1 from background
 *      (proc state 21)"
 *
 *   Android 10 起，App 处于后台时【不允许打开相机】。
 *   而「斗在吗」的场景恰恰是：手机锁屏当副屏、App 在后台持续检测。
 *   所以必须用前台服务 —— 前台服务可以让 App 保持「前台」状态，
 *   从而合法地持有相机。
 *
 *   这也是所有相机类后台 App（钉钉打卡、监控 App）的标准做法。
 *
 * ★ 通知是必须的：
 *   Android 要求前台服务必须显示一条常驻通知，用户可下拉看到。
 *   这也是「你正在被监控」这件事的透明化——对用户是好事。
 */
class SentinelService : Service() {

    companion object {
        private const val TAG = "SentinelSvc"
        const val CHANNEL_ID = "sentinel_detection"
        const val NOTIFICATION_ID = 1001

        fun start(context: Context) {
            val intent = Intent(context, SentinelService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, SentinelService::class.java))
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForegroundWithNotification()
        // 真正的检测由 MainActivity 驱动；这里只负责把进程提到前台，
        // 让 Activity 在后台时也能合法开相机。
        Log.i(TAG, "前台服务已启动")
        return START_STICKY
    }

    private fun startForegroundWithNotification() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = getString(R.string.notif_channel_desc)
                setShowBadge(false)
            }
            nm.createNotificationChannel(channel)
        }

        val notification: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(getString(R.string.notif_text))
            .setSmallIcon(R.drawable.ic_stat_sentinel)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // Android 10+ 必须指定 foreground service type
            startForeground(NOTIFICATION_ID, notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }
}
