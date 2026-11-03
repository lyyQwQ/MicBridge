package io.github.lyyqwq.micbridge.discovery

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager

class LanDiscovery(
    context: Context,
    private val onChanged: (List<ComputerEndpoint>) -> Unit,
    private val localDeviceId: String? = null,
    private val udpReceiver: Boolean = true,
    private val onObserved: (ComputerEndpoint) -> Unit = {},
) {
    private val appContext = context.applicationContext
    private val manager = appContext.getSystemService(NsdManager::class.java)
    private val multicastLock = appContext.getSystemService(WifiManager::class.java)
        ?.createMulticastLock("micbridge-discovery")
        ?.apply { setReferenceCounted(false) }
    private val cache = DiscoveryCache<ComputerEndpoint>(
        identity = { "${it.role}:${it.deviceId}" },
        host = { it.host },
        preferred = ::isOnWifiSubnet,
    )
    private var expiryThread: Thread? = null
    private var listener: NsdManager.DiscoveryListener? = null
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var udp: UdpDiscovery? = null
    fun start() {
        if (listener != null) return
        cache.clear()
        onChanged(emptyList())
        runCatching { multicastLock?.acquire() }
        udp?.stop()
        udp = UdpDiscovery(localDeviceId, ::onUdpPeer).also {
            it.start(17_891, "MicBridge", receive = udpReceiver)
        }
        startExpiryThread()
        val next = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (serviceInfo.serviceType.trimEnd('.') != SERVICE_TYPE) return
                if (serviceInfo.serviceName.length > DiscoveryLimits.MAX_NAME_LENGTH) return
                runCatching { manager.resolveService(serviceInfo, resolver) }
            }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                if (cache.removeSource("mdns:${serviceInfo.serviceName}")) publish()
            }
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                listener = null
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                listener = null
            }
        }
        listener = next
        runCatching { manager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, next) }
            .onFailure {
                listener = null
            }
    }

    fun stop() {
        listener?.let {
            listener = null
            runCatching { manager.stopServiceDiscovery(it) }
        }
        registrationListener?.let {
            registrationListener = null
            runCatching { manager.unregisterService(it) }
        }
        udp?.stop()
        udp = null
        expiryThread?.interrupt()
        expiryThread = null
        runCatching { if (multicastLock?.isHeld == true) multicastLock.release() }
        cache.clear()
        onChanged(emptyList())
    }

    fun register(
        port: Int,
        deviceId: String,
        name: String,
        sources: List<String> = listOf("micbridge_mic"),
        sinks: List<String> = emptyList(),
        transports: List<String> = listOf("tcp", "udp"),
    ) {
        if (!DiscoveryLimits.valid(deviceId, name, "android", "local", port, sources, sinks, transports)) return
        registrationListener?.let { runCatching { manager.unregisterService(it) } }
        val info = NsdServiceInfo().apply {
            serviceName = DeviceNames.utf8Prefix(name, 63)
            serviceType = SERVICE_TYPE
            this.port = port
            setAttribute("role", "android")
            setAttribute("protocol", "1")
            setAttribute("id", deviceId)
            setAttribute("name", DeviceNames.utf8Prefix(name, 250))
            setAttribute("sources", sources.joinToString(","))
            setAttribute("sinks", sinks.joinToString(","))
            setAttribute("transports", transports.joinToString(","))
        }
        val registration = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) = Unit
            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) { registrationListener = null }
            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit
        }
        registrationListener = registration
        runCatching { manager.registerService(info, NsdManager.PROTOCOL_DNS_SD, registration) }
            .onFailure { registrationListener = null }
        runCatching { multicastLock?.acquire() }
        udp?.stop()
        udp = UdpDiscovery(deviceId, ::onUdpPeer).also {
            it.start(port, name, sources, sinks, transports, receive = udpReceiver)
        }
        startExpiryThread()
    }

    fun accept(endpoint: ComputerEndpoint) {
        if (endpoint.deviceId.equals(localDeviceId, ignoreCase = true)) return
        merge(endpoint, "bus")
    }

    private val resolver = object : NsdManager.ResolveListener {
        override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
            if (serviceInfo.serviceName.length > DiscoveryLimits.MAX_NAME_LENGTH) return
            val role = serviceInfo.attributes["role"]?.toUtf8() ?: return
            val protocol = serviceInfo.attributes["protocol"]?.toUtf8()?.toIntOrNull()
            val id = serviceInfo.attributes["id"]?.toUtf8()?.trim().orEmpty()
            if (id.equals(localDeviceId, ignoreCase = true)) return
            val host = serviceInfo.host ?: return
            val hostAddress = host.hostAddress ?: return
            if (role !in setOf("windows", "android") || protocol != 1 || id.isBlank() || serviceInfo.port !in 1..65535) return
            merge(ComputerEndpoint(
                name = serviceInfo.attributes["name"]?.toUtf8()?.ifBlank { serviceInfo.serviceName } ?: serviceInfo.serviceName,
                host = hostAddress,
                port = serviceInfo.port,
                deviceId = id,
                role = role,
                sources = DiscoveryLimits.parseCapabilities(serviceInfo.attributes["sources"]?.toUtf8()) ?: return,
                sinks = DiscoveryLimits.parseCapabilities(serviceInfo.attributes["sinks"]?.toUtf8()) ?: return,
                transports = DiscoveryLimits.parseCapabilities(serviceInfo.attributes["transports"]?.toUtf8()) ?: return,
            ), "mdns:${serviceInfo.serviceName}", observed = true)
        }

        override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit
    }

    private fun onUdpPeer(endpoint: ComputerEndpoint) {
        if (endpoint.deviceId.equals(localDeviceId, ignoreCase = true)) return
        merge(endpoint, "udp", observed = true)
    }

    private fun merge(endpoint: ComputerEndpoint, source: String, observed: Boolean = false) {
        if (!DiscoveryLimits.valid(endpoint.deviceId, endpoint.name, endpoint.role, endpoint.host,
                endpoint.port, endpoint.sources, endpoint.sinks, endpoint.transports) ||
            source.length > DiscoveryLimits.MAX_NAME_LENGTH + 5) return
        val changed = cache.observe(endpoint, source)
        // 真实观测每次都转发；列表是否变化不能决定下游 TTL 续命。
        if (observed) onObserved(endpoint)
        if (changed) publish()
    }

    private fun startExpiryThread() {
        if (expiryThread != null) return
        expiryThread = Thread({
            try {
                while (!Thread.currentThread().isInterrupted) {
                    Thread.sleep(EXPIRY_INTERVAL_MS)
                    if (cache.refresh()) publish()
                }
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }, "micbridge-discovery-expiry").also { it.start() }
    }
    private fun publish() {
        val snapshot = cache.snapshot().sortedWith(compareBy({ it.name.lowercase() }, { it.deviceId }))
        onChanged(snapshot)
    }

    private fun isOnWifiSubnet(host: String): Boolean = runCatching {
        val address = java.net.InetAddress.getByName(host).address
        val connectivity = appContext.getSystemService(ConnectivityManager::class.java)
        connectivity.allNetworks.any { network ->
            val capabilities = connectivity.getNetworkCapabilities(network)
            capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true &&
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN).not() &&
                connectivity.getLinkProperties(network)?.linkAddresses.orEmpty().any { local ->
                    DiscoveryCache.sameSubnet(address, local.address.address, local.prefixLength)
                }
        }
    }.getOrDefault(false)

    private fun ByteArray.toUtf8(): String = toString(Charsets.UTF_8)

    data class ComputerEndpoint(
        val name: String,
        val host: String,
        val port: Int,
        val deviceId: String = "",
        val role: String = "windows",
        val sources: List<String> = emptyList(),
        val sinks: List<String> = emptyList(),
        val transports: List<String> = emptyList(),
    )

    companion object {
        const val SERVICE_TYPE = "_micbridge._tcp"
        private const val EXPIRY_INTERVAL_MS = 5_000L
    }
}
