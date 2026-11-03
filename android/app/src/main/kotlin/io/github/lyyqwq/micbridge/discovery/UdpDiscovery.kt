package io.github.lyyqwq.micbridge.discovery

import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface

internal class UdpDiscovery(
    private val localDeviceId: String?,
    private val onPeer: (LanDiscovery.ComputerEndpoint) -> Unit,
) {
    private val worker = DiscoveryWorker()

    fun start(
        tcpPort: Int,
        name: String,
        sources: List<String> = listOf("micbridge_mic"),
        sinks: List<String> = emptyList(),
        transports: List<String> = listOf("tcp", "udp"),
        receive: Boolean,
    ) {
        if (!DiscoveryLimits.valid(localDeviceId.orEmpty(), name, "android", "local", tcpPort,
                sources, sinks, transports)) return
        val announcement = payload(tcpPort, name, sources, sinks, transports, true)
        worker.start({ openSocket(receive) }) { socket, active ->
            runLoop(socket, announcement, receive, active)
        }
    }

    private fun openSocket(receive: Boolean): MulticastSocket? =
        try {
            MulticastSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(InetAddress.getByName("0.0.0.0"), if (receive) DISCOVERY_PORT else 0))
                if (receive) {
                    soTimeout = 500
                    val group = InetAddress.getByName(MULTICAST_ADDRESS)
                    usableInterfaces().forEach { networkInterface ->
                        runCatching {
                            setNetworkInterface(networkInterface)
                            joinGroup(group)
                        }.onFailure { Log.w(TAG, "UDP discovery multicast join on ${networkInterface.name} failed", it) }
                    }
                }
            }
        } catch (error: Exception) {
            Log.e(TAG, "UDP discovery socket unavailable", error)
            null
        }

    private fun runLoop(
        socket: DatagramSocket,
        announcement: JSONObject,
        receive: Boolean,
        active: () -> Boolean,
    ) {
        var nextAnnouncementAt = 0L
        while (active()) {
            val now = System.currentTimeMillis()
            if (now >= nextAnnouncementAt) {
                runCatching { send(socket, announcement) }
                    .onFailure { Log.w(TAG, "UDP discovery announcement failed", it) }
                nextAnnouncementAt = now + ANNOUNCEMENT_INTERVAL_MS
            }
            if (!receive) {
                Thread.sleep(500)
                continue
            }
            val buffer = ByteArray(MAX_PACKET_SIZE)
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                socket.receive(packet)
                if (!active()) break
                runCatching { handle(socket, announcement, packet) }
                    .onFailure { Log.w(TAG, "UDP discovery packet failed", it) }
            } catch (_: java.net.SocketTimeoutException) {
                // 定时轮询用于同时发送 announcement 和检查停止状态。
            }
        }
    }

    private fun handle(socket: DatagramSocket, announcement: JSONObject, packet: DatagramPacket) {
        if (packet.length !in 1 until MAX_PACKET_SIZE) return
        val body = runCatching { JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8)) }.getOrNull() ?: return
        if (body.optInt("protocol", 0) != 1 || body.optString("id").isBlank() || body.optString("id").equals(localDeviceId, ignoreCase = true)) return
        val role = body.optString("role")
        val port = body.optInt("tcp_port", 0)
        if (role !in setOf("windows", "android") || port !in 1..65535 || body.optString("name").isBlank()) return
        val sources = strings(body.optJSONArray("sources")) ?: return
        val sinks = strings(body.optJSONArray("sinks")) ?: return
        val transports = strings(body.optJSONArray("transports")) ?: return
        val host = packet.address.hostAddress ?: return
        if (!DiscoveryLimits.valid(body.optString("id"), body.optString("name"), role, host, port,
                sources, sinks, transports)) return
        onPeer(LanDiscovery.ComputerEndpoint(
            name = body.optString("name"),
            host = host,
            port = port,
            deviceId = body.optString("id"),
            role = role,
            sources = sources,
            sinks = sinks,
            transports = transports,
        ))
        if (body.optBoolean("announcement", false)) {
            val response = JSONObject(announcement.toString()).put("announcement", false)
            send(socket, response, packet.address, packet.port)
        }
    }

    private fun send(current: DatagramSocket, body: JSONObject, address: InetAddress? = null, port: Int = DISCOVERY_PORT) {
        val bytes = body.toString().toByteArray(Charsets.UTF_8)
        if (address != null) {
            current.send(DatagramPacket(bytes, bytes.size, address, port))
            return
        }
        val destinations = linkedSetOf<InetAddress>().apply {
            add(InetAddress.getByName(MULTICAST_ADDRESS))
            add(InetAddress.getByName("255.255.255.255"))
            usableInterfaces().flatMap { networkInterface ->
                networkInterface.interfaceAddresses.mapNotNull { it.broadcast }
            }.forEach(::add)
        }
        destinations.forEach { destination ->
            runCatching { current.send(DatagramPacket(bytes, bytes.size, destination, DISCOVERY_PORT)) }
                .onFailure { Log.w(TAG, "UDP discovery send to $destination failed", it) }
        }
    }

    private fun usableInterfaces(): List<NetworkInterface> = runCatching {
        NetworkInterface.getNetworkInterfaces()?.asSequence()
            ?.filter { it.isUp && !it.isLoopback && it.supportsMulticast() }
            ?.filter { networkInterface ->
                networkInterface.interfaceAddresses.any { address ->
                    address.address is java.net.Inet4Address
                }
            }
            ?.toList()
            ?: emptyList()
    }.getOrDefault(emptyList())

    private fun payload(port: Int, name: String, sources: List<String>, sinks: List<String>, transports: List<String>, isAnnouncement: Boolean) = JSONObject().apply {
        put("protocol", 1)
        put("id", localDeviceId)
        put("name", name.ifBlank { "MicBridge" })
        put("role", "android")
        put("tcp_port", port)
        put("sources", JSONArray(sources))
        put("sinks", JSONArray(sinks))
        put("transports", JSONArray(transports))
        put("announcement", isAnnouncement)
    }

    private fun strings(values: JSONArray?): List<String>? {
        if (values == null) return emptyList()
        if (values.length() > DiscoveryLimits.MAX_CAPABILITIES) return null
        val result = (0 until values.length()).map { values.opt(it) as? String ?: return null }
        return result.takeIf(DiscoveryLimits::validCapabilities)
    }

    fun stop() = worker.stop()

    companion object {
        private const val TAG = "MicBridgeUdpDiscovery"
        const val DISCOVERY_PORT = 17_893
        private const val ANNOUNCEMENT_INTERVAL_MS = 5_000L
        private const val MAX_PACKET_SIZE = 8 * 1024
        private const val MULTICAST_ADDRESS = "224.0.0.167"
    }
}
