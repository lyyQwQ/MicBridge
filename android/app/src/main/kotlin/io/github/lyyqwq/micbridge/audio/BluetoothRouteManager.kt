package io.github.lyyqwq.micbridge.audio

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

class BluetoothRouteManager(private val context: Context) {
    data class Device(val id: Int, val name: String, val address: String?, val type: Int, val channels: IntArray)

    companion object {
        const val SYSTEM_INPUT_ID = -1
    }

    private val preferences = context.applicationContext.getSharedPreferences("microphone_input", Context.MODE_PRIVATE)

    fun rememberInput(id: Int) {
        if (id == SYSTEM_INPUT_ID) {
            preferences.edit().clear().apply()
            return
        }
        val device = enumerateInputs().firstOrNull { it.id == id }
            ?: throw RouteException("input_device_not_found", "Selected microphone is no longer available")
        rememberInput(device)
    }

    private fun rememberInput(device: Device) {
        preferences.edit().putInt("id", device.id).putInt("type", device.type)
            .putString("address", device.address).putString("name", device.name).apply()
    }

    fun selectedInput(): Device? {
        val inputs = enumerateInputs()
        if (preferences.contains("type")) {
            // Android device IDs can change after a restart or Bluetooth reconnection.
            val type = preferences.getInt("type", -1)
            val address = preferences.getString("address", null)
            val name = preferences.getString("name", null)
            return inputs.firstOrNull { it.type == type &&
                (if (!address.isNullOrBlank()) it.address.equals(address, ignoreCase = true) else it.name == name) }
        }
        return null
    }

    fun selectionSnapshot(): Map<String, Any> {
        val selected = selectedInput()
        val detail = if (selected == null && preferences.contains("type")) "已保存的设备未连接，暂按系统默认" else ""
        return mapOf(
            "selectedInputId" to (selected?.id ?: SYSTEM_INPUT_ID),
            "inputSelectionDetail" to detail,
        )
    }

    fun enumerateInputs(): List<Device> {
        val manager = context.getSystemService(AudioManager::class.java)
        return manager.getDevices(AudioManager.GET_DEVICES_INPUTS)
            .filter(::isSelectableInput)
            .distinctBy { device ->
                if (device.type == AudioDeviceInfo.TYPE_BUILTIN_MIC) {
                    "built-in-microphone"
                } else {
                    "${device.type}:${device.address}:${device.productName}"
                }
            }
            .map { device ->
                Device(device.id, device.productName?.toString() ?: "Unknown input", device.address.takeIf { it.isNotBlank() }, device.type, device.channelCounts)
            }
    }

