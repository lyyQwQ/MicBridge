package io.github.lyyqwq.micbridge.audio

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build

class AudioCaptureController(private val routeManager: BluetoothRouteManager) {
    private val lock = Any()
    private var recorder: AudioRecord? = null
    private var sender: ((ByteArray) -> Unit)? = null
    private var failure: ((String, String) -> Unit)? = null

    fun start(send: (ByteArray) -> Unit, onFailure: ((String, String) -> Unit)? = null): Result<String> = synchronized(lock) {
        if (recorder != null) return Result.success("already_streaming")
        failure = onFailure
        AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STARTING, detail = "selecting audio input", connected = true))
        try {
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
            sender = send
            record.startRecording()
            if (record.recordingState != AudioRecord.RECORDSTATE_RECORDING) throw IllegalStateException("AudioRecord did not enter recording state")
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && record.routedDevice?.id != device.id) {
                throw BluetoothRouteManager.RouteException("input_route_mismatch", "AudioRecord did not route to ${device.name}")
            }
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STREAMING, device.name, connected = true))
            Thread({ readLoop(record, device.id) }, "micbridge-audio-reader").start()
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
                runCatching { routeManager.clearCommunicationDevice() }
                AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.IDLE, connected = true))
                return
            }
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.STOPPING, connected = true))
            cleanupLocked()
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.IDLE, connected = true))
        }
    }

    private fun readLoop(record: AudioRecord, inputId: Int) {
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
        recorder = null
        sender = null
        failure = null
        if (current != null) {
            runCatching { current.stop() }
            runCatching { current.release() }
        }
        runCatching { routeManager.clearCommunicationDevice() }
    }

    private fun findNativeDevice(id: Int): android.media.AudioDeviceInfo {
        return routeManager.nativeInput(id)
            ?: throw BluetoothRouteManager.RouteException("device_disappeared", "Selected microphone disappeared before AudioRecord setup")
    }
}
