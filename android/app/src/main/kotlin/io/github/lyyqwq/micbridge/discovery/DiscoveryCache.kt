package io.github.lyyqwq.micbridge.discovery

/** 仅保存发现候选；来源各自续命，不涉及设备信任。 */
internal class DiscoveryCache<T>(
    private val identity: (T) -> String,
    private val host: (T) -> String,
    private val preferred: (String) -> Boolean,
    private val now: () -> Long = { System.nanoTime() / 1_000_000 },
) {
    private data class Observation<T>(val value: T, val seen: Long)
    private val peers = linkedMapOf<String, MutableMap<Pair<String, String>, Observation<T>>>()
    private val selected = linkedMapOf<String, T>()

    @Synchronized
    fun observe(value: T, source: String): Boolean {
        val key = identity(value)
        val expired = refresh()
        val candidates = peers[key] ?: run {
            if (peers.size >= DiscoveryLimits.MAX_DEVICES) return expired
            linkedMapOf<Pair<String, String>, Observation<T>>().also { peers[key] = it }
        }
        val observation = source to host(value)
        if (observation !in candidates && candidates.size >= DiscoveryLimits.MAX_OBSERVATIONS) {
            // 保留当前选择，回收最旧的备用观测，避免广播淹没缓存。
            val currentHost = selected[key]?.let(host)
            val oldest = candidates.entries.filter { host(it.value.value) != currentHost }
                .minByOrNull { it.value.seen } ?: candidates.entries.minBy { it.value.seen }
            candidates.remove(oldest.key)
        }
        candidates[observation] = Observation(value, now())
        return refresh() || expired
    }

    @Synchronized
    fun removeSource(source: String): Boolean {
        peers.values.forEach { candidates -> candidates.keys.removeAll { it.first == source } }
        return refresh()
    }

    @Synchronized
    fun refresh(): Boolean {
        val time = now()
        val before = selected.toMap()
        val iterator = peers.iterator()
        while (iterator.hasNext()) {
            val (key, candidates) = iterator.next()
            candidates.values.removeAll { time - it.seen > TTL_MS }
            if (candidates.isEmpty()) {
                iterator.remove()
                selected.remove(key)
                continue
            }
            val ranked = candidates.values.sortedWith(
                compareByDescending<Observation<T>> { preferred(host(it.value)) }.thenByDescending { it.seen },
            )
            val best = ranked.first()
            val currentHost = selected[key]?.let(host)
            val current = ranked.firstOrNull { host(it.value) == currentHost }
            // 同等级地址在一个发送周期及抖动余量内保持选择，避免重复广播来回切换。
            val keep = current != null && preferred(host(current.value)) == preferred(host(best.value)) &&
                time - current.seen <= ADDRESS_HOLD_MS
            selected[key] = if (keep) current!!.value else best.value
        }
        return before != selected
    }

    @Synchronized
    fun snapshot(): List<T> = selected.values.toList()

    @Synchronized
    internal fun observationCount(id: String): Int = peers[id]?.size ?: 0

    @Synchronized
    fun clear() {
        peers.clear()
        selected.clear()
    }

    companion object {
        const val TTL_MS = 20_000L
        private const val ADDRESS_HOLD_MS = 6_000L

        fun sameSubnet(address: ByteArray, local: ByteArray, prefix: Int): Boolean {
            if (address.size != 4 || local.size != 4 || prefix !in 1..32) return false
            return (0 until prefix).all { bit ->
                val mask = 1 shl (7 - bit % 8)
                (address[bit / 8].toInt() and mask) == (local[bit / 8].toInt() and mask)
            }
        }
    }
}
