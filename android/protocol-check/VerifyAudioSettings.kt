import io.github.lyyqwq.micbridge.audio.ConfirmedAudioSettings
import io.github.lyyqwq.micbridge.audio.MicAudioSettings
import io.github.lyyqwq.micbridge.audio.PcmGain
import io.github.lyyqwq.micbridge.audio.PreprocessEffect
import io.github.lyyqwq.micbridge.audio.PreprocessEffects

private fun pcm(vararg samples: Int): ByteArray = ByteArray(samples.size * 2).also { bytes ->
    samples.forEachIndexed { index, sample ->
        bytes[index * 2] = sample.toByte()
        bytes[index * 2 + 1] = (sample shr 8).toByte()
    }
}

private fun samples(bytes: ByteArray, count: Int = bytes.size): List<Int> =
    (0 until count step 2).map { (bytes[it].toInt() and 0xFF) or (bytes[it + 1].toInt() shl 8) }

private fun checkGain() {
    check(PcmGain.factor(0) == 1.0) { "0 dB must take the exact fast path" }
    check(PcmGain.factor(20) == 10.0) { "20 dB must be exactly 10x" }
    for (db in MicAudioSettings.GAIN_RANGE) check(PcmGain.factor(db).let { it.isFinite() && it >= 1.0 })
    for (db in listOf(-1, 21)) check(runCatching { PcmGain.factor(db) }.isFailure) { "$db dB must be rejected" }

    val vector = pcm(0, 1, -1, 1000, -1000, 32767, -32768, 0x1234, -0x1234)
    val unchanged = vector.copyOf()
    PcmGain.apply(vector, vector.size, PcmGain.factor(0))
    check(vector.contentEquals(unchanged)) { "0 dB must keep every byte" }

    val amplified = pcm(1000, -1000, 0, 3276, 3277, -3276, -3277, 32767, -32768, 1)
    PcmGain.apply(amplified, amplified.size, PcmGain.factor(20))
    check(samples(amplified) == listOf(10000, -10000, 0, 32760, 32767, -32760, -32768, 32767, -32768, 10)) {
        "20 dB amplification or saturation is wrong: ${samples(amplified)}"
    }
    // little-endian：10000 = 0x2710。
    check(amplified[0] == 0x10.toByte() && amplified[1] == 0x27.toByte()) { "PCM16 must stay little-endian" }

    // 小幅样本按 round 舍入：6 dB ≈ 1.995x。
    val rounded = pcm(1, -1, 2, 3)
    PcmGain.apply(rounded, rounded.size, PcmGain.factor(6))
    check(samples(rounded) == listOf(2, -2, 4, 6)) { "Small samples must round: ${samples(rounded)}" }

    val partial = pcm(1000, 1000, 1000)
    PcmGain.apply(partial, 4, PcmGain.factor(20))
    check(samples(partial) == listOf(10000, 10000, 1000)) { "Bytes beyond count must stay untouched" }

    for (count in listOf(3, -2, partial.size + 2)) {
        check(runCatching { PcmGain.apply(partial.copyOf(), count, PcmGain.factor(0)) }.isFailure) { "count $count must be rejected" }
    }
}

private fun checkWire() {
    val valid = mapOf("gainDb" to 20, "ns" to true, "aec" to false, "agc" to null)
    check(MicAudioSettings.fromWire(valid) == MicAudioSettings(20, true, false, null))
    check(MicAudioSettings.fromWire(MicAudioSettings().toWire()) == MicAudioSettings())
    val invalid = listOf(
        null, "x", valid - "agc", valid + ("extra" to 1), valid + ("gainDb" to 21), valid + ("gainDb" to -1),
        valid + ("gainDb" to 1.5), valid + ("gainDb" to 3L), valid + ("ns" to "true"), valid + ("aec" to 0),
    )
    for (raw in invalid) check(MicAudioSettings.fromWire(raw) == null) { "Must reject $raw" }
}

