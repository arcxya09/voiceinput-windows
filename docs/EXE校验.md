# Windows 语音输入法 EXE 校验

版本：1.3.0 纠错学习（预发布）  
文件：`VoiceInput-Windows-x64-1.3.0.exe`  
大小：77035451 字节  
格式：Windows PE32+，AMD64 `0x8664`，GUI 子系统 `2`。  
文件与清单版本：`1.3.0.0`。  
发布：自包含、单文件、.NET 10.0.12 运行时；未签名。  
图标与清单：PE 包含 RT_ICON、RT_GROUP_ICON、RT_VERSION、RT_MANIFEST；9 个图标资源与源码 ICO 一致。

SHA-256：

```text
435c9d1561cd8c988bf789d58e88828ceb90ca22381217dd590a2652e216dc4d
```

PowerShell 校验：

```powershell
Get-FileHash .\VoiceInput-Windows-x64-1.3.0.exe -Algorithm SHA256
```

117 项自动测试通过，测试目标编译及 Windows 目标发布无警告、无错误。62 处 XAML 事件引用静态检查通过。Windows GUI、DPAPI、麦克风与付费 API 联调尚未执行，详见[验证记录](验证记录.md)。

升级前先退出旧版并备份完整数据目录。新版将数据库升级为版本 2；旧版回退须恢复升级前备份，详见[1.3.0 说明](更新记录_1.3.0.md#升级与回退)。
