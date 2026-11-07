# 下载 / 更新随包分发的 AutoHotkey v2（官方 ahk-v2.zip 的 64 位主程序）。
#
#   pwsh -File tools\fetch-autohotkey.ps1
#
# 只取 AutoHotkey64.exe 与 license.txt：其余文件（安装脚本、CHM、32 位版）
# 与「执行用户脚本」无关，放进发布包只会变大。
# GPL-2.0 要求随二进制提供许可证与源码出处，故同步写 licenses\AutoHotkey-NOTICE.txt。

[CmdletBinding()]
param(
    # 官方「最新 v2」压缩包；要固定某个版本时传对应的 releases/download 地址。
    [string] $Url = 'https://www.autohotkey.com/download/ahk-v2.zip'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'runtime\autohotkey'
$licenses = Join-Path $root 'licenses'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('ltools-ahk-' + [guid]::NewGuid().ToString('N'))
$zip = Join-Path $temp 'ahk-v2.zip'
$extract = Join-Path $temp 'extract'

New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    Write-Host "下载 $Url …"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $exe = Join-Path $extract 'AutoHotkey64.exe'
    $license = Join-Path $extract 'license.txt'
    if (-not (Test-Path $exe)) { throw "压缩包里没有 AutoHotkey64.exe：$Url" }
    if (-not (Test-Path $license)) { throw "压缩包里没有 license.txt：$Url" }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item $exe (Join-Path $target 'AutoHotkey64.exe') -Force
    Copy-Item $license (Join-Path $licenses 'AutoHotkey-LICENSE.txt') -Force

    $version = (Get-Item (Join-Path $target 'AutoHotkey64.exe')).VersionInfo.FileVersion
    Write-Host "已更新 runtime\autohotkey\AutoHotkey64.exe（版本 $version）"

    # GPL-2.0：许可证 + 源码出处。版本变了这里也要跟着变，别让 NOTICE 落后于二进制。
    $notice = @"
AutoHotkey v2（随包版本 $version）
=====================================

本目录下的 AutoHotkey64.exe 是 AutoHotkey 官方发布的未修改二进制，
随 LTools 一起分发，供「脚本 → AutoHotkey 脚本」单元执行用户脚本。

许可证：GNU General Public License v2.0（全文见同目录 AutoHotkey-LICENSE.txt）
项目主页：https://www.autohotkey.com/
对应源码：https://github.com/AutoHotkey/AutoHotkey/releases/tag/v$version
          （亦可向 LTools 维护者索取该版本的完整源码）

更新方式：运行 tools/fetch-autohotkey.ps1（会下载官方最新 ahk-v2.zip，
只取 AutoHotkey64.exe 与 license.txt，并同步更新版本号与上述源码链接）。
"@
    Set-Content -Path (Join-Path $licenses 'AutoHotkey-NOTICE.txt') -Value $notice -Encoding utf8
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
