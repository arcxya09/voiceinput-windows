VoiceInput @VERSION@

使用许可
仅限个人非商业使用。禁止未经书面授权的商用、组织部署、代码修改、二次开发及二次分发。
使用前请阅读同目录 LICENSE.txt；不接受许可请勿安装或使用。
软件设置、提示词和个人词库可正常调整，用户自己的输入和生成内容不受本软件许可追加限制。
第三方组件按 licenses/ 中各自的许可证使用。

启动
双击 VoiceInput.exe。便携版需要先完整解压，再从解压后的文件夹运行。
安装版可从开始菜单启动，并通过 Windows“已安装的应用”卸载。

文件说明
VoiceInput.exe  程序入口
app/           程序、WinUI 3 和 .NET 运行库；请完整保留
licenses/      第三方组件许可证
README.txt     使用说明
LICENSE.txt    VoiceInput 个人非商业使用许可协议

升级
先从托盘菜单选择“退出”，再安装新版本。
便携版请解压到新的空文件夹。不要把新版文件覆盖到旧版运行库目录。

配置与数据
安装版与便携版均使用当前 Windows 用户的原有数据目录：
%LocalAppData%\RealtimeTranscription
已有配置、词库、纠错记录和历史会继续使用。卸载不会删除这些数据。
便携版提供免安装运行；将程序复制到其他电脑时，个人数据不会自动跟随。

开机自启动
在“设置”中开启或关闭“开机自启动”。登录 Windows 后静默启动到托盘。
便携版启用后请保留所在文件夹；移动后请重新打开程序并检查此设置。

系统要求
Windows 10 1809 或更新版本、Windows 11，x64。
已包含 .NET 和 WinUI 3 运行环境。

项目与更新
https://github.com/arcxya09/voiceinput-windows
