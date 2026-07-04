$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectDir "Program.cs"
$icon = Join-Path $projectDir "AppIcon.ico"
$output = Join-Path $projectDir "InternetShutdownMonitor.exe"
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "未找到 C# 编译器：$compiler"
}

& $compiler `
    /target:winexe `
    /optimize+ `
    /win32icon:"$icon" `
    /out:"$output" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Security.dll `
    /reference:System.Windows.Forms.dll `
    "$source"

Write-Host "已生成：$output"
