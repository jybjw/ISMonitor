# Internet 断网关机监控

这是一个 Windows 窗体程序，用于持续检测当前电脑是否能够连接到 Internet。

## 功能

- 可设置断网后的关机倒计时时间，默认 10 分钟。
- 可设置检测间隔，默认 10 秒。
- 点击“开始检测”后持续检测 Internet。
- 一旦检测不到 Internet，开始倒计时。
- 倒计时期间仍持续检测网络。
- 如果 Internet 恢复，自动取消倒计时。
- 倒计时归零后调用 Windows 关机命令。
- 支持最小化到系统托盘。
- 已内置专用程序图标。

## 编译

在 Windows PowerShell 中进入本目录，然后运行：

```powershell
.\build.ps1
```

脚本会使用系统自带的 .NET Framework C# 编译器生成 `InternetShutdownMonitor.exe`。

## 使用

1. 打开 `InternetShutdownMonitor.exe`。
2. 设置“断网后关机倒计时（分钟）”。
3. 设置“检测间隔（秒）”。
4. 点击“开始检测”。
5. 最小化窗口后，程序会留在托盘继续运行。

## 注意

- 程序只有在倒计时归零时才会执行 `shutdown.exe /s /t 0`。
- 关闭主窗口会退出检测程序。
- 如果程序被强制结束，内部倒计时也会停止。
