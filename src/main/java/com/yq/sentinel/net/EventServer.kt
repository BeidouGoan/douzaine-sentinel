package com.yq.sentinel.net

import android.os.Build
import android.util.Log
import org.json.JSONObject
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 事件上报：手机端起一个 TCP ServerSocket，PC 端连上来。
 *
 * ★ 为什么方向是「PC 连手机」而不是「手机连 PC」：
 *
 *   实测（某安卓真机 + Windows）：手机主动连 PC 的常见端口
 *   （445 / 22 / 47984 / 47989 / 47990）**全部被拒**——
 *   Windows 防火墙默认拦截入站。反过来 PC 连手机是出站，允许通过。
 *
 *   所以用 adb 反向隧道：
 *       adb reverse tcp:8765 tcp:8765
 *   手机只需在 localhost:8765 监听，PC 从 127.0.0.1:8765 连过来。
 *   全程走 USB，不需要任何网络配置。
 *
 * 协议：每行一个 JSON，以 \n 结尾。
 *   {"event":"hello","app":"sentinel","backend":"tflite-cpu","pid":12345,...}
 *   {"event":"presence","present":false,"confidence":0.0,"ts":...}
 *   {"event":"stats","fps":14.8,"infer_ms":12,"device":"cpu(TFLite)","ts":...}
 */
class EventServer(private val port: Int) {

    companion object {
        private const val TAG = "EventServer"

        const val EV_HELLO = "hello"
        const val EV_PRESENCE = "presence"
        const val EV_STATS = "stats"
        const val EV_CONFIG = "config"
        const val EV_PING = "ping"
    }

    private var serverSocket: ServerSocket? = null
    private var clientSocket: Socket? = null
    private var writer: BufferedWriter? = null
    private val running = AtomicBoolean(false)

    /**
     * ★ 记住最后一次配置，客户端连上时自动补发。
     *   否则 PC 端在 App 启动之后才连上的话，就永远拿不到 config
     *   （config 只在启动时发一次）。
     */
    @Volatile
    private var lastConfigJson: String? = null

    @Volatile
    var backend: String = "unknown"

    @Volatile
    var model: String = "ssd_mobilenet_v2"

    /**
     * 启动监听。非阻塞，连接在独立线程里处理。
     *
     * ★ 幂等：重复调用不会抛 "Address already in use"。
     *   配置变更时 MainActivity 会 stop→start 循环，
     *   如果这里不幂等，每次都会抛异常。
     *
     * ★ 双栈监听：
     *   Java 的 ServerSocket 默认绑定 IPv6 通配地址 (::)，此时 IPv4 连接
     *   需要走 v4-mapped 才能进来。而 adb reverse 转发到 127.0.0.1（IPv4），
     *   会连不上。所以这里显式同时开一个 IPv4 监听器。
     */
    @Synchronized
    fun start() {
        if (running.get()) {
            Log.d(TAG, "事件服务已在运行")
            return
        }
        serverSocket = ServerSocket(port)
        running.set(true)
        Thread({ acceptLoop() }, "EventServer-accept").apply {
            isDaemon = true
            start()
        }
        Log.i(TAG, "事件服务已监听 0.0.0.0:$port")

        // IPv4 监听（供 adb reverse / IPv4 直连使用）
        Thread({
            try {
                val v4 = ServerSocket(port, 8, java.net.InetAddress.getByName("0.0.0.0"))
                while (running.get()) {
                    try {
                        val s = v4.accept()
                        Log.i(TAG, "IPv4 连接: ${s.remoteSocketAddress}")
                        handleClient(s)
                    } catch (e: Exception) {
                        if (running.get()) Log.w(TAG, "IPv4 accept 异常", e)
                    }
                }
            } catch (e: Exception) {
                // 端口已被 IPv6 监听占用时属正常，忽略
                Log.d(TAG, "IPv4 监听未启动: ${e.message}")
            }
        }, "EventServer-accept-v4").apply {
            isDaemon = true
            start()
        }
    }

    private fun acceptLoop() {
        while (running.get()) {
            try {
                val s = serverSocket?.accept() ?: break
                Log.i(TAG, "PC 已连接: ${s.remoteSocketAddress}")
                handleClient(s)
            } catch (e: Exception) {
                if (running.get()) Log.w(TAG, "连接处理异常", e)
            }
        }
    }

