# Win10 输入与剪贴板策略

研究日期：2026-09-15。适用于 VoiceInput 2.1.8。

## 结论

Windows 10 支持完整正文写入剪贴板，再通过一次 Ctrl+V 交给当前输入区域。发送这组按键无需获取可见光标或光标坐标。检测不到光标、UI Automation 控件或文本模式，并不等于目标软件无法接收粘贴。

键盘焦点与显示光标是独立信息。目标软件仍需要实际接收粘贴的输入上下文；前台窗口存在不能证明其当前控件可编辑。程序也无法凭一次 SendInput 返回值证明目标已插入正文。

## 2.1.8 的行为

1. 按住说话后完成录音、识别、尾句收齐及已启用的文字处理。无输入焦点不影响这条流程。
2. 完整、非空、未取消的结果自动写入 CF_UNICODETEXT 剪贴板。复制本身不查询光标或要求目标控件。
3. 仅听写、没有原生输入焦点或确认目标不宜自动粘贴时，显示“已复制”，用户可自行 Ctrl+V。
4. 原窗口、进程、线程及非零原生焦点稳定时，允许整段一次 Ctrl+V。UIA 能提供可靠控件和选区信息时继续验证；UIA 未知时允许原生兼容路径。
5. 明确密码控件拒绝启动；明确只读/禁用控件只听写并复制。未知 UIA 信息不能保证识别所有密码框。
6. 自动粘贴前核验按键、输入法候选状态及剪贴板序号。明确切换目标时不抢回焦点，已发出的粘贴不重复发送。

只依赖原生 HWND 的兼容路径，无法识别同一 HWND 内全部逻辑编辑控件或选区变化；它采用当前实际焦点的粘贴语义。需要对具体软件进行验证。管理员权限软件还受 UIPI 完整性级别限制，应用也可能不支持或拦截 Ctrl+V。

## 对其他方式的判断

WM_PASTE 可用于支持该消息的标准编辑控件，但不能假定所有浏览器、嵌入式编辑器和自绘界面都支持，因此不作为未知控件的自动兜底。UIA ValuePattern.SetValue 设置控件值，不是通用的在原插入点追加正文接口。逐字模拟键入不再使用，避免重现中文及标点上屏错乱问题。

## 微软官方资料

- [键盘输入及焦点路由](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input)
- [GUITHREADINFO：焦点与光标分别存储](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-guithreadinfo)
- [GetGUIThreadInfo：跨进程查询及窗口切换时的无效信息](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo)
- [SendInput：串行注入、修饰键状态与 UIPI 限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [SetClipboardData：格式、所有权及内存要求](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setclipboarddata)
- [UI Automation 控件模式](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview)
- [WM_PASTE 支持范围](https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-paste)
- [ValuePattern.SetValue](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationvaluepattern-setvalue)


## 2026-09-17：无需剪贴板的上屏方案

目标是避免其他进程占用、替换剪贴板影响输入。不存在对所有 Windows 应用均可靠的通用后台写入接口。下表为基于微软接口契约和当前代码结构的工程判断，尚未在用户反馈的目标软件中验证。

| 方案 | 能否绕开剪贴板 | 适用范围与限制 |
| --- | --- | --- |
| TSF 文本服务 | 可以 | 在目标文档的读写编辑会话中向当前选区提交整段文字，适合长期输入法架构；需要注册、激活文本服务及管理目标上下文，不能仅在现有托盘进程调用一次接口就写入任意应用。仍需验证各编辑器兼容性。 |
| 应用专用插件或编辑接口 | 可以 | 能按目标编辑器的插入与撤销规则提交内容，但需要逐个应用接入，无法覆盖未知软件。 |
| 标准 Edit/RichEdit 的 EM_REPLACESEL | 可以 | 可替换选区或在插入点写入；仅适用于确认为支持该消息的标准控件，不能作为网页或自绘编辑器的通用接口。 |
| UI Automation | 部分可以 | TextPattern 提供读取和选区操作，不能写入；ValuePattern.SetValue 可设置支持控件的值，但并非通用的光标位置插入接口，贸然设置可能替换整个输入框。 |
| SendInput + KEYEVENTF_UNICODE | 可以 | 通过 VK_PACKET/WM_CHAR 发送字符，目标必须正确处理；仍受焦点、按键状态、UIPI 等影响。不能把接受输入事件等同于正文已完整插入。 |
| 当前整段剪贴板粘贴 | 不可以 | 对各类编辑器覆盖较广，但依赖共享剪贴板；2.4.1 加强了占用恢复和失败提示。 |

建议：长期以 TSF 文本服务为主方向，先制作独立原型验证常用应用、中文输入法候选态、选区替换和撤销；短期保留已验证的整段粘贴，可按明确支持的目标增加专用适配。任一路径可能已插入部分正文后，都不应盲目切换另一种方式重发，以免重复输入。此处为方案建议，本版没有切换上屏接口。

微软资料：

- [TSF 框架](https://learn.microsoft.com/en-us/windows/win32/tsf/about-text-services-framework)
- [在选区提交文字与读写编辑会话](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinsertatselection-inserttextatselection)
- [UI Automation TextPattern 与 TSF 的定位](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview)
- [Unicode 键盘输入的处理机制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)
- [SendInput 的权限和按键状态限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [标准编辑控件 EM_REPLACESEL](https://learn.microsoft.com/en-us/windows/win32/controls/em-replacesel)
