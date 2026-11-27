# 取一份随包分发的 ffmpeg（录屏编码用）。
#
#   pwsh -File tools\fetch-ffmpeg.ps1                 # 已有就用，没有就下
#   pwsh -File tools\fetch-ffmpeg.ps1 -Force          # 已有也重下
#   pwsh -File tools\fetch-ffmpeg.ps1 -Version 9.0.2
#
# 三个来源按顺序尝试：
#   ① runtime\ffmpeg\ffmpeg.exe 已经存在（tools\build-ffmpeg.ps1 刚编出来的，或上次取下来的）→ 直接用；
#   ② 本仓库 release 上的预编译精简构建（tag 形如 `ffmpeg-9.0.2`，约 30 MB，由
#      .github/workflows/ffmpeg.yml 产出，只含录屏用得到的编码器 / 封装 / 滤镜）；
#   ③ gyan.dev「essentials」（约 100 MB，兜底；包会大一倍，所以会打一条警告）。
#
# 二进制**不进仓库**（.gitignore 有 /runtime/ffmpeg/），只在本地构建 / CI 发布前临时放进来。
# GPL 要求随二进制提供许可证与源码出处，故同步写 licenses\FFmpeg-NOTICE.txt（按来源写不同的正文）。

[CmdletBinding()]
param(
    [string] $Version = '9.0.2',
    # 预编译精简构建的下载地址；默认取本仓库该版本的 release 附件。
    [string] $AssetUrl = '',
    # gyan.dev 的兜底 zip（版本号对不上时才有意义，一般不用改）。
    [string] $FallbackUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'runtime\ffmpeg'
$exe = Join-Path $target 'ffmpeg.exe'
$licenses = Join-Path $root 'licenses'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('ltools-ffmpeg-' + [guid]::NewGuid().ToString('N'))

if (-not $AssetUrl) {
    $AssetUrl = "https://github.com/lazyst/LTools/releases/download/ffmpeg-$Version/ffmpeg.exe"
}

function Get-Version([string] $path) {
    try {
        $first = (& $path -hide_banner -version 2>&1 | Select-Object -First 1)
        # 第一行是「ffmpeg version <版本> Copyright (c) …」：只取版本那个词，别把版权声明也写进 NOTICE
        if ($first) { return (($first -replace '^ffmpeg version ', '') -split '\s+')[0].Trim() }
    }
    catch { }
    return 'unknown'
}

# 能跑 + 带 libx264 才算可用：下到一半的 exe 或残缺构建不能放进去冒充成功。
function Test-Ffmpeg([string] $path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $out = (& $path -hide_banner -version 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { return $false }
        return ($out -match 'libx264')
    }
    catch { return $false }
}

function Write-Notice([string] $version, [string] $kind) {
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    $source = $version -replace '^n', ''
    $body = if ($kind -eq 'compact') {
        @"
FFmpeg（随包版本 $version，LTools 精简构建）
===========================================

本目录下的 ffmpeg.exe 由 LTools 自己编译（静态单文件，未修改 FFmpeg 源码），
随 LTools 一起分发，供「录屏」抓帧编码（MP4 / MOV / GIF / APNG / WebP）与后期裁剪使用。

许可证：GNU General Public License v3.0
        （--enable-gpl；libx264 / libx265 同样是 GPL）
项目主页：https://ffmpeg.org/
构建脚本：本仓库 tools/build-ffmpeg.ps1（含完整 configure 参数）
对应源码：https://ffmpeg.org/releases/ffmpeg-$source.tar.xz
        https://code.videolan.org/videolan/x264
        https://code.videolan.org/videolan/x265
        （亦可向 LTools 维护者索取该构建的完整源码与编译产物）

构建要点（--disable-everything 后逐项开启）：libx264 / libx265 / NVENC / AMF / aac /
gif / apng / libwebp_anim；mp4 / mov / gif / apng / webp / image2 封装；
rawvideo / wav / mov / lavfi 解封装；scale / pad / fps / palettegen / paletteuse /
setpts / atempo / aresample / aformat / amix / volume / drawbox 等滤镜。

更新方式：重跑 tools/build-ffmpeg.ps1（或 .github/workflows/ffmpeg.yml），
再运行 tools/fetch-ffmpeg.ps1 取回并更新这里的版本号。
"@
    }
    else {
        @"
FFmpeg（随包版本 $version）
===========================

本目录下的 ffmpeg.exe 是 gyan.dev「essentials」构建的未修改二进制（单文件、静态链接），
随 LTools 一起分发，供「录屏」功能抓帧编码使用。

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
    }
    Set-Content -Path (Join-Path $licenses 'FFmpeg-NOTICE.txt') -Value $body -Encoding utf8
}

# NOTICE 必须跟二进制对得上：自己编的是精简构建（GPL + 该构建的源码出处），
# gyan.dev 的是完整构建。靠 -buildconf 判断，别让版本串骗人。
function Sync-Notice([string] $path) {
    $conf = (& $path -hide_banner -buildconf 2>&1 | Out-String)
    $kind = if ($conf -match '--disable-everything') { 'compact' } else { 'full' }
    Write-Notice (Get-Version $path) $kind
}

New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    if ((-not $Force) -and (Test-Ffmpeg $exe)) {
        $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Sync-Notice $exe
        Write-Host "已存在 runtime\ffmpeg\ffmpeg.exe（$(Get-Version $exe)，$sizeMb MB），跳过下载。要重下加 -Force。"
        return
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # ② 预编译精简构建：单文件，直接就是 ffmpeg.exe
    $downloaded = $false
    try {
        Write-Host "下载精简构建 $AssetUrl …（约 30 MB）"
        Invoke-WebRequest -Uri $AssetUrl -OutFile $exe -UseBasicParsing -TimeoutSec 600
        if (Test-Ffmpeg $exe) {
            $downloaded = $true
            Sync-Notice $exe
            Write-Host ("已更新 runtime\ffmpeg\ffmpeg.exe（$(Get-Version $exe)，{0:N1} MB，精简构建）" -f ((Get-Item $exe).Length / 1MB))
        }
        else {
            Write-Warning "下载到的不是可用的 ffmpeg（版本 $AssetUrl），改用兜底源。"
            Remove-Item $exe -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "取精简构建失败：$($_.Exception.Message)"
    }

    # ③ 兜底：gyan.dev essentials（100 MB，什么都带）
    if (-not $downloaded) {
        $zip = Join-Path $temp 'ffmpeg.zip'
        $extract = Join-Path $temp 'extract'
        Write-Host "下载 $FallbackUrl …（约 100 MB）"
        Invoke-WebRequest -Uri $FallbackUrl -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $extract -Force

        $found = Get-ChildItem -Path $extract -Recurse -Filter 'ffmpeg.exe' |
            Where-Object { $_.DirectoryName -like '*\bin' } |
            Select-Object -First 1
        if (-not $found) { throw "压缩包里没有 bin\ffmpeg.exe：$FallbackUrl" }

        Copy-Item $found.FullName $exe -Force
        if (-not (Test-Ffmpeg $exe)) { throw "取回的 ffmpeg.exe 跑不起来：$($found.FullName)" }

        $version = Get-Version $exe
        Write-Notice $version 'full'
        Write-Host ("已更新 runtime\ffmpeg\ffmpeg.exe（$version，{0:N1} MB，gyan.dev essentials）" -f ((Get-Item $exe).Length / 1MB))
        Write-Warning "用的是 100 MB 的完整构建。发版前建议先跑 tools\build-ffmpeg.ps1（约 30 MB），包会小一半。"
    }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
