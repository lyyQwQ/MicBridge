import io.github.lyyqwq.micbridge.discovery.DiscoveryCache
import io.github.lyyqwq.micbridge.discovery.DiscoveryLimits
import io.github.lyyqwq.micbridge.discovery.DeviceNames

private data class Peer(val id: String, val host: String, val port: Int = 17891)

fun main() {
    check(DeviceNames.local("  我的手机😀  ", "Model") == "我的手机😀")
    check(DeviceNames.local("\u0000\n\t", " Model ") == "Model")
    check(DeviceNames.local(null, " \t ") == "Android")
    check(DeviceNames.local("A\u0000B\u0085C\n", "Model") == "ABC")
    check(DeviceNames.local("x".repeat(255) + "😀", null) == "x".repeat(255))
    check(DeviceNames.local("😀".repeat(129), null) == "😀".repeat(128))
    check(DeviceNames.local("\uD800手机\uDC00", null) == "手机")
    for ((source, expected) in listOf(
        "a".repeat(64) to "a".repeat(63),
        "中".repeat(22) to "中".repeat(21),
        "😀".repeat(16) to "😀".repeat(15),
        ("中".repeat(19) + "😀😀") to ("中".repeat(19) + "😀"),
        ("a".repeat(62) + "😀") to "a".repeat(62),
    )) {
        val label = DeviceNames.utf8Prefix(source, 63)
        check(label == expected && label.toByteArray(Charsets.UTF_8).size <= 63)
        check(String(label.toByteArray(Charsets.UTF_8), Charsets.UTF_8) == label)
    }
    check(DeviceNames.utf8Prefix("😀", 0).isEmpty())
    check(DeviceNames.utf8Prefix("中".repeat(100), 250) == "中".repeat(83))
    check(!DeviceNames.valid("name\n") && !DeviceNames.valid("name\u0085"))
    check(!DeviceNames.valid("\uD800") && !DeviceNames.valid("\uDC00"))
    check(DeviceNames.valid("😀".repeat(128)))
    check(!DeviceNames.valid("😀".repeat(129)))
    for (blank in listOf("", "   ", "\u3000", " ".repeat(256))) {
        check(DeviceNames.validPayload(blank) && !DeviceNames.valid(blank)) {
            "Bounded control-free blank HELLO names are valid payloads but cannot refresh trust"
        }
    }
    for (invalid in listOf(" ".repeat(257), "\t", "\n", "\u0085", "\uD800", "\uDC00")) {
        check(!DeviceNames.validPayload(invalid))
    }
    var time = 0L
    fun cache() = DiscoveryCache<Peer>({ it.id }, { it.host }, { it.startsWith("wifi") }, { time })
    val service = cache()
    val page = cache()
    val other = Peer("instance", "other")
    val wifi = Peer("instance", "wifi-a")
    check(service.observe(other, "udp"))
    check(service.observe(wifi, "udp"))
    check(service.snapshot() == listOf(wifi)) { "Same instance must have one preferred endpoint" }
    check(!service.observe(other, "udp")) { "Off-subnet packet must not replace Wi-Fi" }
    val secondWifi = wifi.copy(host = "wifi-b")
    time = 1
    check(!service.observe(secondWifi, "udp")) { "Equal-rank addresses must not flap" }
    time = 6_002
    service.observe(secondWifi, "udp")
    check(service.snapshot() == listOf(secondWifi)) { "Stale selection must yield to recent address" }

    service.clear()
    time = 0
    for (cycle in 0..12) {
        time = cycle * 5_000L
        val changed = service.observe(wifi, "udp")
        check(changed == (cycle == 0))
        // 下游接收每次真实观测，而非仅接收变化通知。
        page.observe(wifi, "bus")
        time += 4_999
        service.refresh()
        page.refresh()
        check(service.snapshot() == listOf(wifi))
        check(page.snapshot() == listOf(wifi)) { "Unchanged heartbeats must renew page TTL" }
    }
    time = 60_000 + DiscoveryCache.TTL_MS
    service.refresh()
    page.refresh()
    check(service.snapshot().size == 1 && page.snapshot().size == 1)
    time++
    check(service.refresh() && page.refresh())
    check(service.snapshot().isEmpty() && page.snapshot().isEmpty()) { "Expired peers must disappear" }

    time = 0
    service.observe(wifi, "udp")
    service.observe(wifi, "mdns:one")
    check(!service.removeSource("mdns:one"))
    check(service.snapshot() == listOf(wifi)) { "mDNS loss must preserve UDP" }
    service.observe(Peer("other-instance", "other"), "mdns:two")
    service.removeSource("mdns:two")
    check(service.snapshot() == listOf(wifi)) { "Removing another peer must be incremental" }
    time = DiscoveryCache.TTL_MS + 1
    service.refresh()
    check(service.snapshot().isEmpty()) { "mDNS loss must not renew UDP TTL" }

    val address = byteArrayOf(192.toByte(), 0, 2, 7)
    val local = byteArrayOf(192.toByte(), 0, 2, 100)
    check(DiscoveryCache.sameSubnet(address, local, 24))
    check(!DiscoveryCache.sameSubnet(address, local, 26))
    check(!DiscoveryCache.sameSubnet(address, ByteArray(16), 24))
    fun valid(id: String = "peer", name: String = "Windows", caps: List<String> = listOf("tcp")) =
        DiscoveryLimits.valid(id, name, "windows", "host", 17891, caps, emptyList(), caps)
    check(valid("i".repeat(DiscoveryLimits.MAX_ID_LENGTH), "n".repeat(DiscoveryLimits.MAX_NAME_LENGTH)))
    check(!valid("i".repeat(DiscoveryLimits.MAX_ID_LENGTH + 1)))
    check(!valid(name = "n".repeat(DiscoveryLimits.MAX_NAME_LENGTH + 1)))
    check(!valid(id = " ") && !valid(name = " "))
    check(valid(caps = List(DiscoveryLimits.MAX_CAPABILITIES) { "c".repeat(DiscoveryLimits.MAX_CAPABILITY_LENGTH) }))
    check(!valid(caps = List(DiscoveryLimits.MAX_CAPABILITIES + 1) { "tcp" }))
    check(!valid(caps = listOf("c".repeat(DiscoveryLimits.MAX_CAPABILITY_LENGTH + 1))))
    check(DiscoveryLimits.parseCapabilities("tcp,udp") == listOf("tcp", "udp"))
    check(DiscoveryLimits.parseCapabilities("x,".repeat(DiscoveryLimits.MAX_CAPABILITIES)) == null)
    check(DiscoveryLimits.parseCapabilities("x".repeat(DiscoveryLimits.MAX_CAPABILITY_TEXT + 1)) == null)
    val bounded = cache()
    repeat(DiscoveryLimits.MAX_DEVICES + 20) { bounded.observe(Peer("peer-$it", "wifi-$it"), "udp") }
    check(bounded.snapshot().size == DiscoveryLimits.MAX_DEVICES)
    repeat(100) { bounded.observe(Peer("peer-0", "wifi-new-$it"), "mdns:$it") }
    check(bounded.observationCount("peer-0") == DiscoveryLimits.MAX_OBSERVATIONS)
    check(bounded.snapshot().any { it.id == "peer-0" && it.host == "wifi-0" })
    time += DiscoveryCache.TTL_MS + 1
    bounded.observe(Peer("replacement", "wifi-replacement"), "udp")
    check(bounded.snapshot() == listOf(Peer("replacement", "wifi-replacement")))
    println("Discovery checks passed: identity/address selection, heartbeat TTL, expiry, source isolation, subnet, metadata limits, device/observation caps")
}
