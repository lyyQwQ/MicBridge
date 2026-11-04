package io.github.lyyqwq.micbridge.audio

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import io.github.lyyqwq.micbridge.discovery.DiscoveryBus
import io.github.lyyqwq.micbridge.discovery.LanDiscovery
import io.github.lyyqwq.micbridge.trust.DeviceTrustStore
import java.net.ServerSocket
import java.util.concurrent.Executors

class MicrophoneService : Service() {
    private lateinit var capture: AudioCaptureController
    private lateinit var client: ControlClient
    private lateinit var trust: DeviceTrustStore
    private var discovery: LanDiscovery? = null
    private var endpoints = emptyList<LanDiscovery.ComputerEndpoint>()
    private val commands = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var server: ServerSocket? = null
    @Volatile private var destroyed = false
    @Volatile private var forgetting = false
    @Volatile private var requestGeneration = 0L
    private var foregroundStarted = false
    private var started = false
    private var retryPaused = false
    private var retryDelayMs = 1_000L
    private var retryScheduled = false
    private val reconnect = Runnable {
        retryScheduled = false
        if (!destroyed && !retryPaused && !client.isBusy()) reconnectTrusted()
    }

    override fun onCreate() {
        super.onCreate()
        trust = DeviceTrustStore.get(this)
        capture = AudioCaptureController(BluetoothRouteManager(this), MicAudioSettingsStore.get(this))
        client = ControlClient(capture, trust) { connected, retryAllowed ->
            main.post {
                if (!destroyed && connected == client.isConnected() && (connected || !client.isBusy())) {
                    if (connected) {
                        retryPaused = false
                        retryDelayMs = 1_000L
                        cancelRetry()
                    } else if (retryAllowed) {
                        scheduleRetry()
                    } else {
                        retryPaused = true
                        cancelRetry()
                    }
                }
            }
        }
        createNotificationChannel()
        startNetworking()
    }

