package io.github.lyyqwq.micbridge.trust

/** 未认证的入站连接不能代表已记住的电脑；本地主动连接和存储故障仍需提示。 */
internal object CandidateFailurePolicy {
    fun reportToUser(outgoing: Boolean, authenticated: Boolean, storageFailure: Boolean): Boolean =
        outgoing || authenticated || storageFailure

    fun requiresRepair(rejectionCode: String?, storageFailure: Boolean): Boolean = storageFailure ||
        rejectionCode != null && rejectionCode !in setOf("pairing_denied", "pairing_expired", "pairing_timeout", "pairing_busy")
}
