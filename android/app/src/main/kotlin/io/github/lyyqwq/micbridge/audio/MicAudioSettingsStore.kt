package io.github.lyyqwq.micbridge.audio

import android.content.Context
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.util.Log
import java.util.concurrent.Executors

/**
 * 进程内唯一的麦克风设置权威。独立 SharedPreferences 文件，不与输入选择或信任存储混用。
 * commit 失败时 SharedPreferences 内存可能已变，因此读取与采集只使用这里已确认的快照。
 */
class MicAudioSettingsStore private constructor(context: Context) {
    class SettingsException(val code: String, message: String) : Exception(message)

    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "micbridge-audio-settings").apply { isDaemon = true } }
    private val confirmed = ConfirmedAudioSettings(load())

    /** 采集启动时读取一次的不可变快照。 */
    fun snapshot(): MicAudioSettings = confirmed.snapshot

    /** 无副作用读取；回调在后台线程执行。 */
    fun read(callback: (Result<Map<String, Any?>>) -> Unit) = worker.execute {
        callback(runCatching { response(confirmed.snapshot, available()) })
    }

    /** 先查询能力再串行保存；只有 commit 成功才发布新快照并回报成功。 */
    fun write(next: MicAudioSettings, callback: (Result<Map<String, Any?>>) -> Unit) = worker.execute {
        callback(runCatching {
            val available = available()
            if (!confirmed.save(next, ::persist) { Log.w(TAG, "op=restore result=failed") }) {
                Log.w(TAG, "op=commit result=failed")
                throw SettingsException("settings_persist_failed", "音频设置保存失败")
            }
            response(next, available)
        })
    }

    private fun response(settings: MicAudioSettings, available: Map<String, Boolean>) =
        mapOf("preferences" to settings.toWire(), "available" to available)

    private fun available(): Map<String, Boolean> = try {
        mapOf(
            "ns" to NoiseSuppressor.isAvailable(),
            "aec" to AcousticEchoCanceler.isAvailable(),
            "agc" to AutomaticGainControl.isAvailable(),
        )
    } catch (error: Exception) {
        Log.w(TAG, "op=isAvailable exception=${error.javaClass.simpleName}")
        throw SettingsException("settings_read_failed", "音频设置读取失败")
    }

    private fun persist(settings: MicAudioSettings): Boolean = try {
        val editor = preferences.edit().putInt(GAIN_DB, settings.gainDb)
        // 跟随系统只移除对应键，不保存 null 或系统初始值。
        for ((key, value) in listOf(NS to settings.ns, AEC to settings.aec, AGC to settings.agc)) {
            if (value == null) editor.remove(key) else editor.putBoolean(key, value)
        }
        editor.commit()
    } catch (error: Exception) {
        Log.w(TAG, "op=commit exception=${error.javaClass.simpleName}")
        false
    }

    /** 逐键校验；损坏或类型错误的键回退默认，不清空其他设置。 */
    private fun load(): MicAudioSettings {
        val stored = runCatching { preferences.all }.getOrElse {
            Log.w(TAG, "op=load exception=${it.javaClass.simpleName}")
            emptyMap()
        }
        val invalid = mutableListOf<String>()
        val gainDb = when (val value = stored[GAIN_DB]) {
            null -> 0
            is Int -> if (value in MicAudioSettings.GAIN_RANGE) value else { invalid += GAIN_DB; 0 }
            else -> { invalid += GAIN_DB; 0 }
        }
        fun effect(key: String): Boolean? = when (val value = stored[key]) {
            null, is Boolean -> value as Boolean?
            else -> { invalid += key; null }
        }
        val settings = MicAudioSettings(gainDb, effect(NS), effect(AEC), effect(AGC))
        if (invalid.isNotEmpty()) Log.w(TAG, "op=load invalid_keys=${invalid.joinToString(",")}")
        return settings
    }

    companion object {
        private const val TAG = "MicAudioSettings"
        private const val PREFERENCES = "micbridge_audio_settings"
        private const val GAIN_DB = "gainDb"
        private const val NS = "ns"
        private const val AEC = "aec"
        private const val AGC = "agc"
        @Volatile private var instance: MicAudioSettingsStore? = null
        fun get(context: Context): MicAudioSettingsStore = instance ?: synchronized(this) {
            instance ?: MicAudioSettingsStore(context).also { instance = it }
        }
    }
}
