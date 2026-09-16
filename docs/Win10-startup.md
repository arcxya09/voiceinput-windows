# Win10 录音启动与浮窗验证

适用于 VoiceInput 2.1.9，审查日期：2026-09-15。

## 所有位置起录失败

“操作失败，请检查网络、设备或保存位置”是旧版对未分类异常的统一提示，不能据此判定网络故障。若在桌面和文本框中都出现，应优先根据失败阶段检查设备打开、WASAPI 启动或识别连接。

本次反馈随后补充：测试在没有麦克风的虚拟机中进行。这与所有位置都无法起录的现象一致，不能用该现象证明 Win10 录音接口不兼容。虚拟机需要先接入或映射麦克风，并允许桌面应用访问该设备，再进行本地录音测试。没有音频输入时，识别流程无法采集语音；这与有没有文本输入焦点是两个独立条件。

失败后先打开设置 → 查看录音诊断，复制版本、阶段、失败阶段、异常类型与 HRESULT。请先保留这份诊断，再运行会覆盖它的本地麦克风测试。诊断不记录 Key、音频或识别正文。

2.1.9 对可定位的音频异常显示具体建议，并在未知音频错误中保留错误码。只有设备明确不存在、断开或失效才临时使用默认通信麦克风；权限不足或其他程序独占设备时不会反复尝试其他麦克风。已保存的设备设置保留，设备重新连接后下一轮重新使用它。

共享、事件驱动 WASAPI 初始化的两个时间参数改为零，与微软文档一致。该修改修正接口使用方式，但在未获取用户 HRESULT、未进行用户设备实测之前，不把它认定为本次启动错误的唯一原因。

## 浮窗置顶

浮窗原先已设置 IsAlwaysOnTop，但该属性不能保证它永远高于其他置顶窗口。新版在可见期间检查真实 Z 序，只在需要时调用不激活窗口的 SetWindowPos 恢复层级，不改变前台输入位置。隐藏后停止检查。

该策略适用于普通桌面的窗口。UAC 安全桌面、锁屏及独占全屏程序不属于普通窗口置顶所能保证的范围。

## 验证范围

自动回归覆盖启动失败阶段、原始异常码、失败清理、无意外上传、下一轮恢复、设备选择回退边界与 PCM 静音。Windows 原生冒烟覆盖浮窗实际 Z 序、竞争置顶窗口、显式降级、隐藏后重显，以及原有复制粘贴与安装卸载。

CI 使用 windows-latest，不是 Win10 实机；没有真实麦克风和云端识别测试。Win10 尚需以下实际验证：

| 场景 | 预期 |
| --- | --- |
| 桌面、记事本、用户原来失败的软件 | 都能开始录音；无可靠焦点时识别完成并复制 |
| 禁止桌面应用访问麦克风 | 提示权限问题，诊断保留失败阶段与 HRESULT |
| 已保存的 USB 麦克风断开、重新接入 | 断开时临时用默认通信设备，重连后下一轮恢复原设备 |
| 设备被独占或音频服务停止 | 给出对应提示，解除故障后能重新录音 |
| 录音中切换窗口、打开另一个置顶窗口 | 浮窗恢复上层，输入焦点仍在目标程序 |
| 完成提示、再次起录、隐藏后等待 | 完成期间仍置顶，新一轮正常显示，隐藏后不自行弹出 |
| 混合 DPI 多屏、系统大字体、RDP 重连 | 位置、文字和胶囊边缘需要 Win10 实测确认 |

## 官方依据

- [IAudioClient.Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [IsAlwaysOnTop](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter.isalwaysontop)
- [Windows 麦克风权限](https://support.microsoft.com/en-us/windows/privacy/turn-on-app-permissions-for-your-microphone-in-windows)

## 2.1.11 音频包兼容收尾

`PacketMetadataAnomaly` 表示驱动位置或时间戳不符合精确收尾假设，应用继续读取 PCM，并使用 `CompatibilityDrainStarted` 的停止后排空路径。`TailClockFallback` 表示松键后迟迟未收到覆盖边界的时间戳，也进入该路径。`CaptureBuffer` 中的 `bufferFrames` 才是实际缓冲容量，首包帧数并不等于缓冲容量。

详细异常包记录前 16 条，`NativeCaptureStopped` 汇总总异常数、报告缺口帧数、排空帧数。重复位置及不可信时钟本身不证明 PCM 丢失；正向位置缺口会提示用户核对复制结果。真实 COM、设备或数据队列故障仍会终止该轮，日志保留原始异常类型和 HRESULT。

参考：[WASAPI 缓冲标记](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags)、[GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)、[Stop](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-stop)、[Reset 会清空缓冲](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-reset)、[MMCSS](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)。共享事件模式继续使用两个零时长参数，遵守 [Initialize 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)。
