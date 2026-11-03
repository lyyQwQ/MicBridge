package io.github.lyyqwq.micbridge.trust

import android.content.Context
import android.os.Build
import android.provider.Settings
import io.github.lyyqwq.micbridge.discovery.DeviceNames
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.github.lyyqwq.micbridge.protocol.Protocol
import org.json.JSONObject
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** One installation and one computer. Secrets and installation identity are excluded from backup. */
class DeviceTrustStore private constructor(context: Context) {
    data class Record(
        val deviceId: String,
        val deviceName: String,
        val pairingId: String,
        val secret: String,
        val host: String = "",
        val port: Int = 17_891,
    ) {
        fun publicMap(): Map<String, String> = mapOf(
            "deviceId" to deviceId, "deviceName" to deviceName, "pairingId" to pairingId,
        )
    }

    class StorageException : Exception("trust_store_failed: 设备信任存储损坏或无法保存，请忘记电脑后重新配对")

    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
    private val resolver = context.applicationContext.contentResolver
    private var identity: String? = null
    private var record: Record? = null
    private var broken = false
    private var state = "unpaired"

    init {
        try {
            val storedId = preferences.getString("installation_id", null)
            if (storedId == null) {
                check(!preferences.contains("trust"))
                val fresh = UUID.randomUUID().toString().replace("-", "")
                check(preferences.edit().putString("installation_id", fresh).commit())
                identity = fresh
            } else {
                check(Protocol.validIdentity(storedId))
                identity = storedId
            }
            preferences.getString("trust", null)?.let { encoded ->
                val envelope = JSONObject(encoded)
                check(envelope.getInt("version") == 1)
                val iv = Base64.decode(envelope.getString("iv"), Base64.NO_WRAP)
                check(iv.size == 12)
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, key(create = false), GCMParameterSpec(128, iv))
                cipher.updateAAD(identity!!.toByteArray(Charsets.UTF_8))
                val body = JSONObject(String(cipher.doFinal(Base64.decode(envelope.getString("ciphertext"), Base64.NO_WRAP)), Charsets.UTF_8))
                record = Record(body.getString("device_id"), body.getString("device_name"), body.getString("pairing_id"),
                    body.getString("secret"), body.optString("host"), body.optInt("port", 17_891)).also(::validate)
                state = "trusted"
            }
        } catch (_: Exception) {
            // Never turn an unreadable saved record into an apparently unpaired installation.
            record = null
            broken = true
            state = "repair_required"
        }
    }

    @Synchronized fun installationId(): String {
        ensureHealthy()
        return identity ?: throw StorageException()
    }
    fun localDeviceName(): String = DeviceNames.local(
        runCatching { Settings.Global.getString(resolver, Settings.Global.DEVICE_NAME) }.getOrNull(), Build.MODEL)

    @Synchronized fun refreshAuthenticatedName(candidate: String?) {
        val saved = current() ?: throw StorageException()
        val name = DeviceNames.authenticatedName(saved.deviceName, candidate)
        if (name != saved.deviceName) save(saved.copy(deviceName = name))
    }
    @Synchronized fun current(): Record? { ensureHealthy(); return record }
    @Synchronized fun snapshot(): Map<String, Any?> = mapOf(
        "trustedDevice" to record?.publicMap(), "trustState" to state,
        "lastHost" to (record?.host ?: ""), "lastPort" to (record?.port ?: 17_891),
    )
    @Synchronized fun failure(): String? = if (broken) StorageException().message else null
    @Synchronized fun trustState(): String = state
    @Synchronized fun pairing() { ensureHealthy(); state = "pairing" }
    @Synchronized fun authenticated() { ensureHealthy(); check(record != null); state = "trusted" }
    @Synchronized fun repairRequired() { state = "repair_required" }
    @Synchronized fun pairingEnded() { if (state == "pairing") state = if (record == null) "unpaired" else "repair_required" }

    @Synchronized fun save(value: Record) {
        ensureHealthy()
        try {
            validate(value)
            val body = JSONObject().put("device_id", value.deviceId).put("device_name", value.deviceName)
                .put("pairing_id", value.pairingId).put("secret", value.secret).put("host", value.host).put("port", value.port)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key(create = true))
            cipher.updateAAD(identity!!.toByteArray(Charsets.UTF_8))
            val encrypted = cipher.doFinal(body.toString().toByteArray(Charsets.UTF_8))
            val envelope = JSONObject().put("version", 1)
                .put("iv", Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
                .put("ciphertext", Base64.encodeToString(encrypted, Base64.NO_WRAP))
            // commit(), not apply(): an ACK must never precede durable storage.
            check(preferences.edit().putString("trust", envelope.toString()).commit())
            record = value
        } catch (_: Exception) {
            broken = true
            state = "repair_required"
            throw StorageException()
        }
    }

    @Synchronized fun rememberAddress(host: String, port: Int) {
        val saved = current() ?: throw StorageException()
        if (saved.host != host || saved.port != port) save(saved.copy(host = host, port = port))
    }

    @Synchronized fun forget() {
        try {
            val id = identity ?: UUID.randomUUID().toString().replace("-", "")
            check(preferences.edit().clear().putString("installation_id", id).commit())
            // A missing/invalid Keystore key is recoverable only through explicit forgetting.
            val keys = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            if (keys.containsAlias(KEY_ALIAS)) keys.deleteEntry(KEY_ALIAS)
            identity = id
            record = null
            broken = false
            state = "unpaired"
        } catch (_: Exception) {
            broken = true
            state = "repair_required"
            throw StorageException()
        }
    }

    private fun ensureHealthy() { if (broken) throw StorageException() }
    private fun validate(value: Record) {
        require(Protocol.validIdentity(value.deviceId) && Protocol.validIdentity(value.pairingId))
        require(value.deviceName.isNotBlank() && value.deviceName.length <= 256)
        require(value.secret.matches(Regex("[A-Za-z0-9_-]{43}")))
        val decoded = Base64.decode(value.secret, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        require(decoded.size == 32 && Base64.encodeToString(decoded, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING) == value.secret)
        require(value.host.length <= 255 && value.port in 1..65535)
    }
    private fun key(create: Boolean): SecretKey {
        val keys = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keys.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        check(create && !preferences.contains("trust"))
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setKeySize(256).setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setRandomizedEncryptionRequired(true).build())
        }.generateKey()
    }

    companion object {
        const val PREFERENCES = "micbridge_device_trust"
        private const val KEY_ALIAS = "micbridge_device_trust_aes256_v1"
        @Volatile private var instance: DeviceTrustStore? = null
        fun get(context: Context): DeviceTrustStore = instance ?: synchronized(this) {
            instance ?: DeviceTrustStore(context).also { instance = it }
        }
    }
}
