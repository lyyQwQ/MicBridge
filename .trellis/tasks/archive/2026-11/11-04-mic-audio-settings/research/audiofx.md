# Android audiofx 官方调研

查阅日期：2026-11-04。只读文档和源码；未进行真机测试。API 契约、AOSP 实现与设备推测分别标注。

## 官方来源

- S1 [NoiseSuppressor](https://developer.android.com/reference/android/media/audiofx/NoiseSuppressor)
- S2 [AcousticEchoCanceler](https://developer.android.com/reference/android/media/audiofx/AcousticEchoCanceler)
- S3 [AutomaticGainControl](https://developer.android.com/reference/android/media/audiofx/AutomaticGainControl)
- S4 [AudioEffect](https://developer.android.com/reference/android/media/audiofx/AudioEffect#setEnabled(boolean))；同页 getEnabled、hasControl、release、控制权和 enabled 状态监听器。
- S5 [AOSP Configure preprocessing effects](https://source.android.com/docs/core/audio/implement-pre-processing)
- S6 [AOSP NoiseSuppressor.java](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/media/java/android/media/audiofx/NoiseSuppressor.java)
- S7 [AOSP AcousticEchoCanceler.java](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/media/java/android/media/audiofx/AcousticEchoCanceler.java)
- S8 [AOSP AutomaticGainControl.java](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/media/java/android/media/audiofx/AutomaticGainControl.java)

## 结论与证据边界

1. **API 契约**：三种预处理器均从 API 16 提供。isAvailable() 检查设备是否实现对应效果；create(audioSessionId) 将效果附加到同 session 的 AudioRecord，设备未实现可返回 null。它不是某一输入路由的音质保证。[S1–S3]
2. **AOSP 当前实现**：三类 create 均捕获 IllegalArgumentException（未实现）、UnsupportedOperationException（预处理资源超限）、RuntimeException（实现注释称内存分配失败）并返回 null。因此 isAvailable=true 也必须处理 create=null；不能把所有 null 都解释为永久不支持。main 分支源码只能证明参考实现，不能替代 OEM 固件实测。[S6–S8]
3. **API 契约**：setEnabled(boolean) 在现有 effect/session 上启停，无需为每次设置重新 create；返回 SUCCESS 或 ERROR_INVALID_OPERATION/ERROR_DEAD_OBJECT，也可能抛 IllegalStateException。hasControl 和控制权监听器可报告是否拥有控制权，getEnabled 回读实际状态。release 释放 native 资源。[S4]
4. **推测／待真机验证**：录音中直接调用 setEnabled 是 API 可行方案，但官方文档不承诺切换延迟、无咔哒声、无丢首字或各厂商均成功。本任务已决定全部下次采集生效，不实施实时切换。[S4]
5. **API 与平台文档**：三种效果可能按 AudioSource 被平台默认插入；必须在 create 后、任何显式 setEnabled 前读取 getEnabled()。类说明中 getEnable() 是文字拼写，实际继承方法为 getEnabled()。VOICE_COMMUNICATION 不等于三个效果一律默认开启；默认配置由厂商 /vendor/etc/audio_effects.xml 与产品路由规则决定。Android 10 的指南要求实现应提供 AEC，若提供则必须经 SDK 可发现和控制。[S1–S5]
6. **推测／待真机验证**：未找到上述官方文档对所有 Bluetooth SCO 输入应用 NS/AEC/AGC 的统一保证。S5 明确 AudioSource 到设备、增益和预处理的映射由产品规则决定。SCO/Ray-Ban 可能已有设备端处理，可能与手机端叠加、被旁路或听不出差异；这些都是推测。isAvailable=true、create 成功、getEnabled=true 只证明 SDK 状态，不证明 Ray-Ban 的声音确实改变。[S1–S5]
7. **功能边界**：AEC 处理捕获信号中远端回放贡献；不承诺消除本项目 Windows 扬声器的回声，本任务不增加远端回放参考路径。[S2]

## 规划影响

- UI 仅展示用户偏好与 isAvailable 结果；不采集时不虚构实际 enabled，也不为探测状态创建 AudioRecord。实际状态只记日志。
- 每个效果固定提供「系统默认 / 开 / 关」三段单选，对应 null / true / false；选择系统默认时不调用 setEnabled，跟随每个新 session 的初始状态，且不将读回布尔值持久化；选择开/关时在下次采集遵循保存的偏好。
- 创建失败、无控制权、设置失败与读回结果仅记录日志；单项失败不阻止基础采集。UI 只对 isAvailable=false 显示禁用原因，不把偏好当成实际已开启/关闭。
- 真机验收按内置麦、Ray-Ban/SCO 分开记录初始状态、实际状态、设置返回码、采集连续性；不保存 PCM 或个人信息。
