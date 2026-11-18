package com.yq.sentinel

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.util.Log
import android.view.View
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.core.widget.ImageViewCompat
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.yq.sentinel.affinity.CpuAffinity
import com.yq.sentinel.camera.CameraFrameSource
import com.yq.sentinel.detect.DetectionResult
import com.yq.sentinel.detect.PersonDetector
import com.yq.sentinel.net.EventServer
import com.yq.sentinel.net.NetInfo
import com.yq.sentinel.presence.PresenceStateMachine

/**
 * 「斗在吗」· 状态主屏。
 *
 * 职责：把「相机 → 检测 → 状态机 → 上报」这条链路串起来，
 * 并用一屏说清楚「现在什么状态」。
 *
 * ★ 界面设计决策：
 *   首屏只回答两个问题——「现在什么状态」「开还是关」。
 *   所有检测参数移到了二级页（SettingsActivity），
 *   因为参数是低频需求，铺在首屏会稀释状态这个主角。
 *
 * ★ 状态机语义（沿用 PresenceStateMachine）：
 *   PRESENT   有人    → 绿
 *   ABSENT    确认无人 → 红
 *   UNCERTAIN 待确认  → 琥珀（去抖窗口内，不算离开）
 */
class MainActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "Sentinel"
        private const val REQ_CAMERA = 1001
        private const val EVENT_PORT = 8765

        /** 相机采集分辨率 */
        private const val CAMERA_SIZE = 320

        /** 可选的检测帧率 */
        private val FPS_OPTIONS = intArrayOf(5, 10, 15, 20)
    }

    private lateinit var settings: Settings

    /** 检测线程。刻意与 UI 线程分开 */
    private lateinit var detectThread: HandlerThread
    private lateinit var detectHandler: Handler

    private var camera: CameraFrameSource? = null
    private var detector: PersonDetector? = null
    private lateinit var stateMachine: PresenceStateMachine
    private lateinit var eventServer: EventServer

    // --- 视图 ---
    private lateinit var statusCard: MaterialCardView
    private lateinit var statusText: TextView
    private lateinit var statusHint: TextView
    private lateinit var statusDot: View
    private lateinit var statusGlow: View
    private lateinit var metricFps: TextView
    private lateinit var metricInfer: TextView
    private lateinit var metricConfidence: TextView
    private lateinit var pinHint: TextView
    private lateinit var netHint: TextView
    private lateinit var toggleBtn: MaterialButton

    /** 当前 WiFi 的 IPv4，空串表示没连 WiFi */
    private var currentIp = ""

    /** 主线程定时器：WiFi 可能中途切换，地址要跟着变 */
    private val uiHandler = Handler(Looper.getMainLooper())
    private val netTick = object : Runnable {
        override fun run() {
            refreshNetHint()
            uiHandler.postDelayed(this, 3000)
        }
    }

    private var frameCount = 0
    private var statWindowStart = System.currentTimeMillis()
    private var lastFps = 0f
    private var lastInferMs = 0L
    private var lastDevice = "-"
    private var lastBestScore = 0f
    private var lastBestLabel = ""

    /** 上一次上报给 PC 的状态，避免重复上报 */
    private var lastReportedPresent: Boolean? = null

    /** 是否把检测线程绑到小核 */
    @Volatile
    private var pinToLittleCores = true

    /** 上次打印检测详情的时间 */
    private var lastDebugLog = 0L

    /** 检测是否正在运行 */
    @Volatile
    private var running = false

    /** 呼吸动画的运行状态 */
    private var breathing = false

    // ------------------------------------------------------------------ //
    // 生命周期
    // ------------------------------------------------------------------ //

    override fun onCreate(savedInstanceState: Bundle?) {
        // ★ 深色模式强制开关（调试用）。
        //   设备不一定能切系统深色（EMUI 上 cmd uimode 常被拒），
        //   但界面长夜要放在工位上当副屏，必须能独立验证深色渲染。
        //   用法：adb shell am start -n com.yq.sentinel/.MainActivity --ez night yes
        //
        // ★ 必须在 super.onCreate() 之前调用：
        //   晚一步会触发 Activity 重建，onCreate 走两遍，检测线程会被建两次。
        if (intent?.getBooleanExtra("night", false) == true) {
            androidx.appcompat.app.AppCompatDelegate.setDefaultNightMode(
                androidx.appcompat.app.AppCompatDelegate.MODE_NIGHT_YES
            )
        }

        super.onCreate(savedInstanceState)

        setContentView(R.layout.activity_main)

        settings = Settings(this)

        statusCard = findViewById(R.id.status_card)
        statusText = findViewById(R.id.status_text)
        statusHint = findViewById(R.id.status_hint)
        statusDot = findViewById(R.id.status_dot)
        statusGlow = findViewById(R.id.status_glow)
        metricFps = findViewById(R.id.metric_fps)
        metricInfer = findViewById(R.id.metric_infer)
        metricConfidence = findViewById(R.id.metric_confidence)
        pinHint = findViewById(R.id.pin_hint)
        netHint = findViewById(R.id.net_hint)
        toggleBtn = findViewById(R.id.btn_toggle)

        detectThread = HandlerThread("Detect").also { it.start() }
        detectHandler = Handler(detectThread.looper)

        stateMachine = newStateMachine()
        eventServer = EventServer(EVENT_PORT).apply {
            backend = "tflite-cpu"
            model = "ssd_mobilenet_v2"
        }

        setupUi()
        applyIntent()

        requestCameraPermission()
    }

    /** 按当前设置重建状态机（去抖参数变了必须重建） */
    private fun newStateMachine(): PresenceStateMachine {
        val missing = settings.missingFrames
        val absentMs = settings.absentSeconds * 1000L
        // ★ 打出来，才能确认「设置页改的值」真的进了状态机。
        //   之前排查「改了还是 8 秒」全靠猜，就是缺这一行。
        Log.i(TAG, "状态机重建: missingFrames=$missing, absentMillis=$absentMs")
        return PresenceStateMachine(missingFrames = missing, absentMillis = absentMs)
    }

    // ------------------------------------------------------------------ //
    // UI
    // ------------------------------------------------------------------ //

    private fun setupUi() {
        toggleBtn.setOnClickListener {
            if (running) stopAll() else startAll()
        }

        findViewById<View>(R.id.btn_settings).setOnClickListener { openSettings() }
        findViewById<View>(R.id.btn_open_settings).setOnClickListener { openSettings() }

        // 点一下就把「IP:端口」复制走，PC 端直接粘贴
        netHint.setOnClickListener { copyPhoneAddr() }

        // 初始：未启动
        applyIdleState()
    }

    // ------------------------------------------------------------------ //
    //  本机地址（电脑端要填的就是这个）
    // ------------------------------------------------------------------ //

    override fun onResume() {
        super.onResume()
        // 从后台回来、或从设置页返回，WiFi 可能已经换了
        uiHandler.post(netTick)
    }

    override fun onPause() {
        super.onPause()
        uiHandler.removeCallbacks(netTick)
    }

    /**
     * 刷新「手机地址」那一行。
     * ★ 没 WiFi 时要说清楚为什么没有，不能留一个空白让用户猜。
     */
    private fun refreshNetHint() {
        val ip = NetInfo.wifiIpv4()
        val changed = ip != currentIp
        currentIp = ip
        if (changed && ip.isNotEmpty()) {
            // 顺手记住：设置页里「电脑端连的是哪个地址」要用它。
            // 只在变化时写，别每 3 秒刷一次 SharedPreferences。
            settings.lastPhoneIp = ip
        }
        if (ip.isEmpty()) {
            netHint.text = getString(R.string.net_hint_none)
            netHint.isClickable = false
        } else {
            netHint.text = getString(R.string.net_hint, "$ip:$EVENT_PORT")
            netHint.isClickable = true
        }
    }

    private fun copyPhoneAddr() {
        if (currentIp.isEmpty()) return
        val text = "$currentIp:$EVENT_PORT"
        try {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("sentinel-host", text))
            // Android 13 起系统会自己弹「已复制」浮层，再来一个 Toast 就重复了
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
                Toast.makeText(this, getString(R.string.net_copied, text), Toast.LENGTH_SHORT).show()
            }
        } catch (e: Exception) {
            Log.w(TAG, "复制地址失败", e)
        }
    }

    /**
     * 进设置页之前的参数快照。
     *
     * ★ 为什么要在「进入前」记，而不是返回后再读一次：
     *   Settings 是无状态的（每次都从 SharedPreferences 读），
     *   设置页已经把新值写进去了，返回后主屏再读一遍，
     *   拿到的就是新值——两次读同一份数据永远相等，
     *   「参数有没有变」根本判不出来，重启检测那段代码等于死代码。
     *   这就是「改了离开秒数、实际还是 8 秒」的根因。
     */
    private var snapshotBeforeSettings = ""

    private fun openSettings() {
        snapshotBeforeSettings = settings.snapshot()
        val intent = Intent(this, SettingsActivity::class.java)
        // 把 PC 连接信息传过去，说明文案里要显示「电脑连的是哪个地址」
        intent.putExtra("host", settings.lastPhoneIp.ifEmpty { "本机（USB 通道）" })
        intent.putExtra("port", EVENT_PORT)
        settingsLauncher.launch(intent)
    }

    private val settingsLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult()
    ) { _ ->
        syncSettingsFromSettingsPage()
    }

    /**
     * 从设置页返回。
     *
     * 参数变了必须重启检测（亲和性和帧率是线程级属性，
     * 去抖参数在状态机构造时定死），所以统一走「停止 → 重启」。
     */
    private fun syncSettingsFromSettingsPage() {
        settings = Settings(this)
        val after = settings.snapshot()
        if (snapshotBeforeSettings == after) {
            Log.i(TAG, "设置未变更，不重启检测")
            return
        }

        Log.i(TAG, "设置已变更: $snapshotBeforeSettings -> $after")
        stateMachine = newStateMachine()
        eventServer.sendConfig(settings.toJson())
        if (running) {
            stopAll()
            detectHandler.postDelayed({ startAll() }, 600)
        }
    }

    // ------------------------------------------------------------------ //
    // 命令行控制（供脚本 / PC 端调用）
    // ------------------------------------------------------------------ //

    /**
     * 支持：
     *   adb shell am start -n com.yq.sentinel/.MainActivity --ez autostart true
     *   adb shell am start -n com.yq.sentinel/.MainActivity --ez stop true
     *   adb shell am start -n com.yq.sentinel/.MainActivity --es threshold 0.35
     *   adb shell am start -n com.yq.sentinel/.MainActivity --ei absent_seconds 12
     *   adb shell am start -n com.yq.sentinel/.MainActivity --ez pin false
     */
    private fun applyIntent() {
        val i = intent ?: return
        val before = settings.snapshot()

        i.getStringExtra("threshold")?.toFloatOrNull()?.let { settings.threshold = it }
        i.getIntExtra("absent_seconds", -1).takeIf { it >= 0 }?.let { settings.absentSeconds = it }
        i.getBooleanExtra("pin", settings.pinToLittleCores).let {
            settings.pinToLittleCores = it
            pinToLittleCores = it
        }
        i.getIntExtra("fps", settings.fps).let { settings.fps = it }

        stateMachine = newStateMachine()

        // ★ 参数变了，正在跑的检测要跟着重启：
        //   帧率/绑核是线程属性，阈值也要重新 load 才彻底生效。
        //   不重启的话，改完参数只有「保存了」，没有「生效了」。
        if (running && settings.snapshot() != before) {
            Log.i(TAG, "参数已变更，重启检测: $before -> ${settings.snapshot()}")
            stopAll()
            detectHandler.postDelayed({ startAll() }, 600)
            return
        }

        when {
            i.getBooleanExtra("stop", false) -> stopAll()
            i.getBooleanExtra("autostart", false) ->
                detectHandler.postDelayed({ startAll() }, 600)
        }
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        setIntent(intent)
        applyIntent()
    }

    // ------------------------------------------------------------------ //
    // 检测控制
    // ------------------------------------------------------------------ //

    private fun startAll() {
        if (running) return
        running = true
        // ★ 阈值要在检测启动前同步。不同步的话，设置页调的「多像才算人」
        //   其实一直是 0.4，detector 从加载那一刻起就再没变过。
        PersonDetector.scoreThreshold = settings.threshold
        detectHandler.post {
            // ★ 事件服务要先起来。
            //   注意：这个必须放在最前面，且失败要打日志——
            //   漏掉它会导致 PC 端连不上，而 sendConfig 在没连接时
            //   会静默失败（writer 为 null），把问题掩盖掉。
            try {
                eventServer.start()
            } catch (e: Exception) {
                Log.e(TAG, "事件服务启动失败", e)
            }

            // ★ 必须先启动前台服务。
            //   Android 10 起，后台 App 不能开相机
            //   （实测报错: "cannot open camera from background"）。
            //   前台服务能让进程保持前台状态，从而合法持有相机。
            try {
                SentinelService.start(this)
            } catch (e: Exception) {
                Log.e(TAG, "前台服务启动失败", e)
            }

            // ★ 绑核要在检测线程里做（绑的是调用者线程），
            //   而且必须在创建相机/推理线程之前，
            //   这样后续创建的线程会继承这个亲和性。
            applyAffinityIfNeeded()

            if (detector == null) {
                val d = PersonDetector(this)
                try {
                    d.load("ssd_mobilenet_v2.tflite")
                    d.warmUp()
                    detector = d
                } catch (e: Exception) {
                    Log.e(TAG, "模型加载失败", e)
                    running = false
                    runOnUiThread {
                        applyErrorState("模型加载失败: ${e.message}")
                    }
                    return@post
                }
            }

            val cam = CameraFrameSource(
                this,
                // ★ 采集分辨率。MediaPipe 的 ssd_mobilenet_v2 输入是 256x256，
                //   但 Tasks 库内部会自己缩放，喂稍大的图反而更清晰。
                //   取 320 是权衡：比 256 清晰，又不让 YUV→RGB 转换太贵。
                CAMERA_SIZE,
                settings.fps
            ) { bmp, ts -> onFrame(bmp, ts) }
            camera = cam
            cam.start()

            eventServer.sendConfig(settings.toJson())
            runOnUiThread {
                setButtonRunning(true)
                applyUncertainState()
            }
        }
    }

    private fun stopAll() {
        if (!running) return
        running = false
        detectHandler.post {
            camera?.stop()
            camera = null
            try {
                SentinelService.stop(this)
            } catch (_: Exception) {}
            runOnUiThread {
                setButtonRunning(false)
                applyIdleState()
            }
        }
    }

    /**
     * 把检测线程绑到小核。
     *
     * ★ 必须在检测线程内调用：sched_setaffinity 绑的是调用者线程。
     * ★ 目标核由 native 层自动识别（按 max_freq 找最小的一档 = A55），
     *   不硬编码编号——不同 ROM 的核编号顺序不一样。
     */
    private fun applyAffinityIfNeeded() {
        if (!pinToLittleCores) {
            Log.i(TAG, "未绑核：检测线程可调度到全部核心")
            runOnUiThread { pinHint.visibility = View.GONE }
            return
        }
        if (!CpuAffinity.available) {
            Log.w(TAG, "绑核库不可用，跳过（推理照常运行）")
            return
        }
        val before = CpuAffinity.currentMask()
        val mask = CpuAffinity.pinCurrentThread()
        val after = CpuAffinity.currentMask()
        Log.i(TAG, "绑核: before=0x${before.toString(16)} after=0x${after.toString(16)}")
        runOnUiThread {
            pinHint.visibility = View.VISIBLE
            pinHint.text = if (mask > 0) {
                "已绑小核 0x${mask.toString(16)}（cpu${Integer.numberOfTrailingZeros(mask)} 起）"
            } else {
                "绑核失败，检测将在全部核心上调度"
            }
        }
    }

    /** 检测线程回调 */
    private fun onFrame(bmp: android.graphics.Bitmap, ts: Long) {
        val d = detector
        if (d == null) {
            bmp.recycle()
            return
        }
        val r: DetectionResult = try {
            d.detect(bmp)
        } catch (e: Exception) {
            Log.e(TAG, "检测异常", e)
            DetectionResult.empty(0, "error")
        } catch (e: Throwable) {
            // ★ native 层的 SIGSEGV 会以 Error 形式抛出，
            //   普通 Exception 抓不到。必须兜住 Throwable。
            Log.e(TAG, "检测崩溃（native）", e)
            DetectionResult.empty(0, "native-crash")
        }
        bmp.recycle()

        lastInferMs = r.inferMs
        lastDevice = r.device
        lastBestScore = d.lastBestScore
        lastBestLabel = d.lastBestLabel
        frameCount++

        val hasPerson = r.hasPerson()
        val st = stateMachine.update(hasPerson, ts)

        // 定期打印检测详情，便于确认模型真的在识别人
        if (System.currentTimeMillis() - lastDebugLog > 3000) {
            lastDebugLog = System.currentTimeMillis()
            Log.i(TAG, "检测: 框=${r.boxes.size} 最高分=${String.format("%.2f", d.lastBestScore)} " +
                    "类别=${d.lastBestLabel} 有人=$hasPerson 状态=$st 推理=${r.inferMs}ms")
        }

        val now = System.currentTimeMillis()
        if (now - statWindowStart >= 1000) {
            lastFps = frameCount * 1000f / (now - statWindowStart)
            frameCount = 0
            statWindowStart = now
            eventServer.sendStats(lastFps, lastInferMs, lastDevice)

            // ★ 状态快照：每秒发一次，不是只在状态迁移时发。
            //   PC 端要靠「持续收到 presence」来推进自己的离开计时，
            //   只在迁移时发的话 PC 端永远等不到下一条事件。
            // ★ 按状态机三态上报，别用「有没有检测到人」折成 true/false：
            //   UNCERTAIN（离开确认窗口）期间 hasPerson=false，但那不是「无人」，
            //   只是还在数那 8 秒。折平会让 PC 端提前动手。
            val stateCode = when (st) {
                PresenceStateMachine.State.PRESENT -> "present"
                PresenceStateMachine.State.ABSENT -> "absent"
                else -> "uncertain"
            }
            val present = st == PresenceStateMachine.State.PRESENT
            eventServer.sendPresence(present, r.bestPersonScore(), stateCode)
            lastReportedPresent = present

            val fps = lastFps
            val ms = lastInferMs
            val dev = lastDevice
            val best = lastBestScore
            val label = lastBestLabel
            val stateStr = when (st) {
                PresenceStateMachine.State.PRESENT -> "有人"
                PresenceStateMachine.State.ABSENT -> "无人"
                else -> "不确定"
            }
            runOnUiThread {
                metricFps.text = String.format("%.1f", fps)
                metricInfer.text = "${ms}ms"
                metricConfidence.text = String.format("%.2f", best)

                when (st) {
                    PresenceStateMachine.State.PRESENT -> applyPresentState()
                    PresenceStateMachine.State.ABSENT -> applyAbsentState()
                    else -> applyUncertainState()
                }
                if (label.isNotEmpty()) {
                    statusHint.text = statusHint.text.toString().substringBefore(" ·") + " · " + label
                }
            }
        }
    }

    // ------------------------------------------------------------------ //
    // 状态呈现
    // ------------------------------------------------------------------ //

    /**
     * 把三态映射成视觉。
     *
     * ★ 关键设计点：状态不只用颜色区分，还配了不同的文字。
     *   纯色弱视用户看不出绿/红/橙的区别，必须有文字兜底。
     */
    private fun applyPresentState() {
        statusText.setText(R.string.state_present)
        statusText.setTextColor(getColor(R.color.state_present))
        statusHint.setText(R.string.hint_present)
        statusDot.background?.mutate()?.setTint(getColor(R.color.state_present_glow))
        statusGlow.background?.mutate()?.setTint(getColor(R.color.state_present_glow))
        startBreathing()
    }

    private fun applyAbsentState() {
        statusText.setText(R.string.state_absent)
        statusText.setTextColor(getColor(R.color.state_absent))
        statusHint.setText(R.string.hint_absent)
        statusDot.background?.mutate()?.setTint(getColor(R.color.state_absent_glow))
        statusGlow.background?.mutate()?.setTint(getColor(R.color.state_absent_glow))
        startBreathing()
    }

    private fun applyUncertainState() {
        statusText.setText(R.string.state_uncertain)
        statusText.setTextColor(getColor(R.color.state_uncertain))
        statusHint.setText(R.string.hint_uncertain)
        statusDot.background?.mutate()?.setTint(getColor(R.color.state_uncertain_glow))
        statusGlow.background?.mutate()?.setTint(getColor(R.color.state_uncertain_glow))
        startBreathing()
    }

    private fun applyIdleState() {
        statusText.setText(R.string.state_idle)
        statusText.setTextColor(getColor(R.color.on_surface_muted))
        statusHint.setText(R.string.hint_idle)
        statusDot.background?.mutate()?.setTint(getColor(R.color.on_surface_muted))
        statusGlow.background?.mutate()?.setTint(getColor(R.color.on_surface_muted))
        stopBreathing()
        metricFps.setText(R.string.metric_value_placeholder)
        metricInfer.setText(R.string.metric_value_placeholder)
        metricConfidence.setText(R.string.metric_value_placeholder)
    }

    private fun applyErrorState(msg: String) {
        statusText.setText(R.string.state_absent)
        statusText.setTextColor(getColor(R.color.state_absent))
        statusHint.text = msg
        stopBreathing()
        setButtonRunning(false)
    }

    private fun setButtonRunning(isRunning: Boolean) {
        toggleBtn.setText(if (isRunning) R.string.action_stop else R.string.action_start)
        toggleBtn.setIconResource(if (isRunning) R.drawable.ic_stop else R.drawable.ic_play)
    }

    // ------------------------------------------------------------------ //
    // 呼吸动画：让「正在看着」这件事有生命感
    // ------------------------------------------------------------------ //

    private fun startBreathing() {
        if (breathing) return
        breathing = true
        statusGlow.animate()
            .scaleX(1f).scaleY(1f)
            .alpha(0.55f)
            .setDuration(1400)
            .withEndAction {
                if (!breathing) return@withEndAction
                statusGlow.animate()
                    .scaleX(1.6f).scaleY(1.6f)
                    .alpha(0f)
                    .setDuration(1400)
                    .withEndAction { startBreathing() }
                    .start()
            }
            .start()
    }

    private fun stopBreathing() {
        breathing = false
        statusGlow.animate().cancel()
        statusGlow.animate()
            .scaleX(1f).scaleY(1f).alpha(0f)
            .setDuration(0)
            .start()
    }

    // ------------------------------------------------------------------ //

    private fun requestCameraPermission() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        running = false
        detectHandler.post {
            camera?.stop()
            camera = null
            try {
                SentinelService.stop(this)
            } catch (_: Exception) {}
        }
        eventServer.stop()
        detector?.close()
        detectThread.quitSafely()
    }
}