    private fun handleClient(s: Socket) {
        clientSocket = s
        writer = BufferedWriter(OutputStreamWriter(s.getOutputStream(), "UTF-8"))
        sendHello()
        // ★ 补发配置：PC 端可能是在 App 启动之后才连上的，
        //   那样就收不到启动时那一次 config。
        lastConfigJson?.let { sendConfig(it) }
        readLoop()
        closeClient()
    }

    /** 保持连接并处理对端消息（目前只需要心跳回应）。 */
    private fun readLoop() {
        try {
            val input = BufferedReader(InputStreamReader(clientSocket!!.inputStream, "UTF-8"))
            while (running.get()) {
                val line = input.readLine() ?: break
                if (line.isBlank()) continue
                Log.d(TAG, "收到 PC 消息: $line")
                if (line.contains(EV_PING)) {
                    writeJson(JSONObject().apply { put("event", EV_PING) })
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "读取断开", e)
        }
    }

    private fun sendHello() {
        writeJson(JSONObject().apply {
            put("event", EV_HELLO)
            put("app", "sentinel")
            put("backend", backend)
            put("model", model)
            // ★ 上报 PID：PC 端拿到后就能用 taskset 给我们绑小核
            put("pid", android.os.Process.myPid())
            put("device", Build.MODEL)
            put("sdk", Build.VERSION.SDK_INT)
            put("ts", System.currentTimeMillis())
        })
    }

    /**
     * 上报在/不在状态。
     *
     * @param present    是否有人
     * @param confidence 最高人形置信度（没有则为 0）
     * @param state      状态机的三态："present" / "uncertain" / "absent"
     *
     * ★ state 必须发，不能只发 present：
     *   状态机的 UNCERTAIN 覆盖了整个「离开确认」窗口（默认 8 秒），
     *   也就是手机端还在数、还没判定「真走了」的那段时间。
     *   这期间 present 也是 false，但【不能】当成「无人」发给 PC——
     *   否则 PC 端一收到 false 就开始计时，8 秒确认期整个被吃掉。
     *   PC 端看到 "uncertain" 会什么都不做，等它给确定答案。
     */
    fun sendPresence(present: Boolean, confidence: Float, state: String) {
        writeJson(JSONObject().apply {
            put("event", EV_PRESENCE)
            put("present", present)
            put("state", state)
            put("confidence", confidence)
            put("ts", System.currentTimeMillis())
        })
    }

    /** 上报推理性能统计，用于 A/B 对照。 */
    fun sendStats(fps: Float, inferMs: Long, device: String) {
        writeJson(JSONObject().apply {
            put("event", EV_STATS)
            put("fps", fps)
            put("infer_ms", inferMs)
            put("device", device)
            put("ts", System.currentTimeMillis())
        })
    }

    /**
     * 上报手机端配置，让 PC 端 GUI 能显示「手机上现在配的是什么」。
     *
     * ★ 为什么需要：
     *   两边的配置是【叠加】的（手机 8 秒 + PC 额外缓冲），
     *   PC 端如果不知道手机端的设置，就会算错实际触发延迟。
     */
    fun sendConfig(configJson: String) {
        // 记住它，等客户端连上时补发
        lastConfigJson = configJson
        writeJson(JSONObject().apply {
            put("event", EV_CONFIG)
            put("config", JSONObject(configJson))
            put("ts", System.currentTimeMillis())
        })
    }

    private fun writeJson(o: JSONObject) {
        val w = writer ?: return
        try {
            synchronized(this) {
                w.write(o.toString())
                w.write("\n")
                w.flush()
            }
        } catch (e: Exception) {
            Log.w(TAG, "写入失败（PC 可能已断开）", e)
            closeClient()
        }
    }

    @Synchronized
    private fun closeClient() {
        try {
            writer?.close()
            writer = null
            clientSocket?.close()
            clientSocket = null
        } catch (_: Exception) {}
    }

    fun isConnected() = writer != null

    fun stop() {
        running.set(false)
        closeClient()
        try {
            serverSocket?.close()
        } catch (_: Exception) {}
    }
}
