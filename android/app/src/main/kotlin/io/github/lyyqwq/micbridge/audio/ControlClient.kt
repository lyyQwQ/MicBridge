package io.github.lyyqwq.micbridge.audio

import android.os.SystemClock
import android.util.Log
import io.github.lyyqwq.micbridge.protocol.Protocol
import io.github.lyyqwq.micbridge.trust.DeviceTrustStore
import io.github.lyyqwq.micbridge.trust.TrustHandshake
import io.github.lyyqwq.micbridge.trust.CandidateFailurePolicy
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/** Both TCP directions use the same authentication and active-session lifetime. */
class ControlClient(
    private val capture: AudioCaptureController,
    private val trust: DeviceTrustStore,
    private val onConnection: (connected: Boolean, retryAllowed: Boolean) -> Unit,
) {
    private class Session(
        val socket: Socket,
        val writer: DataOutputStream,
        val generation: Long,
        val key: String,
    ) {
        val writeLock = Any()
        val sequence = AtomicLong(1)
        @Volatile var transport = "tcp"
        @Volatile var audioPort = 0
        @Volatile var udp: DatagramSocket? = null
        @Volatile var lastReceived = SystemClock.elapsedRealtime()
        val pingPending = java.util.concurrent.atomic.AtomicBoolean(false)
        var captureAttempt = 0L
        var heartbeat: ScheduledFuture<*>? = null
    }

    private val lifecycle = Any()
    private val timers = Executors.newSingleThreadScheduledExecutor()
    // Capture callbacks may run while AudioCaptureController holds its lock.
    private val failures = Executors.newSingleThreadExecutor()
    private val candidates = java.util.concurrent.ConcurrentHashMap.newKeySet<Socket>()
    @Volatile private var generation = 0L
    @Volatile private var active: Session? = null
    @Volatile private var closed = false
    private var admissionPaused = false

    fun isBusy(): Boolean = synchronized(lifecycle) { active != null || candidates.isNotEmpty() }
    fun isConnected(): Boolean = active?.let(::isCurrent) == true

    fun connect(host: String, port: Int = 17_891, pairingRequested: Boolean = false, expectedDeviceId: String? = null) {
        synchronized(lifecycle) {
            if (closed) return
            // Cancel before replacing generation: a blocked connect/read must actually be released.
            invalidateLocked()
            try {
                require(host.isNotBlank() && host.length <= 255 && port in 1..65535) { "电脑地址或端口无效" }
                val saved = trust.current()
                val pair = pairingRequested && saved == null
                if (pair) trust.pairing()
                AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.DISCONNECTED,
                    detail = "正在连接电脑…"))
                startCandidateLocked(Socket(), host, port, pair, expectedDeviceId, outgoing = true)
            } catch (error: Exception) {
                publishFailureLocked(error, retryAllowed = false, phase = "连接电脑")
            }
        }
    }

    fun accept(socket: Socket) {
        synchronized(lifecycle) {
            if (closed || admissionPaused || candidates.size >= 4) { runCatching { socket.close() }; return }
            startCandidateLocked(socket, socket.inetAddress.hostAddress ?: "", 17_891,
                pairingRequested = false, expectedDeviceId = null, outgoing = false)
        }
    }

    fun forgetDevice() {
        synchronized(lifecycle) {
            if (closed) return
            admissionPaused = true
            try {
                // accept(), candidate persistence and adoption use this same lock. A waiting
                // accept can only register after the old generation and durable trust are gone.
                invalidateLocked()
                trust.forget()
                AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.DISCONNECTED,
                    detail = "已忘记电脑；请在电脑也忘记手机，然后在手机前台重新连接"))
                onConnection(false, false)
            } catch (error: Exception) {
                publishFailureLocked(error, retryAllowed = false)
            } finally {
                admissionPaused = false
            }
        }
    }

    fun disconnect() {
        synchronized(lifecycle) {
            val detail = AudioStateBus.current().detail
            invalidateLocked()
            trust.pairingEnded()
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.DISCONNECTED, detail = detail))
            onConnection(false, false)
        }
    }

    fun close() {
        closed = true
        // 不等生命周期锁；先释放可能堵住控制线程和音频写入的 socket。
        candidates.forEach { runCatching { it.close() } }
        active?.let { session ->
            runCatching { session.socket.close() }
            runCatching { session.udp?.close() }
        }
        timers.shutdownNow()
        failures.shutdownNow()
        Thread({ disconnect() }, "micbridge-control-close").start()
    }

    fun sendStart(): Boolean = sendCommand(Protocol.Type.START)
    fun sendStop(): Boolean = sendCommand(Protocol.Type.STOP)

    private fun startCandidateLocked(socket: Socket, host: String, port: Int, pairingRequested: Boolean,
        expectedDeviceId: String?, outgoing: Boolean) {
        val owner = generation
        candidates += socket
        if (closed) { candidates.remove(socket); runCatching { socket.close() }; return }
        Thread({ runCandidate(socket, owner, host, port, pairingRequested, expectedDeviceId, outgoing) },
            "micbridge-control-${if (outgoing) "client" else "accepted"}").start()
    }

    private fun runCandidate(socket: Socket, owner: Long, host: String, port: Int,
        pairingRequested: Boolean, expectedDeviceId: String?, outgoing: Boolean) {
        var session: Session? = null
        var failure: Exception? = null
        var output: DataOutputStream? = null
        var deadline: ScheduledFuture<*>? = null
        val approvalExpired = java.util.concurrent.atomic.AtomicBoolean(false)
        var awaitingApproval = false
        var phase = "连接电脑"
        val deadlineExpired = java.util.concurrent.atomic.AtomicBoolean(false)
        try {
            Log.i(TAG, "phase=$phase direction=${if (outgoing) "outgoing" else "incoming"}")
            deadline = timers.schedule({ deadlineExpired.set(true); runCatching { socket.close() } }, 10, TimeUnit.SECONDS)
            if (outgoing) socket.connect(InetSocketAddress(host, port), 5_000)
            ensureCandidate(socket, owner)
            phase = "验证电脑身份"
            socket.tcpNoDelay = true
            val writer = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))
            output = writer
            val input = DataInputStream(BufferedInputStream(socket.getInputStream()))
            val sequence = AtomicLong(1)
            val result = TrustHandshake(trust).authenticate(input, pairingRequested, expectedDeviceId,
                send = { type, body ->
                    ensureCandidate(socket, owner)
                    Protocol.write(writer, Protocol.json(type, sequence.getAndIncrement(), body.toString()))
                },
                persist = { record -> synchronized(lifecycle) {
                    ensureCandidate(socket, owner)
                    if (active != null || trust.current() != null) throw TrustHandshake.Rejected("trust_slot_occupied", "已有设备信任，请重新连接")
                    trust.save(record)
                } },
                setReadDeadline = { timeout ->
                    ensureCandidate(socket, owner)
                    deadline?.cancel(false)
                    socket.soTimeout = timeout
                    awaitingApproval = timeout == TrustHandshake.PAIRING_TIMEOUT_MS
                    deadlineExpired.set(false)
                    phase = if (awaitingApproval) "等待电脑批准" else "验证电脑身份"
                    synchronized(lifecycle) {
                        ensureCandidate(socket, owner)
                        if (outgoing && active == null) AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.DISCONNECTED,
                            detail = if (awaitingApproval) "等待电脑批准连接（60 秒）" else "正在验证电脑身份…"))
                    }
                    Log.i(TAG, "phase=$phase")
                    deadline = timers.schedule({
                        deadlineExpired.set(true)
                        if (timeout == TrustHandshake.PAIRING_TIMEOUT_MS) approvalExpired.set(true)
                        runCatching { socket.close() }
                    }, timeout.toLong(), TimeUnit.MILLISECONDS)
                },
                checkCurrent = { ensureCandidate(socket, owner) },
            )
            synchronized(lifecycle) {
                ensureCandidate(socket, owner)
                val previous = active
                if (previous != null && previous.key <= result.sessionKey) return
                // Authentication, including Windows proof, is complete before touching the active session.
                // An accepted socket does not advertise the computer's listening port.
                // Keep the last authenticated port instead of replacing it with the default.
                trust.rememberAddress(host, if (outgoing) port else trust.current()?.port ?: port)
                trust.refreshAuthenticatedName(result.advertisedName)
                if (previous != null) cleanupLocked(previous)
                val next = Session(socket, writer, owner, result.sessionKey)
                next.sequence.set(sequence.get())
                updateTransport(next, result.status)
                active = next
                session = next
                candidates.remove(socket)
                trust.authenticated()
                deadline?.cancel(false)
                socket.soTimeout = SESSION_TIMEOUT_MS
                phase = "控制连接"
                Log.i(TAG, "phase=authenticated")
                AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.IDLE, connected = true))
                onConnection(true, false)
                next.heartbeat = timers.scheduleAtFixedRate({ heartbeat(next) }, 5, 5, TimeUnit.SECONDS)
                sendStatus(next)
            }
            val accepted = session ?: return
            while (isCurrent(accepted)) {
                val frame = Protocol.read(input)
                synchronized(lifecycle) {
                    if (!isCurrent(accepted)) return
                    accepted.lastReceived = SystemClock.elapsedRealtime()
                    dispatch(accepted, frame)
                }
            }
        } catch (error: Exception) {
            failure = if (session == null && (approvalExpired.get() || awaitingApproval && error is java.net.SocketTimeoutException)) {
                TrustHandshake.Rejected("pairing_timeout", TrustHandshake.errorDetail("pairing_timeout"))
            } else if (session == null && deadlineExpired.get()) java.net.SocketTimeoutException("超时") else error
            // A rejected candidate gets its own error; it cannot write to the authenticated socket.
            if (session == null && error is TrustHandshake.Rejected) {
                runCatching { output?.let { Protocol.write(it, Protocol.json(Protocol.Type.ERROR, 0,
                    JSONObject().put("code", error.code).put("message", error.message).toString())) } }
            } else if (session == null && error is DeviceTrustStore.StorageException) {
                runCatching { output?.let { Protocol.write(it, Protocol.json(Protocol.Type.ERROR, 0,
                    JSONObject().put("code", "trust_store_failed").put("message", "设备信任保存失败").toString())) } }
            }
        } finally {
            deadline?.cancel(false)
            // Close resources before publishing a failure or permitting the next attempt.
            runCatching { socket.close() }
            synchronized(lifecycle) {
                candidates.remove(socket)
                val accepted = session
                if (accepted != null && isCurrent(accepted)) {
                    val firstDetail = AudioStateBus.current().takeIf { it.state == AudioStateBus.State.ERROR }?.detail
                    cleanupLocked(accepted)
                    val reason = failure ?: IllegalStateException(firstDetail ?: "电脑连接已断开")
                    publishFailureLocked(reason, retryAllowed = true, phase = phase)
                } else if (accepted == null && owner == generation && active == null && !closed &&
                    (outgoing || candidates.isEmpty())) {
                    if (!CandidateFailurePolicy.reportToUser(outgoing, authenticated = false,
                            storageFailure = failure is DeviceTrustStore.StorageException)) {
                        // 陌生入站认证失败只关闭该候选，不改信任或覆盖用户状态。
                        Log.w(TAG, "phase=$phase incoming_candidate_rejected")
                        onConnection(false, true)
                        return
                    }
                    val wasPairing = trust.trustState() == "pairing"
                    trust.pairingEnded()
                    val retry = !wasPairing && failure !is TrustHandshake.Rejected && failure !is DeviceTrustStore.StorageException
                    publishFailureLocked(failure ?: IllegalStateException("电脑连接已断开"), retry, phase)
                }
            }
        }
    }

    private fun ensureCandidate(socket: Socket, owner: Long) {
        synchronized(lifecycle) {
            check(!closed && owner == generation && socket in candidates && !socket.isClosed) { "stale control connection" }
        }
    }
    private fun isCurrent(session: Session): Boolean = !closed && active === session && generation == session.generation

    private fun invalidateLocked() {
        generation++
        candidates.forEach { runCatching { it.close() } }
        candidates.clear()
        active?.let(::cleanupLocked)
    }

    private fun cleanupLocked(session: Session) {
        if (active !== session) return
        active = null
        session.heartbeat?.cancel(false)
        runCatching { session.socket.close() }
        // Closing the socket releases audio writes before waiting on the capture lock.
        synchronized(session.writeLock) {
            runCatching { session.udp?.close() }
            session.udp = null
        }
        capture.stop()
    }

    private fun publishFailureLocked(error: Exception, retryAllowed: Boolean, phase: String = "控制连接") {
        val rejected = error as? TrustHandshake.Rejected
        if (CandidateFailurePolicy.requiresRepair(rejected?.code, error is DeviceTrustStore.StorageException)) {
            trust.repairRequired()
        }
        val retry = retryAllowed && rejected == null && error !is DeviceTrustStore.StorageException && trust.trustState() != "repair_required"
        val reason = when (error) {
            is java.net.SocketTimeoutException -> "超时"
            is java.net.ConnectException -> "无法连接，请检查电脑地址、服务和防火墙"
            is java.net.UnknownHostException -> "无法解析电脑地址"
            is java.io.EOFException -> "电脑已断开连接"
            is TrustHandshake.Rejected -> TrustHandshake.errorDetail(error.code)
            is DeviceTrustStore.StorageException -> "设备信任存储不可用"
            is IllegalArgumentException -> "电脑地址或端口无效"
            else -> "连接不可用，请检查网络和电脑服务"
        }
        // Log only local phase/type and protocol error code, never handshake bodies or secrets.
        Log.w(TAG, "phase=$phase exception=${error.javaClass.simpleName} code=${rejected?.code ?: "connection_failed"}")
        val detail = "${phase}失败：$reason (${error.javaClass.simpleName})" +
            if (trust.trustState() == "repair_required") "；自动重连已暂停，请在双方忘记设备后重新配对" else ""
        AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR, detail = detail))
        onConnection(false, retry)
    }

    private fun dispatch(session: Session, frame: Protocol.Frame) {
        when (frame.type) {
            Protocol.Type.START -> {
                if (AudioStateBus.current().state == AudioStateBus.State.STREAMING) {
                    sendStatus(session)
                    return
                }
                val attempt = ++session.captureAttempt
                val result = capture.start(
                    send = { bytes -> if (isCurrent(session)) sendAudio(session, bytes) },
                    onFailure = { code, message ->
                        runCatching { failures.execute { failCapture(session, attempt, code, message) } }
                    },
                )
                if (result.isSuccess && isCurrent(session)) sendStatus(session)
            }
            Protocol.Type.STOP -> { session.captureAttempt++; capture.stop(); sendStatus(session) }
            Protocol.Type.PING -> sendStatus(session)
            Protocol.Type.STATUS -> {
                updateTransport(session, JSONObject(Protocol.json(frame)))
                // STATUS is a heartbeat response, not a request (avoid STATUS echo loops).
            }
            Protocol.Type.ERROR -> {
                val body = JSONObject(Protocol.json(frame))
                val code = body.optString("code", "peer_error")
                if (code.startsWith("auth_") || code.startsWith("pairing_") || code in setOf("unknown_device", "trust_slot_occupied", "trust_store_failed")) {
                    throw TrustHandshake.Rejected(code, TrustHandshake.errorDetail(code))
                }
                throw Protocol.ProtocolException("$code: ${body.optString("message", "电脑结束了连接")}")
            }
            Protocol.Type.RESPONSE -> Unit
            else -> throw Protocol.ProtocolException("认证后收到无效控制消息")
        }
    }

    private fun failCapture(session: Session, attempt: Long, code: String, message: String) {
        synchronized(lifecycle) {
            if (!isCurrent(session) || session.captureAttempt != attempt) return
            Log.w(TAG, "phase=capture code=$code")
            capture.stop()
            val detail = "音频采集失败：$message"
            AudioStateBus.publish(AudioStateBus.Snapshot(AudioStateBus.State.ERROR,
                detail = detail, connected = true))
            try {
                // A missing/failed microphone is not a broken control connection. Keep the
                // authenticated session so a manual retry does not retrigger automatic START.
                sendJson(session, Protocol.Type.ERROR, JSONObject().put("code", code).put("message", detail))
            } catch (error: Exception) {
                cleanupLocked(session)
                publishFailureLocked(IllegalStateException("发送采集错误失败：${error.message ?: detail}", error), retryAllowed = true)
            }
        }
    }

    private fun sendCommand(type: Protocol.Type): Boolean = synchronized(lifecycle) {
        val session = active
        if (session == null || !isCurrent(session)) {
            AudioStateBus.publish(AudioStateBus.current().copy(state = AudioStateBus.State.ERROR,
                detail = "无法${if (type == Protocol.Type.START) "开始" else "结束"}通话：电脑尚未完成认证", connected = false))
            return false
        }
        try {
            sendJson(session, type, JSONObject().put("command", type.name))
            true
        } catch (error: Exception) {
            cleanupLocked(session)
            publishFailureLocked(error, retryAllowed = true)
            false
        }
    }

    private fun heartbeat(session: Session) {
        if (!isCurrent(session)) return
        if (SystemClock.elapsedRealtime() - session.lastReceived >= SESSION_TIMEOUT_MS) {
            runCatching { session.socket.close() }
            return
        }
        if (session.pingPending.compareAndSet(false, true)) {
            runCatching { failures.execute {
                try {
                    if (isCurrent(session)) sendJson(session, Protocol.Type.PING, JSONObject())
                } catch (_: Exception) {
                    runCatching { session.socket.close() }
                } finally {
                    session.pingPending.set(false)
                }
            } }.onFailure { session.pingPending.set(false) }
        }
    }

    private fun updateTransport(session: Session, body: JSONObject) {
        val port = body.optInt("audio_port", 0)
        session.transport = if (body.optString("audio_transport") == "udp" && port in 1..65535) "udp" else "tcp"
        session.audioPort = if (session.transport == "udp") port else 0
    }
    private fun sendStatus(session: Session) {
        val snapshot = AudioStateBus.current()
        sendJson(session, Protocol.Type.STATUS, JSONObject().put("state", snapshot.state.wire)
            .put("device", snapshot.device ?: "").put("detail", snapshot.detail ?: ""))
    }
    private fun sendJson(session: Session, type: Protocol.Type, body: JSONObject) {
        sendFrame(session, Protocol.json(type, session.sequence.getAndIncrement(), body.toString()))
    }
    private fun sendAudio(session: Session, bytes: ByteArray) {
        if (session.transport != "udp") {
            sendFrame(session, Protocol.Frame(Protocol.Type.AUDIO, session.sequence.getAndIncrement(), bytes))
        } else {
            var offset = 0
            while (offset < bytes.size && isCurrent(session)) {
                val end = minOf(offset + Protocol.MAX_UDP_PAYLOAD, bytes.size)
                sendFrame(session, Protocol.Frame(Protocol.Type.AUDIO, session.sequence.getAndIncrement(), bytes.copyOfRange(offset, end)))
                offset = end
            }
        }
    }
    private fun sendFrame(session: Session, frame: Protocol.Frame) {
        synchronized(session.writeLock) {
            check(isCurrent(session)) { "stale control connection" }
            if (frame.type == Protocol.Type.AUDIO && session.transport == "udp") {
                val udp = session.udp ?: DatagramSocket().also { session.udp = it }
                val encoded = Protocol.encode(frame)
                udp.send(DatagramPacket(encoded, encoded.size, session.socket.inetAddress, session.audioPort))
            } else {
                Protocol.write(session.writer, frame)
            }
        }
    }

    companion object {
        private const val SESSION_TIMEOUT_MS = 15_000
        private const val TAG = "MicBridgeControl"
    }
}
