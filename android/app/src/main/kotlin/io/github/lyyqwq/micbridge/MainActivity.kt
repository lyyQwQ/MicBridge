package io.github.lyyqwq.micbridge

import android.Manifest
import android.content.Intent
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import io.github.lyyqwq.micbridge.trust.DeviceTrustStore
import io.github.lyyqwq.micbridge.audio.AudioStateBus
import io.github.lyyqwq.micbridge.audio.BluetoothRouteManager
import io.github.lyyqwq.micbridge.audio.MicAudioSettings
import io.github.lyyqwq.micbridge.audio.MicAudioSettingsStore
import io.github.lyyqwq.micbridge.audio.MicrophoneService
import io.github.lyyqwq.micbridge.discovery.DiscoveryBus
import io.github.lyyqwq.micbridge.discovery.LanDiscovery
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    private val trust by lazy { DeviceTrustStore.get(this) }
    private var foreground = false
    private val routeManager by lazy(LazyThreadSafetyMode.NONE) { BluetoothRouteManager(this) }
    private val audioSettings by lazy(LazyThreadSafetyMode.NONE) { MicAudioSettingsStore.get(this) }
    private val discovery by lazy(LazyThreadSafetyMode.NONE) {
        LanDiscovery(this, { endpoints ->
            runOnUiThread { discoverySink?.success(endpoints.map(::computerMap)) }
        }, localDeviceId(), udpReceiver = false)
    }
    private var stateSubscription: (() -> Unit)? = null
    private var discoveryBusSubscription: (() -> Unit)? = null
    private var eventSink: EventChannel.EventSink? = null
    private var discoverySink: EventChannel.EventSink? = null
    private val inputDevicesChanged = object : AudioDeviceCallback() {
        override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>) {
            AudioStateBus.publish(AudioStateBus.current())
        }
        override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>) {
            AudioStateBus.publish(AudioStateBus.current())
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        getSystemService(AudioManager::class.java).registerAudioDeviceCallback(inputDevicesChanged, null)
        discoveryBusSubscription = DiscoveryBus.subscribe(discovery::accept)
        startListeningServiceIfPermitted()
        trust.failure()?.let { AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = it)) }
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL).setMethodCallHandler { call, result ->
            when (call.method) {
                "permissions" -> result.success(requiredPermissions().map { permission -> mapOf("permission" to permission, "granted" to (checkSelfPermission(permission) == 0)) })
                "requestPermissions" -> { requestPermissions(requiredPermissions().toTypedArray(), REQUEST_PERMISSIONS); result.success(null) }
                "devices" -> result.success(
                    listOf(mapOf("id" to BluetoothRouteManager.SYSTEM_INPUT_ID, "name" to "跟随系统", "address" to "", "type" to 0, "channels" to emptyList<Int>())) +
                        routeManager.enumerateInputs().map { device -> mapOf("id" to device.id, "name" to device.name, "address" to (device.address ?: ""), "type" to device.type, "channels" to device.channels.toList()) })
                "selectInput" -> {
                    val id = call.argument<Int>("id")
                    if (id == null) result.error("missing_input", "需要输入设备", null)
                    else runCatching {
                        routeManager.rememberInput(id)
                        AudioStateBus.publish(AudioStateBus.current())
                    }.onSuccess { result.success(null) }
                        .onFailure { result.error("input_selection_failed", it.message, null) }
                }
                "discoverComputers" -> { discovery.start(); result.success(null) }
                "stopDiscovery" -> { discovery.stop(); result.success(null) }
                "connect" -> {
                    val host = call.argument<String>("host")
                    val port = call.argument<Int>("port") ?: 17_891
                    when {
                        host.isNullOrBlank() || host.length > 255 -> result.error("missing_host", "需要有效的 Windows 核心地址", null)
                        port !in 1..65535 -> result.error("invalid_port", "电脑端口无效", null)
                        !foreground -> result.error("foreground_required", "请在手机前台点击连接", null)
                        checkSelfPermission(Manifest.permission.RECORD_AUDIO) != 0 -> result.error("permission_required", "请先授予麦克风权限", null)
                        else -> runCatching { startMicrophoneService(host, port, call.argument<String>("expectedDeviceId")) }
                            .onSuccess { result.success(null) }
                            .onFailure { result.error("connect_failed", it.message, null) }
                    }
                }
                "forgetDevice" -> {
                    runCatching { startService(Intent(this, MicrophoneService::class.java).setAction(MicrophoneService.ACTION_FORGET_DEVICE)) }
                        .onSuccess { result.success(null) }
                        .onFailure { result.error("forget_failed", it.message, null) }
                }
                "start" -> { sendServiceAction(MicrophoneService.ACTION_START_CAPTURE); result.success(null) }
                "stop" -> { sendServiceAction(MicrophoneService.ACTION_STOP_CAPTURE); result.success(null) }
                "status" -> result.success(snapshotMap(AudioStateBus.current()))
                "getAudioSettings" -> {
                    if (call.arguments != null) result.error("settings_invalid", "音频设置参数无效", null)
                    else audioSettings.read { settingsResult(result, it, "settings_read_failed") }
                }
                "setAudioSettings" -> {
                    val next = MicAudioSettings.fromWire(call.arguments)
                    if (next == null) result.error("settings_invalid", "音频设置参数无效", null)
                    else audioSettings.write(next) { settingsResult(result, it, "settings_persist_failed") }
                }
                else -> result.notImplemented()
            }
        }
        EventChannel(flutterEngine.dartExecutor.binaryMessenger, STATE_CHANNEL).setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                eventSink = events
                stateSubscription = AudioStateBus.subscribe { snapshot -> runOnUiThread { eventSink?.success(snapshotMap(snapshot)) } }
            }
            override fun onCancel(arguments: Any?) {
                stateSubscription?.invoke()
                stateSubscription = null
                eventSink = null
            }
        })
        EventChannel(flutterEngine.dartExecutor.binaryMessenger, DISCOVERY_CHANNEL).setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                discoverySink = events
                discovery.start()
            }
            override fun onCancel(arguments: Any?) {
                discovery.stop()
                discoverySink = null
            }
        })
    }

    override fun onResume() { super.onResume(); foreground = true }
    override fun onPause() { foreground = false; super.onPause() }

    override fun onDestroy() {
        getSystemService(AudioManager::class.java).unregisterAudioDeviceCallback(inputDevicesChanged)
        discoveryBusSubscription?.invoke()
        discovery.stop()
        stateSubscription?.invoke()
        super.onDestroy()
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_PERMISSIONS && checkSelfPermission(Manifest.permission.RECORD_AUDIO) == 0) {
            startListeningServiceIfPermitted()
            AudioStateBus.publish(AudioStateBus.current())
        }
    }

    private fun startListeningServiceIfPermitted() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != 0) return
        val intent = Intent(this, MicrophoneService::class.java).setAction(MicrophoneService.ACTION_LISTEN)
        runCatching { if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(intent) else startService(intent) }
            .onFailure { Toast.makeText(this, it.message ?: "Android 监听服务不可用", Toast.LENGTH_SHORT).show() }
    }

    private fun startMicrophoneService(host: String, port: Int, expectedDeviceId: String?) {
        val intent = Intent(this, MicrophoneService::class.java)
            .setAction(MicrophoneService.ACTION_CONNECT)
            .putExtra(MicrophoneService.EXTRA_HOST, host)
            .putExtra(MicrophoneService.EXTRA_PORT, port)
            .putExtra(MicrophoneService.EXTRA_EXPECTED_DEVICE_ID, expectedDeviceId)
            .putExtra(MicrophoneService.EXTRA_PAIRING_REQUESTED, foreground)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(intent) else startService(intent)
    }


    private fun sendServiceAction(action: String) {
        runCatching { startService(Intent(this, MicrophoneService::class.java).setAction(action)) }
            .onFailure { Toast.makeText(this, it.message ?: "服务尚未连接", Toast.LENGTH_SHORT).show() }
    }

    /** 设置读写在后台线程完成，结果回到主线程；只有确认成功才回报 success。 */
    private fun settingsResult(result: MethodChannel.Result, outcome: Result<Map<String, Any?>>, fallbackCode: String) = runOnUiThread {
        outcome.onSuccess(result::success).onFailure {
            result.error((it as? MicAudioSettingsStore.SettingsException)?.code ?: fallbackCode, it.message, null)
        }
    }

    private fun localDeviceId(): String? = runCatching { trust.installationId() }.getOrNull()
    private fun requiredPermissions(): List<String> = buildList {
        add(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            add(Manifest.permission.BLUETOOTH_CONNECT)
            add(Manifest.permission.BLUETOOTH_SCAN)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) add(Manifest.permission.POST_NOTIFICATIONS)
    }

    private fun computerMap(endpoint: LanDiscovery.ComputerEndpoint): Map<String, Any> = mapOf(
        "name" to endpoint.name,
        "host" to endpoint.host,
        "port" to endpoint.port,
        "deviceId" to endpoint.deviceId,
        "role" to endpoint.role,
        "sources" to endpoint.sources,
        "sinks" to endpoint.sinks,
        "transports" to endpoint.transports,
    )

    private fun snapshotMap(snapshot: AudioStateBus.Snapshot): Map<String, Any?> = mapOf(
        "state" to snapshot.state.wire,
        "device" to (snapshot.device ?: ""),
        "detail" to (snapshot.detail ?: ""),
        "connected" to snapshot.connected,
    ) + runCatching { routeManager.selectionSnapshot() }.getOrDefault(mapOf("selectedInputId" to BluetoothRouteManager.SYSTEM_INPUT_ID)) + trust.snapshot()

    companion object {
        private const val CHANNEL = "micbridge/platform"
        private const val STATE_CHANNEL = "micbridge/state"
        private const val DISCOVERY_CHANNEL = "micbridge/discovery"
        private const val REQUEST_PERMISSIONS = 4202
    }
}
