package io.github.lyyqwq.micbridge.audio

import java.util.concurrent.CopyOnWriteArrayList

object AudioStateBus {
    enum class State(val wire: String) { DISCONNECTED("disconnected"), IDLE("idle"), STARTING("starting"), STREAMING("streaming"), STOPPING("stopping"), ERROR("error") }
    data class Snapshot(
        val state: State,
        val device: String? = null,
        val detail: String? = null,
        val connected: Boolean = false,
    )

    private val listeners = CopyOnWriteArrayList<(Snapshot) -> Unit>()
    @Volatile private var current = Snapshot(State.DISCONNECTED)

    fun current(): Snapshot = current
    fun subscribe(listener: (Snapshot) -> Unit): () -> Unit {
        listeners += listener
        listener(current)
        return { listeners -= listener }
    }
    fun publish(snapshot: Snapshot) {
        current = snapshot
        listeners.forEach { it(snapshot) }
    }
}