    private fun isSelectableInput(device: AudioDeviceInfo): Boolean {
        return device.type == AudioDeviceInfo.TYPE_BUILTIN_MIC ||
            device.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO ||
            device.type == AudioDeviceInfo.TYPE_WIRED_HEADSET ||
            device.type == AudioDeviceInfo.TYPE_WIRED_HEADPHONES ||
            device.type == AudioDeviceInfo.TYPE_USB_DEVICE ||
            device.type == AudioDeviceInfo.TYPE_USB_ACCESSORY ||
            device.type == AudioDeviceInfo.TYPE_USB_HEADSET ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && device.type == AudioDeviceInfo.TYPE_BLE_HEADSET)
    }

    fun nativeInput(id: Int): AudioDeviceInfo? {
        val manager = context.getSystemService(AudioManager::class.java)
        return manager.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.id == id }
    }

    fun selectInput(): Device = selectedInput() ?: systemInput()

    private fun systemInput(): Device {
        val manager = context.getSystemService(AudioManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) manager.clearCommunicationDevice()
        manager.mode = AudioManager.MODE_IN_COMMUNICATION
        val inputs = enumerateInputs()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            // Android documents communicationDevice as the currently selected communication
            // device. Clear our previous override before reading it in communication mode.
            // setCommunicationDevice selects an output sink; Android chooses its matching input.
            // availableCommunicationDevices is only a list of selectable sinks, NOT a priority list.
            // https://developer.android.com/reference/android/media/AudioManager#getCommunicationDevice()
            val current = manager.communicationDevice
            if (current != null) matchingInput(current, inputs)?.let { return it }
            // Compatibility fallback only when the system route has no usable matching input.
            // This headset preference is application policy, not Android's documented ordering.
            for (candidate in manager.availableCommunicationDevices) {
                matchingInput(candidate, inputs)?.takeIf { it.type != AudioDeviceInfo.TYPE_BUILTIN_MIC }
                    ?.let { return it }
            }
        }
        return inputs.firstOrNull { it.type != AudioDeviceInfo.TYPE_BUILTIN_MIC }
            ?: inputs.firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC }
            ?: throw RouteException("input_device_not_found", "没有可用的麦克风输入")
    }

    private fun matchingInput(output: AudioDeviceInfo, inputs: List<Device>): Device? {
        if (output.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER || output.type == AudioDeviceInfo.TYPE_BUILTIN_EARPIECE) {
            return inputs.firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC }
        }
        val candidates = inputs.filter { it.type == output.type }
        return candidates.firstOrNull { !output.address.isNullOrBlank() && it.address == output.address }
            ?: candidates.firstOrNull { it.name == output.productName?.toString() }
            ?: candidates.singleOrNull()
    }

    fun useCommunicationDevice(device: Device) {
        val manager = context.getSystemService(AudioManager::class.java)
        manager.mode = AudioManager.MODE_IN_COMMUNICATION
        if (isBluetoothInput(device)) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                activateCommunicationDevice(manager, device)
            } else {
                activateLegacySco(manager)
            }
        } else {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) manager.clearCommunicationDevice()
            @Suppress("DEPRECATION")
            manager.isBluetoothScoOn = false
            @Suppress("DEPRECATION")
            manager.stopBluetoothSco()
        }
    }

    @Suppress("DEPRECATION")
    private fun activateLegacySco(manager: AudioManager) {
        val ready = CountDownLatch(1)
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                if (intent?.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1) == AudioManager.SCO_AUDIO_STATE_CONNECTED) {
                    ready.countDown()
                }
            }
        }
        // 先注册再启动；粘性广播也会报告已经连接的 SCO。
        context.registerReceiver(receiver, IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED))
        try {
            manager.startBluetoothSco()
            if (!ready.await(10, TimeUnit.SECONDS)) {
                throw RouteException("route_not_active", "蓝牙 SCO 音频未能在限定时间内连接")
            }
            manager.isBluetoothScoOn = true
        } finally {
            context.unregisterReceiver(receiver)
        }
    }

    private fun activateCommunicationDevice(manager: AudioManager, input: Device) {
        val candidates = manager.availableCommunicationDevices.filter(::isBluetoothCommunicationDevice)
        val selected = candidates.firstOrNull {
            !input.address.isNullOrBlank() && it.address.equals(input.address, ignoreCase = true)
        } ?: candidates.firstOrNull {
            it.productName?.toString().equals(input.name, ignoreCase = true)
        } ?: candidates.singleOrNull()
            ?: throw RouteException(
                "communication_device_not_available",
                "No Bluetooth communication device is available: ${candidates.joinToString { it.productName?.toString() ?: "Unknown" }}",
            )

        val ready = CountDownLatch(1)
        val executor = Executor { command -> Handler(Looper.getMainLooper()).post(command) }
        val listener = AudioManager.OnCommunicationDeviceChangedListener { current ->
            if (current?.id == selected.id) ready.countDown()
        }
        manager.addOnCommunicationDeviceChangedListener(executor, listener)
        try {
            if (!manager.setCommunicationDevice(selected)) {
                throw RouteException("route_rejected", "Android rejected the Bluetooth communication device")
            }
            if (manager.communicationDevice?.id != selected.id &&
                !ready.await(10, TimeUnit.SECONDS)
            ) {
                throw RouteException("route_not_active", "Bluetooth communication device did not become active")
            }
        } finally {
            manager.removeOnCommunicationDeviceChangedListener(listener)
        }
    }

    private fun isBluetoothInput(device: Device): Boolean = isBluetoothType(device.type)

    private fun isBluetoothCommunicationDevice(device: AudioDeviceInfo): Boolean = isBluetoothType(device.type)

    private fun isBluetoothType(type: Int): Boolean {
        return type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && type == AudioDeviceInfo.TYPE_BLE_HEADSET)
    }


    fun clearCommunicationDevice() {
        val manager = context.getSystemService(AudioManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) manager.clearCommunicationDevice()
        @Suppress("DEPRECATION")
        manager.isBluetoothScoOn = false
        @Suppress("DEPRECATION")
        manager.stopBluetoothSco()
        manager.mode = AudioManager.MODE_NORMAL
    }

    class RouteException(val code: String, message: String) : Exception(message)
}
