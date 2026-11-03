package io.github.lyyqwq.micbridge.discovery

/** 所有发现来源共用的资源边界；超限候选直接忽略，不截断设备身份。 */
internal object DiscoveryLimits {
    const val MAX_DEVICES = 32
    const val MAX_OBSERVATIONS = 8
    const val MAX_ID_LENGTH = 128
    const val MAX_NAME_LENGTH = 256
    const val MAX_CAPABILITIES = 16
    const val MAX_CAPABILITY_LENGTH = 64
    const val MAX_CAPABILITY_TEXT = MAX_CAPABILITIES * (MAX_CAPABILITY_LENGTH + 1)

    fun valid(id: String, name: String, role: String, host: String, port: Int,
        sources: List<String>, sinks: List<String>, transports: List<String>): Boolean =
        id.isNotBlank() && id.length <= MAX_ID_LENGTH &&
            DeviceNames.valid(name) &&
            role in setOf("windows", "android") && host.isNotBlank() && host.length <= 255 &&
            port in 1..65535 && listOf(sources, sinks, transports).all(::validCapabilities)

    fun validCapabilities(values: List<String>): Boolean = values.size <= MAX_CAPABILITIES &&
        values.all { it.isNotBlank() && it.length <= MAX_CAPABILITY_LENGTH }

    fun parseCapabilities(value: String?): List<String>? {
        if (value == null || value.isEmpty()) return emptyList()
        if (value.length > MAX_CAPABILITY_TEXT) return null
        val items = value.split(',').map(String::trim)
        return items.takeIf(::validCapabilities)
    }
}

/** 名称按完整 Unicode 码点截断；发现和认证共享同一显示名称规则。 */
internal object DeviceNames {
    fun valid(value: String): Boolean = value.isNotBlank() && validPayload(value)

    fun validPayload(value: String): Boolean = value.length <= 256 &&
        value.codePoints().allMatch { !Character.isISOControl(it) && it !in 0xD800..0xDFFF }

    fun local(configured: String?, model: String?): String =
        sequenceOf(configured, model, "Android").map { sanitize(it.orEmpty()) }.first { it.isNotBlank() }

    private fun sanitize(value: String): String {
        val result = StringBuilder()
        val points = value.codePoints().iterator()
        while (points.hasNext()) {
            val point = points.nextInt()
            if (Character.isISOControl(point) || point in 0xD800..0xDFFF) continue
            if (result.length + Character.charCount(point) > 256) break
            result.appendCodePoint(point)
        }
        return result.toString().trim()
    }

    fun utf8Prefix(value: String, maxBytes: Int): String {
        require(maxBytes >= 0)
        val result = StringBuilder()
        var bytes = 0
        val points = value.codePoints().iterator()
        while (points.hasNext()) {
            val point = points.nextInt()
            val width = when { point <= 0x7F -> 1; point <= 0x7FF -> 2; point <= 0xFFFF -> 3; else -> 4 }
            if (bytes + width > maxBytes) break
            result.appendCodePoint(point)
            bytes += width
        }
        return result.toString()
    }

    fun authenticatedName(current: String, candidate: String?): String =
        candidate?.takeIf { valid(it) && it != current } ?: current
}
