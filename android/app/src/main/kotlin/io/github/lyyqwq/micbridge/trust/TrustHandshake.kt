package io.github.lyyqwq.micbridge.trust

import io.github.lyyqwq.micbridge.discovery.DeviceNames
import io.github.lyyqwq.micbridge.protocol.Protocol
import org.json.JSONArray
import org.json.JSONObject
import java.io.DataInputStream
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID

class TrustHandshake(private val store: DeviceTrustStore) {
    class Rejected(val code: String, detail: String) : Exception("$code: $detail")
    data class Result(val peer: DeviceTrustStore.Record, val sessionKey: String, val status: JSONObject,
        val advertisedName: String?)

    /** Callbacks are generation-checked by the owner, including the durable write before ACK. */
    fun authenticate(
        input: DataInputStream,
        pairingRequested: Boolean,
        expectedDeviceId: String?,
        send: (Protocol.Type, JSONObject) -> Unit,
        persist: (DeviceTrustStore.Record) -> Unit,
        setReadDeadline: (Int) -> Unit,
        checkCurrent: () -> Unit,
    ): Result {
        val localId = store.installationId()
        val connectionId = UUID.randomUUID().toString().replace("-", "")
        var saved = store.current()
        val request = pairingRequested && saved == null
        setReadDeadline(AUTH_TIMEOUT_MS)
        checkCurrent()
        send(Protocol.Type.HELLO, JSONObject().apply {
            put("peer", "android")
            put("device_name", store.localDeviceName())
            put("device_id", localId)
            put("connection_id", connectionId)
            put("auth_mode", AUTH_MODE)
            put("pairing_requested", request)
            put("pairing_id", saved?.pairingId ?: "")
            put("sources", JSONArray().put("micbridge_mic"))
            put("sinks", JSONArray())
            put("audio_rate", Protocol.AUDIO_RATE)
            put("audio_bits", Protocol.AUDIO_BITS)
            put("audio_channels", Protocol.AUDIO_CHANNELS)
            put("audio_transports", JSONArray().put("tcp").put("udp"))
        })
        val hello = read(input, Protocol.Type.HELLO, checkCurrent)
        if (hello.optString("peer") != "windows" || hello.optString("auth_mode") != AUTH_MODE) {
            reject("auth_mode_unsupported", "电脑不支持设备信任认证，请更新两端应用")
        }
        if (hello.optInt("audio_rate", Protocol.AUDIO_RATE) != Protocol.AUDIO_RATE ||
            hello.optInt("audio_bits", Protocol.AUDIO_BITS) != Protocol.AUDIO_BITS ||
            hello.optInt("audio_channels", Protocol.AUDIO_CHANNELS) != Protocol.AUDIO_CHANNELS) {
            reject("auth_malformed", "电脑音频参数不兼容")
        }
        val remoteId = hello.optString("device_id")
        val remoteConnection = hello.optString("connection_id")
        val remoteName = hello.opt("device_name") as? String
        if ((!hello.isNull("device_name") && remoteName == null) ||
            (remoteName != null && !DeviceNames.validPayload(remoteName))) {
            reject("auth_malformed", "电脑名称无效")
        }
        val remotePairing = if (hello.isNull("pairing_id")) "" else hello.optString("pairing_id")
        if (hello.opt("device_id") !is String || hello.opt("connection_id") !is String ||
            !Protocol.validIdentity(remoteId) || !Protocol.validIdentity(remoteConnection) ||
            (!hello.isNull("pairing_id") && hello.opt("pairing_id") !is String) || hello.opt("pairing_requested") != false ||
            remotePairing.isNotEmpty() && !Protocol.validIdentity(remotePairing)) {
            reject("auth_malformed", "电脑身份信息无效")
        }
        if (!expectedDeviceId.isNullOrBlank() && expectedDeviceId != remoteId) {
            reject("unknown_device", "发现的电脑身份已改变，请重新选择电脑")
        }
        if (saved != null) {
            if (saved.deviceId != remoteId) reject("trust_slot_occupied", "已记住另一台电脑，请先在手机上忘记电脑")
            if (saved.pairingId != remotePairing) reject("unknown_device", "电脑已忘记此手机，请在双方忘记设备后重新配对")
        } else {
            if (remotePairing.isNotEmpty()) reject("unknown_device", "手机未保存此配对，请在双方忘记设备后重新配对")
            if (!request) reject("unknown_device", "未配对，请在手机前台点击连接并在电脑批准")
            setReadDeadline(PAIRING_TIMEOUT_MS)
            val grant = read(input, Protocol.Type.PAIR_GRANT, checkCurrent)
            val pairingId = grant.optString("pairing_id")
            val secret = grant.optString("secret")
            if (!Protocol.validIdentity(pairingId) || !validSecret(secret)) reject("auth_malformed", "电脑配对授权无效")
            saved = DeviceTrustStore.Record(remoteId, remoteName?.takeIf(DeviceNames::valid) ?: "Windows", pairingId, secret)
            persist(saved)
            send(Protocol.Type.PAIR_ACK, JSONObject().put("pairing_id", pairingId))
        }
        // One local monotonic/socket deadline for the entire auth exchange; clocks need not agree.
        setReadDeadline(AUTH_TIMEOUT_MS)
        val trusted = saved ?: reject("unknown_device", "没有已保存的电脑信任")
        val challenge = read(input, Protocol.Type.AUTH_CHALLENGE, checkCurrent)
        val value = challenge.optString("challenge")
        if (!value.matches(Regex("[A-Za-z0-9_-]{32}")) || challenge.optString("device_id") != remoteId ||
            challenge.optString("connection_id") != remoteConnection) reject("auth_malformed", "电脑认证挑战无效")
        val nonce = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(24).also { SecureRandom().nextBytes(it) })
        val proof = Protocol.authProof(trusted.secret, value, localId, remoteId, connectionId, remoteConnection)
        send(Protocol.Type.AUTH_PROOF, JSONObject().put("proof", proof).put("nonce", nonce))
        val confirmation = readFrame(input, checkCurrent)
        val expected = Protocol.serverAuthProof(trusted.secret, remoteId, localId, remoteConnection, connectionId, trusted.pairingId, value, nonce, proof)
        val actual = JSONObject(Protocol.json(confirmation)).optString("proof")
        if (!Protocol.verifyServerProof(confirmation.type, expected, actual)) {
            reject("auth_proof_invalid", "电脑身份验证失败，请在双方忘记设备后重新配对")
        }
        // STATUS alone is never authentication, even when it says connected/idle.
        val status = read(input, Protocol.Type.STATUS, checkCurrent)
        val devices = listOf(localId, remoteId).sorted()
        val connections = listOf(connectionId, remoteConnection).sorted()
        return Result(trusted, "${devices[0]}:${devices[1]}:${connections[0]}:${connections[1]}", status, remoteName)
    }

    private fun read(input: DataInputStream, expected: Protocol.Type, checkCurrent: () -> Unit): JSONObject {
        val frame = readFrame(input, checkCurrent)
        if (frame.type != expected) reject("auth_malformed", "认证消息顺序无效：需要 ${expected.name}")
        return JSONObject(Protocol.json(frame))
    }
    private fun readFrame(input: DataInputStream, checkCurrent: () -> Unit): Protocol.Frame {
        checkCurrent()
        val frame = Protocol.read(input)
        checkCurrent()
        if (frame.type == Protocol.Type.ERROR) {
            val body = JSONObject(Protocol.json(frame))
            val code = body.optString("code", "auth_rejected")
            reject(code, errorDetail(code))
        }
        return frame
    }
    private fun validSecret(value: String): Boolean {
        if (!value.matches(Regex("[A-Za-z0-9_-]{43}"))) return false
        val bytes = Base64.getUrlDecoder().decode(value)
        return bytes.size == 32 && Base64.getUrlEncoder().withoutPadding().encodeToString(bytes) == value
    }
    private fun reject(code: String, message: String): Nothing = throw Rejected(code, message)

    companion object {
        const val AUTH_MODE = "device_trust_v1"
        const val AUTH_TIMEOUT_MS = 10_000
        const val PAIRING_TIMEOUT_MS = 65_000
        fun errorDetail(code: String): String = when (code) {
            "pairing_denied" -> "电脑拒绝了配对，请在手机前台重新连接"
            "pairing_expired", "pairing_timeout" -> "等待电脑批准超时，请在手机前台重新连接"
            "pairing_busy" -> "电脑正在处理另一项配对，请稍后重新连接"
            "trust_slot_occupied" -> "电脑已有其他设备配对，请先在电脑上忘记设备，再在手机重新连接"
            "unknown_device", "auth_proof_invalid", "pairing_ack_invalid" -> "两端设备信任不一致，请在双方忘记设备后重新配对"
            "trust_store_failed" -> "设备信任保存失败，请在双方忘记设备后重新配对"
            "auth_mode_unsupported" -> "电脑不支持设备信任认证，请更新两端应用"
            else -> "设备认证失败，请在双方检查配对并重新连接"
        }
    }
}
