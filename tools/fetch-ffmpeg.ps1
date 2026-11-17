# 下载 / 更新随包分发的 ffmpeg（录屏编码用）。
#
#   pwsh -File tools\fetch-ffmpeg.ps1
#
# 只取 gyan.dev「essentials」构建里的 ffmpeg.exe（单文件、静态链接，约 100 MB）；
# ffprobe / ffplay 与 LTools 无关，不进发布包。二进制**不进仓库**（.gitignore 有
# /runtime/ffmpeg/），只在本地构建 / CI 发布前临时放进去。
#
# GPL 要求随二进制提供许可证与源码出处，故同步写 licenses\FFmpeg-NOTICE.txt。

[CmdletBinding()]
param(
    # 固定版本时传对应构建的 zip 地址；默认取 essentials 最新 release。
    [string] $Url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'runtime\ffmpeg'
$licenses = Join-Path $root 'licenses'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('ltools-ffmpeg-' + [guid]::NewGuid().ToString('N'))
$zip = Join-Path $temp 'ffmpeg.zip'
$extract = Join-Path $temp 'extract'

New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    Write-Host "下载 $Url …（约 100 MB）"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $exe = Get-ChildItem -Path $extract -Recurse -Filter 'ffmpeg.exe' |
        Where-Object { $_.DirectoryName -like '*\bin' } |
        Select-Object -First 1
    if (-not $exe) { throw "压缩包里没有 bin\ffmpeg.exe：$Url" }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item $exe.FullName (Join-Path $target 'ffmpeg.exe') -Force

    # 版本串只用于 NOTICE，取不到也不该让整个下载失败
    $version = 'unknown'
    try {
        $first = (& (Join-Path $target 'ffmpeg.exe') -hide_banner -version 2>&1 | Select-Object -First 1)
        # 第一行是「ffmpeg version <版本> Copyright (c) …」：只取版本那个词，别把版权声明也写进 NOTICE
        if ($first) { $version = (($first -replace '^ffmpeg version ', '') -split '\s+')[0].Trim() }
    }
    catch { Write-Warning "读取 ffmpeg 版本失败：$($_.Exception.Message)" }

    $sizeMb = [math]::Round((Get-Item (Join-Path $target 'ffmpeg.exe')).Length / 1MB, 1)
    Write-Host "已更新 runtime\ffmpeg\ffmpeg.exe（版本 $version，$sizeMb MB）"

    # GPL：许可证 + 源码出处。版本变了这里也要跟着变，别让 NOTICE 落后于二进制。
    $notice = @"
FFmpeg（随包版本 $version）
===========================

本目录下的 ffmpeg.exe 是 gyan.dev「essentials」构建的未修改二进制（单文件、静态链接），
随 LTools 一起分发，供「录屏」功能抓帧编码（MP4 / GIF）使用。

许可证：GNU General Public License v3.0（FFmpeg 本体；该构建启用 --enable-gpl
        与 --enable-version3，libx264 同样是 GPL）
项目主页：https://ffmpeg.org/
预编译构建：https://www.gyan.dev/ffmpeg/builds/
对应源码：https://ffmpeg.org/download.html#get-sources（按上面的版本号取同一版本），
        https://code.videolan.org/videolan/x264（libx264）
        （亦可向 LTools 维护者索取该构建的完整源码）

更新方式：运行 tools/fetch-ffmpeg.ps1（下载 essentials zip，只取 bin\ffmpeg.exe，
并同步更新版本号与上述源码链接）。
"@
    Set-Content -Path (Join-Path $licenses 'FFmpeg-NOTICE.txt') -Value $notice -Encoding utf8
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
