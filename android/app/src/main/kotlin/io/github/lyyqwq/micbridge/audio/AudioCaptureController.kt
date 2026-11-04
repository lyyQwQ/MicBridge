package io.github.lyyqwq.micbridge.audio

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AudioEffect
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.os.Build
import android.util.Log

class AudioCaptureController(
    private val routeManager: BluetoothRouteManager,
    private val settings: MicAudioSettingsStore,
) {
    private val lock = Any()
    private var recorder: AudioRecord? = null
    private var sender: ((ByteArray) -> Unit)? = null
    private var failure: ((String, String) -> Unit)? = null
    private var effects: PreprocessEffects? = null
    // 匿名会话代次，仅用于把效果诊断日志对应到某次采集。
    private var generation = 0L

    fun start(send: (ByteArray) -> Unit, onFailure: ((String, String) -> Unit)? = null): Result<String> = synchronized(lock) {
        if (recorder != null) return Result.success("already_streaming")
        failure = onFailure
        AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STARTING, detail = "selecting audio input", connected = true))
        try {
            // 每次 START 只取一次已确认快照；保存设置不影响正在进行的采集。
            val snapshot = settings.snapshot()
            val gainFactor = PcmGain.factor(snapshot.gainDb)
            val device = routeManager.selectInput()
            routeManager.useCommunicationDevice(device)
            val minimum = AudioRecord.getMinBufferSize(
                48_000,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
            )
            if (minimum <= 0) throw IllegalStateException("Android returned no valid AudioRecord buffer size")
            val record = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                AudioRecord.Builder()
                    .setAudioSource(MediaRecorder.AudioSource.VOICE_COMMUNICATION)
                    .setAudioFormat(AudioFormat.Builder().setSampleRate(48_000).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_IN_MONO).build())
                    .setBufferSizeInBytes(maxOf(minimum * 2, 4096))
                    .build()
            } else {
                @Suppress("DEPRECATION")
                AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, 48_000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(minimum * 2, 4096))
            }
            // Own the resource before route validation so every exception releases it.
            recorder = record
            if (record.state != AudioRecord.STATE_INITIALIZED) {
                throw IllegalStateException("AudioRecord failed to initialize for ${device.name}")
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && !record.setPreferredDevice(findNativeDevice(device.id))) {
                throw BluetoothRouteManager.RouteException("preferred_device_rejected", "AudioRecord rejected the selected microphone")
            }
            effects = openEffects(record.audioSessionId, snapshot)
            sender = send
            record.startRecording()
            if (record.recordingState != AudioRecord.RECORDSTATE_RECORDING) throw IllegalStateException("AudioRecord did not enter recording state")
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && record.routedDevice?.id != device.id) {
                throw BluetoothRouteManager.RouteException("input_route_mismatch", "AudioRecord did not route to ${device.name}")
            }
            effects?.logState()
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STREAMING, device.name, connected = true))
            Thread({ readLoop(record, device.id, gainFactor) }, "micbridge-audio-reader").start()
            Result.success(device.name)
        } catch (error: Exception) {
            val code = (error as? BluetoothRouteManager.RouteException)?.code ?: "capture_start_failed"
            val callback = failure
            cleanupLocked()
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = error.message ?: "capture start failed", connected = true))
            callback?.invoke(code, error.message ?: "capture start failed")
            Result.failure(error)
        }
    }

    fun stop() {
        synchronized(lock) {
            if (recorder == null) {
                cleanupLocked()
                AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.IDLE, connected = true))
                return
            }
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STOPPING, connected = true))
            cleanupLocked()
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.IDLE, connected = true))
        }
    }

    private fun readLoop(record: AudioRecord, inputId: Int, gainFactor: Double) {
        val buffer = ByteArray(4096)
        try {
            while (true) {
                synchronized(lock) { if (recorder !== record) return }
                val count = record.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
                if (count < 0) throw IllegalStateException("AudioRecord read failed: $count")
                if (count > 0) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                        val routed = record.routedDevice
                        if (routed != null && routed.id != inputId) {
                            throw BluetoothRouteManager.RouteException("input_route_lost", "Selected microphone is no longer the active recording input")
                        }
                    }
                    // 系数随会话固定，音频线程不读取可变设置；奇数 count 抛错走统一清理。
                    PcmGain.apply(buffer, count, gainFactor)
                    val activeSender = synchronized(lock) {
                        if (recorder === record) sender else null
                    }
                    activeSender?.invoke(buffer.copyOf(count))
                }
            }
        } catch (error: Exception) {
            synchronized(lock) {
                if (recorder === record) {
                    val callback = failure
                    cleanupLocked()
                    val message = error.message ?: "audio read failed"
                    AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = message, connected = true))
                    callback?.invoke((error as? BluetoothRouteManager.RouteException)?.code ?: "audio_read_failed", message)
                }
            }
        }
    }

    private fun cleanupLocked() {
        val current = recorder
        val sessionEffects = effects
        recorder = null
        sender = null
        failure = null
        effects = null
        if (current != null) runCatching { current.stop() }
        // 效果先于 recorder 释放；单项异常已在内部隔离。
        runCatching { sessionEffects?.release() }
        if (current != null) runCatching { current.release() }
        runCatching { routeManager.clearCommunicationDevice() }
    }

    /** 偏好为 null 的效果仍创建以记录系统初始状态，但绝不调用 setEnabled。 */
    private fun openEffects(sessionId: Int, snapshot: MicAudioSettings): PreprocessEffects {
        val session = ++generation
        fun request(name: String, preference: Boolean?, available: () -> Boolean, create: (Int) -> AudioEffect?) =
            PreprocessEffects.Request(name, preference) { if (available()) create(sessionId)?.let(::SystemEffect) else null }
        return PreprocessEffects(
            listOf(
                request("ns", snapshot.ns, NoiseSuppressor::isAvailable, NoiseSuppressor::create),
                request("aec", snapshot.aec, AcousticEchoCanceler::isAvailable, AcousticEchoCanceler::create),
                request("agc", snapshot.agc, AutomaticGainControl::isAvailable, AutomaticGainControl::create),
            ),
        ) { warn, message -> if (warn) Log.w(TAG, "session=$session $message") else Log.d(TAG, "session=$session $message") }
    }

    private class SystemEffect(private val effect: AudioEffect) : PreprocessEffect {
        override fun hasControl() = effect.hasControl()
        override fun getEnabled() = effect.enabled
        override fun setEnabled(enabled: Boolean) = effect.setEnabled(enabled)
        override fun release() = effect.release()
    }

    private fun findNativeDevice(id: Int): android.media.AudioDeviceInfo {
        return routeManager.nativeInput(id)
            ?: throw BluetoothRouteManager.RouteException("device_disappeared", "Selected microphone disappeared before AudioRecord setup")
    }

    private companion object {
        const val TAG = "AudioCapture"
    }
}
