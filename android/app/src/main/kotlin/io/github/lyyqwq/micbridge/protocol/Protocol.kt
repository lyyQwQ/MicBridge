package io.github.lyyqwq.micbridge.protocol

import java.util.Base64
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.zip.CRC32

object Protocol {
    const val VERSION: Int = 1
    const val MAX_PAYLOAD: Int = 1 shl 20
    const val MAX_UDP_PAYLOAD: Int = 1_180
    const val AUDIO_RATE = 48_000
    const val AUDIO_BITS = 16
    const val AUDIO_CHANNELS = 1
    private val magic = byteArrayOf('M'.code.toByte(), 'C'.code.toByte(), 'B'.code.toByte(), 'R'.code.toByte())

    enum class Type(val value: Int) { HELLO(1), START(2), STOP(3), STATUS(4), PING(5), RESPONSE(6), AUDIO(7), ERROR(8), AUTH_CHALLENGE(9), AUTH_PROOF(10), AUTH_OK(11), PAIR_GRANT(12), PAIR_ACK(13) }
    data class Frame(val type: Type, val sequence: Long, val payload: ByteArray)

    fun read(input: DataInputStream): Frame {
        val header = ByteArray(20)
        input.readFully(header)
        if (!header.copyOfRange(0, 4).contentEquals(magic)) throw ProtocolException("invalid protocol magic")
        if (header[4].toInt() != VERSION) throw ProtocolException("unsupported protocol version ${header[4]}")
        val type = Type.entries.firstOrNull { it.value == header[5].toInt() } ?: throw ProtocolException("unknown message type ${header[5]}")
        val sequence = u32(header, 8)
        val size = u32(header, 12).toInt()
        if (size < 0 || size > MAX_PAYLOAD) throw ProtocolException("payload exceeds 1 MiB limit")
        if (type != Type.AUDIO && size > 64 * 1024) throw ProtocolException("control payload exceeds 64 KiB limit")
        val payload = ByteArray(size)
        input.readFully(payload)
        val crc = CRC32().apply { update(payload) }.value
        if (crc != u32(header, 16)) throw ProtocolException("payload checksum mismatch")
        return Frame(type, sequence, payload)
    }

    fun encode(frame: Frame): ByteArray {
        require(frame.payload.size <= MAX_PAYLOAD) { "payload exceeds 1 MiB limit" }
        require(frame.type == Type.AUDIO || frame.payload.size <= 64 * 1024) { "control payload exceeds 64 KiB limit" }
        val output = ByteArray(20 + frame.payload.size)
        magic.copyInto(output, 0)
        output[4] = VERSION.toByte()
        output[5] = frame.type.value.toByte()
        putU32(output, 8, frame.sequence)
        putU32(output, 12, frame.payload.size.toLong())
        putU32(output, 16, crc32(frame.payload))
        frame.payload.copyInto(output, 20)
        return output
    }

    fun write(output: DataOutputStream, frame: Frame) {
        output.write(encode(frame))
        output.flush()
    }

    private fun crc32(payload: ByteArray): Long = CRC32().apply { update(payload) }.value

    fun json(type: Type, sequence: Long, body: String): Frame = Frame(type, sequence, body.toByteArray(Charsets.UTF_8))

    fun authProof(
        secret: String,
        challenge: String,
        localDeviceId: String,
        remoteDeviceId: String,
        localConnectionId: String,
        remoteConnectionId: String,
    ): String {
        val devices = listOf(localDeviceId, remoteDeviceId).sorted()
        val connections = listOf(localConnectionId, remoteConnectionId).sorted()
        val message = "MICBRIDGE-AUTH-V1|1|${devices[0]}|${devices[1]}|${connections[0]}|${connections[1]}|$challenge"
        return hmac(secret, message)
    }

    fun serverAuthProof(
        secret: String, windowsDeviceId: String, androidDeviceId: String,
        windowsConnectionId: String, androidConnectionId: String, pairingId: String,
        challenge: String, nonce: String, phoneProof: String,
    ): String = hmac(secret, "MICBRIDGE-SERVER-AUTH-V1|1|$windowsDeviceId|$androidDeviceId|$windowsConnectionId|$androidConnectionId|$pairingId|$challenge|$nonce|$phoneProof")

    fun verifyServerProof(type: Type, expected: String, actual: String): Boolean =
        type == Type.AUTH_OK && actual.matches(Regex("[A-Za-z0-9_-]{43}")) && fixedTimeEquals(expected, actual)

    private fun hmac(secret: String, message: String): String {
        val mac = javax.crypto.Mac.getInstance("HmacSHA256")
        mac.init(javax.crypto.spec.SecretKeySpec(secret.toByteArray(StandardCharsets.UTF_8), "HmacSHA256"))
        return Base64.getUrlEncoder().withoutPadding().encodeToString(mac.doFinal(message.toByteArray(StandardCharsets.UTF_8)))
    }

    fun validIdentity(value: String): Boolean = value.matches(Regex("[0-9a-f]{32}"))

    fun fixedTimeEquals(expected: String, actual: String): Boolean {
        val expectedBytes = expected.toByteArray(StandardCharsets.US_ASCII)
        val actualBytes = actual.toByteArray(StandardCharsets.US_ASCII)
        return expectedBytes.size == actualBytes.size && MessageDigest.isEqual(expectedBytes, actualBytes)
    }
    fun json(frame: Frame): String = frame.payload.toString(Charsets.UTF_8)

    private fun u32(bytes: ByteArray, offset: Int): Long {
        return ((bytes[offset].toLong() and 0xff) shl 24) or
            ((bytes[offset + 1].toLong() and 0xff) shl 16) or
            ((bytes[offset + 2].toLong() and 0xff) shl 8) or
            (bytes[offset + 3].toLong() and 0xff)
    }
    private fun putU32(bytes: ByteArray, offset: Int, value: Long) {
        bytes[offset] = (value ushr 24).toByte()
        bytes[offset + 1] = (value ushr 16).toByte()
        bytes[offset + 2] = (value ushr 8).toByte()
        bytes[offset + 3] = value.toByte()
    }

    class ProtocolException(message: String) : Exception(message)
}
