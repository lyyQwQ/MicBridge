package io.github.lyyqwq.micbridge.audio

/** 系统预处理效果的最小抽象，使失败隔离可在无 Android 依赖的自检中验证。 */
internal interface PreprocessEffect {
    fun hasControl(): Boolean
    fun getEnabled(): Boolean
    fun setEnabled(enabled: Boolean): Int
    fun release()
}

/** 一次采集会话拥有的 NS/AEC/AGC 句柄；每步单独捕获异常，失败只记日志，不中断采集。 */
internal class PreprocessEffects(
    requests: List<Request>,
    private val log: (warn: Boolean, message: String) -> Unit,
) {
    class Request(val name: String, val preference: Boolean?, val create: () -> PreprocessEffect?)

    private val handles = mutableListOf<Pair<String, PreprocessEffect>>()

    init {
        for (request in requests) {
            val name = request.name
            val effect = attempt(name, "create") { request.create() }
            if (effect == null) {
                log(true, "effect=$name op=create result=null")
                continue
            }
            // 创建成功立即归入本会话，后续任何一步失败都会在 release 中释放。
            handles += name to effect
            val control = attempt(name, "hasControl") { effect.hasControl() }
            attempt(name, "getEnabled") { effect.getEnabled() }?.let { log(false, "effect=$name op=initial enabled=$it control=$control") }
            val preference = request.preference ?: continue
            // 控制权读取失败时仍尝试显式偏好；明确无控制权才跳过。
            if (control == false) {
                log(true, "effect=$name op=setEnabled skipped=no_control")
                continue
            }
            attempt(name, "setEnabled") { effect.setEnabled(preference) }?.let { code ->
                log(code != SUCCESS, "effect=$name op=setEnabled requested=$preference code=$code")
            }
            attempt(name, "getEnabled") { effect.getEnabled() }?.let { log(false, "effect=$name op=afterSet enabled=$it") }
        }
    }

    /** 启动后回读一次，仅用于诊断；SDK 状态不代表实际听感。 */
    fun logState() {
        for ((name, effect) in handles) {
            attempt(name, "getEnabled") { effect.getEnabled() }?.let { log(false, "effect=$name op=readback enabled=$it") }
        }
    }

    /** 可重复调用；单个 release 异常不影响其余句柄。 */
    fun release() {
        for ((name, effect) in handles) attempt(name, "release") { effect.release() }
        handles.clear()
    }

    private inline fun <T> attempt(name: String, operation: String, block: () -> T): T? = try {
        block()
    } catch (error: Exception) {
        log(true, "effect=$name op=$operation exception=${error.javaClass.simpleName}")
        null
    }

    private companion object {
        // 与 android.media.audiofx.AudioEffect.SUCCESS 相同。
        const val SUCCESS = 0
    }
}
