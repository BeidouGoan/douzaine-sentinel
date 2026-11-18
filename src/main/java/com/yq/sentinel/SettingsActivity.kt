package com.yq.sentinel

import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.android.material.slider.Slider

/**
 * 「斗在吗」· 参数设置页。
 *
 * ★ 为什么单独一页：
 *   首屏只有一个任务——让人一眼看出「在不在」。
 *   参数是低频需求，铺在首屏会稀释状态这个主角。
 *
 * ★ 参数改完怎么办：
 *   不在这里重启检测（那需要跨页通信，复杂且容易出竞态），
 *   而是写入 SharedPreferences 后返回主屏，由主屏统一「停止 → 重启」。
 *   这样参数的生效时机只有一个地方，不会出现两处逻辑不一致。
 */
class SettingsActivity : AppCompatActivity() {

    private lateinit var settings: Settings

    private lateinit var absentSlider: Slider
    private lateinit var thresholdSlider: Slider
    private lateinit var fpsSlider: Slider
    private lateinit var pinSwitch: MaterialSwitch

    private lateinit var absentLabel: TextView
    private lateinit var thresholdLabel: TextView
    private lateinit var fpsLabel: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)

        settings = Settings(this)

        findViewById<com.google.android.material.appbar.MaterialToolbar>(R.id.toolbar)
            .setNavigationOnClickListener { finish() }

        absentSlider = findViewById(R.id.slider_absent_seconds)
        thresholdSlider = findViewById(R.id.slider_threshold)
        fpsSlider = findViewById(R.id.slider_fps)
        pinSwitch = findViewById(R.id.switch_pin_cpu)

        absentLabel = findViewById(R.id.label_absent_seconds)
        thresholdLabel = findViewById(R.id.label_threshold)
        fpsLabel = findViewById(R.id.label_fps)

        bindValues()
        bindListeners()
        updateAboutText()
    }

    private fun bindValues() {
        absentSlider.value = settings.absentSeconds.toFloat().coerceIn(
            absentSlider.valueFrom, absentSlider.valueTo
        )
        thresholdSlider.value = settings.threshold.toFloat().coerceIn(
            thresholdSlider.valueFrom, thresholdSlider.valueTo
        )
        fpsSlider.value = settings.fps.toFloat().coerceIn(
            fpsSlider.valueFrom, fpsSlider.valueTo
        )
        pinSwitch.isChecked = settings.pinToLittleCores

        absentLabel.text = getString(R.string.unit_seconds, settings.absentSeconds)
        thresholdLabel.text = String.format("%.2f", settings.threshold)
        fpsLabel.text = getString(R.string.unit_fps, settings.fps)
    }

    private fun bindListeners() {
        absentSlider.addOnChangeListener { _, value, _ ->
            settings.absentSeconds = value.toInt()
            absentLabel.text = getString(R.string.unit_seconds, value.toInt())
        }

        thresholdSlider.addOnChangeListener { _, value, _ ->
            settings.threshold = value
            thresholdLabel.text = String.format("%.2f", value)
        }

        fpsSlider.addOnChangeListener { _, value, _ ->
            settings.fps = value.toInt()
            fpsLabel.text = getString(R.string.unit_fps, value.toInt())
        }

        pinSwitch.setOnCheckedChangeListener { _, checked ->
            settings.pinToLittleCores = checked
        }
    }

    /**
     * 说明文案里带了「电脑连的哪个地址哪个端口」。
     * 这些信息 Settings 里没有（是运行时才知道的），
     * 所以放在主屏算好了通过 Intent 传进来。
     */
    private fun updateAboutText() {
        val host = intent.getStringExtra("host") ?: "本机"
        val port = intent.getIntExtra("port", 8765)
        findViewById<TextView>(R.id.about_body).text =
            getString(R.string.setting_about_body, host, port)
    }
}
