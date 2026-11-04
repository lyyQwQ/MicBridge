package io.github.lyyqwq.micbridge.audio

/** 全局麦克风设置；效果偏好为 null 表示跟随系统，不调用 setEnabled。 */
data class MicAudioSettings(
    val gainDb: Int = 0,
    val ns: Boolean? = null,
    val aec: Boolean? = null,
    val agc: Boolean? = null,
) {
    init { require(gainDb in GAIN_RANGE) { "gainDb out of range: $gainDb" } }

    fun toWire(): Map<String, Any?> = mapOf("gainDb" to gainDb, "ns" to ns, "aec" to aec, "agc" to agc)

    companion object {
        val GAIN_RANGE = 0..20
        private val KEYS = setOf("gainDb", "ns", "aec", "agc")

        /** 本地通道边界：四个键必须齐全且无多余键，缺键不等于 null；非法返回 null。 */
        fun fromWire(raw: Any?): MicAudioSettings? {
            val map = raw as? Map<*, *> ?: return null
            if (map.keys != KEYS) return null
            val gainDb = map["gainDb"] as? Int ?: return null
            if (gainDb !in GAIN_RANGE) return null
            val effects = listOf("ns", "aec", "agc").map { key ->
                val value = map[key]
                if (value != null && value !is Boolean) return null
                value as Boolean?
            }
            return MicAudioSettings(gainDb, effects[0], effects[1], effects[2])
        }
    }
}

/** 写入者由 store 的串行执行器约束；读取者只能看到完整、不可变的已确认快照。 */
internal class ConfirmedAudioSettings(initial: MicAudioSettings) {
    @Volatile var snapshot: MicAudioSettings = initial
        private set

    fun save(next: MicAudioSettings, commit: (MicAudioSettings) -> Boolean, restoreFailed: () -> Unit): Boolean {
        val previous = snapshot
        if (!commit(next)) {
            // commit 失败也可能更新 SharedPreferences 内存；尽力恢复，但绝不发布失败的新值。
            if (!commit(previous)) restoreFailed()
            return false
        }
        snapshot = next
        return true
    }
}