private fun checkConfirmed() {
    val previous = MicAudioSettings(2, null, false, true)
    val next = MicAudioSettings(20, true, null, false)
    val confirmed = ConfirmedAudioSettings(previous)
    val captured = confirmed.snapshot
    var memory = previous
    var commits = 0
    var restoreFailures = 0
    // 模拟 commit 在失败前已经更改内存，随后恢复成功。
    check(!confirmed.save(next, { value ->
        memory = value
        check(confirmed.snapshot === previous)
        ++commits > 1
    }) { restoreFailures++ })
    check(commits == 2 && memory == previous && restoreFailures == 0)
    check(confirmed.snapshot === previous)
    // 恢复也失败时仍不能发布未确认值；错误被记录。
    check(!confirmed.save(next, { false }) { restoreFailures++ })
    check(restoreFailures == 1 && confirmed.snapshot === previous)
    check(confirmed.save(next, { value ->
        memory = value
        check(confirmed.snapshot === previous)
        true
    }) { error("Unexpected restore") })
    check(confirmed.snapshot === next && memory == next)
    check(captured === previous && captured.gainDb == 2 && captured.ns == null)
}

private class FakeEffect(
    private val control: Boolean = true,
    private val failSet: Boolean = false,
    private val failRelease: Boolean = false,
    private val failControl: Boolean = false,
    private val failRead: Boolean = false,
    private val setCode: Int = 0,
) : PreprocessEffect {
    val calls = mutableListOf<String>()
    override fun hasControl(): Boolean {
        calls += "hasControl"
        if (failControl) throw IllegalStateException("control failed")
        return control
    }
    override fun getEnabled(): Boolean {
        calls += "getEnabled"
        if (failRead) throw IllegalStateException("read failed")
        return true
    }
    override fun setEnabled(enabled: Boolean): Int {
        calls += "setEnabled($enabled)"
        if (failSet) throw IllegalStateException("set failed")
        return setCode
    }
    override fun release() {
        calls += "release"
        if (failRelease) throw IllegalStateException("release failed")
    }
}

private fun checkEffects() {
    val followSystem = FakeEffect()
    val explicit = FakeEffect(failSet = true, failRelease = true)
    val noControl = FakeEffect(control = false)
    val afterFailure = FakeEffect()
    val brokenDiagnostics = FakeEffect(failControl = true, failRead = true)
    val rejected = FakeEffect(setCode = -5)
    val logs = mutableListOf<String>()
    val effects = PreprocessEffects(
        listOf(
            PreprocessEffects.Request("ns", null) { followSystem },
            PreprocessEffects.Request("missing", true) { null },
            PreprocessEffects.Request("throws", true) { throw RuntimeException("create failed") },
            PreprocessEffects.Request("aec", false) { explicit },
            PreprocessEffects.Request("agc", true) { noControl },
            PreprocessEffects.Request("last", false) { afterFailure },
            PreprocessEffects.Request("diagnostics", true) { brokenDiagnostics },
            PreprocessEffects.Request("rejected", true) { rejected },
        ),
    ) { _, message -> logs += message }
    check(followSystem.calls.none { it.startsWith("setEnabled") }) { "Follow System must never call setEnabled" }
    check("setEnabled(false)" in explicit.calls) { "Explicit preference must be attempted" }
    check(noControl.calls.none { it.startsWith("setEnabled") }) { "No control must skip setEnabled" }
    check("setEnabled(false)" in afterFailure.calls) { "One failure must not stop later effects" }
    check(logs.any { "op=create result=null" in it } && logs.any { "op=create exception" in it } && logs.any { "skipped=no_control" in it })
    check("setEnabled(true)" in brokenDiagnostics.calls) { "Diagnostic failure must not prevent explicit preference" }
    check(logs.any { "code=-5" in it }) { "Non-success return must be logged" }
    check(afterFailure.calls.indexOf("getEnabled") < afterFailure.calls.indexOf("setEnabled(false)"))
    effects.logState()
    effects.release()
    effects.release()
    for (effect in listOf(followSystem, explicit, noControl, afterFailure, brokenDiagnostics, rejected)) {
        check(effect.calls.count { it == "release" } == 1) { "Each created effect must be released exactly once" }
    }
}

fun main() {
    checkGain()
    checkWire()
    checkConfirmed()
    checkEffects()
    println("Audio settings checks passed: 4 groups (PCM16 gain/count, wire validation, confirmed commit/restore, effect failure/release)")
}
