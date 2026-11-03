package io.github.lyyqwq.micbridge.discovery

import java.util.concurrent.CopyOnWriteArraySet

internal object DiscoveryBus {
    private val listeners = CopyOnWriteArraySet<(LanDiscovery.ComputerEndpoint) -> Unit>()

    fun subscribe(listener: (LanDiscovery.ComputerEndpoint) -> Unit): () -> Unit {
        listeners += listener
        return { listeners -= listener }
    }

    fun publish(endpoint: LanDiscovery.ComputerEndpoint) {
        listeners.forEach { it(endpoint) }
    }
}
