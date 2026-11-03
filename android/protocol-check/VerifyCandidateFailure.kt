import io.github.lyyqwq.micbridge.trust.CandidateFailurePolicy
import io.github.lyyqwq.micbridge.discovery.DeviceNames

fun main() {
    val current = "Saved computer"
    check(DeviceNames.authenticatedName(current, "新电脑😀") == "新电脑😀")
    check(DeviceNames.authenticatedName(current, "😀".repeat(128)) == "😀".repeat(128))
    val refreshed = DeviceNames.authenticatedName(current, "新电脑😀")
    for (lateName in listOf(null, "", "   ", "\u3000")) {
        check(DeviceNames.authenticatedName(refreshed, lateName) === refreshed) {
            "A late candidate without a name must preserve the latest stored name"
        }
    }
    for (candidate in listOf(null, "", "   ", "\t", "bad\nname", "bad\u0085name",
        "x".repeat(257), "😀".repeat(129), "\uD800", "\uDC00", current)) {
        check(DeviceNames.authenticatedName(current, candidate) === current) {
            "Empty, invalid or unchanged authenticated names must preserve the stored name"
        }
    }
    for (code in listOf("unknown_device", "trust_slot_occupied", "auth_malformed", "auth_proof_invalid")) {
        check(!CandidateFailurePolicy.reportToUser(false, false, false)) { "Untrusted inbound $code must not alter UI or trust" }
        check(CandidateFailurePolicy.reportToUser(true, false, false) && CandidateFailurePolicy.requiresRepair(code, false)) {
            "Explicit outgoing identity conflict $code must retain repair_required"
        }
    }
    check(CandidateFailurePolicy.reportToUser(false, true, false))
    check(CandidateFailurePolicy.reportToUser(false, false, true))
    check(CandidateFailurePolicy.requiresRepair(null, true))
    check(!CandidateFailurePolicy.requiresRepair(null, false))
    for (code in listOf("pairing_denied", "pairing_expired", "pairing_timeout", "pairing_busy")) {
        check(!CandidateFailurePolicy.requiresRepair(code, false))
    }
    println("Candidate failure checks passed: inbound isolation, explicit identity conflicts, storage failures, normal pairing decisions")
}