    private fun startNetworking() {
        if (discovery != null || destroyed) return
        try {
            val deviceId = trust.installationId()
            val browser = LanDiscovery(this, { found ->
                main.post { if (!destroyed) endpoints = found.filter { it.role == "windows" } }
            }, deviceId, udpReceiver = true, onObserved = DiscoveryBus::publish)
            discovery = browser
            browser.start()
            val listener = ServerSocket(17_891)
            server = listener
            browser.register(listener.localPort, deviceId, trust.localDeviceName())
            Thread({
                while (!destroyed && !listener.isClosed) {
                    val socket = runCatching { listener.accept() }.getOrNull() ?: break
                    if (forgetting || destroyed) runCatching { socket.close() } else client.accept(socket)
                }
            }, "micbridge-control-listener").start()
        } catch (error: Exception) {
            retryPaused = error is DeviceTrustStore.StorageException
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR,
                detail = error.message ?: "Android 监听服务不可用"))
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_FORGET_DEVICE) {
            forgetDevice(!foregroundStarted || checkSelfPermission(Manifest.permission.RECORD_AUDIO) != 0, startId)
            return START_NOT_STICKY
        }
        try {
            val notification = buildNotification()
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            } else startForeground(NOTIFICATION_ID, notification)
            foregroundStarted = true
        } catch (error: Exception) {
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR,
                detail = "无法启动连接服务，请在手机前台授予麦克风权限：${error.message ?: "权限不可用"}"))
            stopSelf(startId)
            return START_NOT_STICKY
        }

        when (intent?.action) {
            ACTION_START_CAPTURE -> commands.execute { if (!destroyed) client.sendStart() }
            ACTION_STOP_CAPTURE -> commands.execute { if (!destroyed) client.sendStop() }
            ACTION_CONNECT -> {
                val host = intent.getStringExtra(EXTRA_HOST).orEmpty()
                val port = intent.getIntExtra(EXTRA_PORT, 17_891)
                val expected = intent.getStringExtra(EXTRA_EXPECTED_DEVICE_ID)
                // Only Activity's explicit foreground action can request first-time approval.
                val requestPairing = intent.getBooleanExtra(EXTRA_PAIRING_REQUESTED, false)
                retryPaused = false
                retryDelayMs = 1_000L
                cancelRetry()
                val request = ++requestGeneration
                commands.execute { if (!destroyed && request == requestGeneration) client.connect(host, port, requestPairing, expected) }
            }
            else -> if (!started) {
                retryPaused = trust.trustState() == "repair_required"
                scheduleRetry(immediate = true)
            }
        }
        started = true
        // Starting/listening/reconnecting never calls capture.start(). Only authenticated START does.
        return START_NOT_STICKY
    }

    private fun forgetDevice(stopAfter: Boolean, startId: Int) {
        val request = ++requestGeneration
        retryPaused = true
        forgetting = true
        cancelRetry()
        commands.execute {
            if (destroyed) return@execute
            try {
                client.forgetDevice()
            } finally {
                forgetting = false
                main.post {
                    if (!destroyed && request == requestGeneration) {
                        if (stopAfter) stopSelfResult(startId) else startNetworking()
                    }
                }
            }
        }
    }

    private fun scheduleRetry(immediate: Boolean = false) {
        if (destroyed || forgetting || retryPaused || retryScheduled || client.isBusy()) return
        val saved = try { trust.current() } catch (error: Exception) {
            retryPaused = true
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = error.message))
            return
        }
        if (saved == null) return
        retryScheduled = true
        main.postDelayed(reconnect, if (immediate) 0 else retryDelayMs)
        if (!immediate) retryDelayMs = (retryDelayMs * 2).coerceAtMost(30_000L)
    }

    private fun reconnectTrusted() {
        try {
            val saved = trust.current() ?: return
            if (trust.trustState() == "repair_required") { retryPaused = true; return }
            val found = endpoints.firstOrNull { it.deviceId == saved.deviceId }
            val host = found?.host ?: saved.host
            val port = found?.port ?: saved.port
            if (host.isBlank()) { scheduleRetry(); return }
            val request = requestGeneration
            commands.execute {
                if (!destroyed && !forgetting && request == requestGeneration && !client.isBusy()) {
                    client.connect(host, port, pairingRequested = false, expectedDeviceId = saved.deviceId)
                }
            }
        } catch (error: Exception) {
            retryPaused = true
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = error.message))
        }
    }

    private fun cancelRetry() { main.removeCallbacks(reconnect); retryScheduled = false }

    override fun onDestroy() {
        destroyed = true
        requestGeneration++
        cancelRetry()
        main.removeCallbacksAndMessages(null)
        runCatching { server?.close() }
        client.close()
        commands.shutdownNow()
        val browser = discovery
        discovery = null
        Thread({ browser?.stop() }, "micbridge-discovery-close").start()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createNotificationChannel() {
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "MicBridge", NotificationManager.IMPORTANCE_LOW))
    }
    private fun buildNotification(): Notification = Notification.Builder(this, CHANNEL_ID)
        .setContentTitle("MicBridge已就绪").setContentText("录音软件请求或手动开始后采集所选麦克风音频")
        .setSmallIcon(android.R.drawable.ic_btn_speak_now).setOngoing(true).build()

    companion object {
        const val EXTRA_HOST = "core_host"
        const val EXTRA_PORT = "core_port"
        const val EXTRA_EXPECTED_DEVICE_ID = "expected_device_id"
        const val EXTRA_PAIRING_REQUESTED = "pairing_requested"
        const val ACTION_CONNECT = "io.github.lyyqwq.micbridge.CONNECT"
        const val ACTION_FORGET_DEVICE = "io.github.lyyqwq.micbridge.FORGET_DEVICE"
        const val ACTION_START_CAPTURE = "io.github.lyyqwq.micbridge.START_CAPTURE"
        const val ACTION_STOP_CAPTURE = "io.github.lyyqwq.micbridge.STOP_CAPTURE"
        const val ACTION_LISTEN = "io.github.lyyqwq.micbridge.LISTEN"
        private const val CHANNEL_ID = "micbridge_mic"
        private const val NOTIFICATION_ID = 4201
    }
}
