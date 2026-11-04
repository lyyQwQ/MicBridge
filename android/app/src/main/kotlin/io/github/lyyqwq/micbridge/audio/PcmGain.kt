package io.github.lyyqwq.micbridge.audio

import kotlin.math.pow
import kotlin.math.roundToInt

/** little-endian PCM16 软件增益；无 Android 依赖，供 protocol-check 直接编译自检。 */
internal object PcmGain {
    /** 每次采集启动预计算一次；20 dB 恰为 10 倍，0 dB 恰为 1.0 以走快路。 */
    fun factor(gainDb: Int): Double {
        require(gainDb in MicAudioSettings.GAIN_RANGE) { "gainDb out of range: $gainDb" }
        return 10.0.pow(gainDb / 20.0)
    }

    /** 原地放大有效 count 内的样本并饱和限幅；奇数 count 拒绝，避免发送半个样本。 */
    fun apply(buffer: ByteArray, count: Int, factor: Double) {
        require(count in 0..buffer.size && count % 2 == 0) { "PCM16 read returned invalid byte count: $count" }
        if (factor == 1.0) return
        var index = 0
        while (index < count) {
            val sample = (buffer[index].toInt() and 0xFF) or (buffer[index + 1].toInt() shl 8)
            // 先在 Int 上夹取再写回两个字节，不能先转 Short，否则会整数回绕。
            val scaled = (sample * factor).roundToInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
            buffer[index] = scaled.toByte()
            buffer[index + 1] = (scaled shr 8).toByte()
            index += 2
        }
    }
}
