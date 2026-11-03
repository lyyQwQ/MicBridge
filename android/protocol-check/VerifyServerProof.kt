import io.github.lyyqwq.micbridge.protocol.Protocol

/** Standalone JVM checks; compile together with Protocol.kt. No Android runtime or test framework. */
fun main() {
    val secret = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
    val windows = "0".repeat(32)
    val android = "1".repeat(32)
    val windowsConnection = "2".repeat(32)
    val androidConnection = "3".repeat(32)
    val pairing = "4".repeat(32)
    val vector = Protocol.serverAuthProof(secret, windows, android, windowsConnection, androidConnection,
        pairing, "challenge", "nonce", "phone-proof")
    check(vector == "YDV738wX-8B_PNSxQN1zZFZajFfbMcOTvGlix6RpNrQ") { "Server proof differs from the independent HMAC vector" }
    check(Protocol.verifyServerProof(Protocol.Type.AUTH_OK, vector, vector))
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, vector, "A" + vector.drop(1))) { "Corrupt Windows proof was accepted" }
    check(!Protocol.verifyServerProof(Protocol.Type.STATUS, vector, vector)) { "STATUS bypassed AUTH_OK" }
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_PROOF, vector, vector)) { "Phone proof frame bypassed AUTH_OK" }
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, vector, "")) { "Empty proof was accepted" }

    val phoneProof = Protocol.authProof(secret, "challenge", android, windows, androidConnection, windowsConnection)
    val serverProof = Protocol.serverAuthProof(secret, windows, android, windowsConnection, androidConnection,
        pairing, "challenge", "nonce", phoneProof)
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, serverProof, phoneProof)) { "Reflected phone proof was accepted as a Windows proof" }
    val changedNonce = Protocol.serverAuthProof(secret, windows, android, windowsConnection, androidConnection,
        pairing, "challenge", "different-nonce", phoneProof)
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, changedNonce, serverProof)) { "Old nonce proof was replayed" }
    val changedPairing = Protocol.serverAuthProof(secret, windows, android, windowsConnection, androidConnection,
        "5".repeat(32), "challenge", "nonce", phoneProof)
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, changedPairing, serverProof)) { "Proof from a different pairing was accepted" }
    val changedConnection = Protocol.serverAuthProof(secret, windows, android, "6".repeat(32), androidConnection,
        pairing, "challenge", "nonce", phoneProof)
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, changedConnection, serverProof)) { "Proof from an old connection was replayed" }
    val changedSecret = Protocol.serverAuthProof(secret.reversed(), windows, android, windowsConnection, androidConnection,
        pairing, "challenge", "nonce", phoneProof)
    check(!Protocol.verifyServerProof(Protocol.Type.AUTH_OK, changedSecret, serverProof)) { "Proof with an incorrect secret was accepted" }
    println("Windows proof verification checks passed")
}
