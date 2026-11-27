<#
    构建随包的精简 ffmpeg（只服务录屏 / 录屏的后期处理）。

    产出：runtime\ffmpeg\ffmpeg.exe（静态单文件，约 30 MB，替代 gyan.dev essentials 的 100 MB）。
    用法：
        pwsh -File tools\build-ffmpeg.ps1
        pwsh -File tools\build-ffmpeg.ps1 -Msys2Root D:\msys64 -Version 9.0.2 -Jobs 8

    依赖：MSYS2（https://www.msys2.org/，国内用清华镜像）。脚本会自动装
    mingw-w64 工具链与 x264 / x265 / libwebp / zlib / ffnvcodec / amf 头文件，外加
    MSYS 侧的 make 与 diffutils（configure 要 cmp）。
    首次大约 10 分钟（装包 + 编译），之后改清单重编约 3 分钟。

    为什么要裁剪（见 AGENTS.md「发布包体积」）：官方 essentials 构建带 cairo / sdl2 / fontconfig /
    gnutls / 各类解码器，100 MB。LTools 只用「rawvideo 管道进、WAV 进、MP4/GIF/APNG/WebP 出」，
    外加 libx264 / libx265 / NVENC / AMF 与 drawbox 进度条，别的都用不上。
    QSV 不编（Intel 用户由 RecordEncoders 探测失败后回退软件编码）：MSYS2 的 libvpl 只有 DLL，
    -static 链接不过。

    验收：编完跑 tests\RecordFfmpegSmoke（真编码 / 真混音 / 真裁剪，不需要 Windows 录屏 UI）。
#>
[CmdletBinding()]
param(
    [string] $Msys2Root = '',
    [string] $Version = '9.0.2',
    [int] $Jobs = 0,
    [switch] $SkipDeps
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repo 'runtime\ffmpeg'
$outExe = Join-Path $target 'ffmpeg.exe'

if (-not $Msys2Root) {
    # msys2/setup-msys2 在 windows runner 上默认装到 C:\msys64，但版本不同放的地方也不同，
    # 所以把几个可能的落点都试一遍（CI 里就不必再传 -Msys2Root）。
    $candidates = @($env:MSYS2_LOCATION, $env:MSYS2_ROOT, "$env:LOCALAPPDATA\msys64", 'C:\msys64')
    if ($env:RUNNER_TOOL_CACHE) { $candidates += Join-Path $env:RUNNER_TOOL_CACHE 'msys64' }
    $candidates = $candidates | Where-Object { $_ }

    $Msys2Root = $candidates |
        Where-Object { Test-Path (Join-Path $_ 'usr\bin\bash.exe') } |
        Select-Object -First 1
}
if (-not $Msys2Root) {
    throw '找不到 MSYS2。先装一个（国内：https://mirrors.tuna.tsinghua.edu.cn/msys2/distrib/msys2-x86_64-latest.exe），或用 -Msys2Root 指定。'
}
$bash = Join-Path $Msys2Root 'usr\bin\bash.exe'
if ($Jobs -le 0) { $Jobs = [Environment]::ProcessorCount }

function ToMsys([string] $path) {
    $full = [IO.Path]::GetFullPath($path).Replace('\', '/')
    if ($full -match '^([A-Za-z]):(.*)$') {
        return '/' + $Matches[1].ToLowerInvariant() + $Matches[2]
    }
    return $full
}

# 源码：优先用已经下好的 tarball，否则从镜像下（ffmpeg.org 国内很慢，ghproxy 快）。
# 候选必须过 tar -tf：下到一半的残片照样有十来 MB，只看体积会在解压时炸「Unexpected end of input」。
$temp = Join-Path ([IO.Path]::GetTempPath()) 'opencode'
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Test-Tarball([string] $path) {
    if (-not (Test-Path $path)) { return $false }
    if ((Get-Item $path).Length -lt 8MB) { return $false }
    & $bash -lc "tar -tf '$(ToMsys $path)' > /dev/null 2>&1"
    return ($LASTEXITCODE -eq 0)
}

$archive = @(
    (Join-Path $temp 'ffmpeg-src.tar.gz'),
    (Join-Path $temp "ffmpeg-$Version.tar.gz"),
    (Join-Path $temp "ffmpeg-$Version.tar.xz")
) | Where-Object { Test-Tarball $_ } | Select-Object -First 1

if (-not $archive) {
    $mirrors = @(
        @{ Url = "https://ghproxy.net/https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n$Version.tar.gz"; Name = "ffmpeg-$Version.tar.gz" },
        @{ Url = "https://ffmpeg.org/releases/ffmpeg-$Version.tar.xz"; Name = "ffmpeg-$Version.tar.xz" }
    )
    foreach ($m in $mirrors) {
        $dest = Join-Path $temp $m.Name
        try {
            Write-Host "下载源码：$($m.Url)"
            Invoke-WebRequest -Uri $m.Url -OutFile $dest -UseBasicParsing -TimeoutSec 1800
            if (Test-Tarball $dest) { $archive = $dest; break }
            Write-Warning "下载的包不完整，删掉重试：$dest"
            Remove-Item $dest -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Warning "失败：$($m.Url)（$($_.Exception.Message)）"
        }
    }
    if (-not $archive) { throw "源码下载失败；手动放一份完整的 ffmpeg-$Version.tar.xz 到 $temp 再跑。" }
}

$work = Join-Path $temp "ffmpeg-build-$Version"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# mingw64 仓库里的包名（yasm 已弃用，nasm 就够；NVENC 头文件叫 ffnvcodec-headers；AMF 是 amf-headers）
$deps = @(
    # 新装的 MSYS2 只有镜像列表、没有 sync 数据库，先刷一次（本地已有 DB 时也只是一次同步）
    'pacman -Sy --noconfirm >/dev/null 2>&1 || true',
    'pacman -S --noconfirm --needed ' + (@(
    'mingw-w64-x86_64-toolchain',
    'mingw-w64-x86_64-nasm',
    'mingw-w64-x86_64-x264',
    'mingw-w64-x86_64-x265',
    'mingw-w64-x86_64-libwebp',
    'mingw-w64-x86_64-zlib',
    'mingw-w64-x86_64-ffnvcodec-headers',
    'mingw-w64-x86_64-amf-headers',
    'mingw-w64-x86_64-pkgconf',
    # MSYS 侧（不带 mingw 前缀）：make 编 FFmpeg，diffutils 提供 configure 要用的 cmp
    'make',
    'diffutils'
) -join ' '),
    # 装完立刻报一次工具链位置：CI 上「包装到另一棵树」就是这么暴露出来的
    'echo "工具链：gcc=$(command -v gcc || echo 未找到) make=$(command -v make || echo 未找到) nasm=$(command -v nasm || echo 未找到)"'
) -join "`n"

# configure 清单：产品要用的必须齐（录屏 / 收尾 / 动图 / 裁剪），另外留几样只给冒烟用
# （tests\RecordFfmpegSmoke 与 capture-overlay-smoke 要自己合成测试音、测音量、造画面）：
#   wav 封装 + pcm_s16le 编码、volumedetect（量成品音量）、testsrc2（造测试画面）。
# QSV 不编：MSYS2 的 libvpl 只有 DLL（没有 .a），-static 链接不过；Intel 用户由 RecordEncoders 探测失败后回退软件编码。
$configure = @'
./configure \
  --prefix="$PREFIX" \
  --target-os=mingw32 --arch=x86_64 \
  --disable-shared --enable-static \
  --disable-doc --disable-debug --disable-network --disable-autodetect \
  --disable-everything --disable-programs --enable-ffmpeg --disable-ffplay --disable-ffprobe \
  --pkg-config-flags=--static \
  --extra-ldflags="-static -static-libgcc" \
  --enable-gpl --enable-zlib \
  --enable-libx264 --enable-libx265 --enable-libwebp \
  --enable-ffnvcodec --enable-nvenc --enable-amf \
  --enable-avdevice --enable-indev=lavfi \
  --enable-encoder=libx264,libx265,h264_nvenc,hevc_nvenc,h264_amf,hevc_amf,aac,gif,apng,libwebp_anim,png,wrapped_avframe,pcm_s16le \
  --enable-decoder=h264,hevc,rawvideo,wrapped_avframe,aac,png,gif,apng,webp,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le \
  --enable-demuxer=rawvideo,wav,mov,image2 \
  --enable-muxer=mp4,mov,gif,apng,webp,image2,null,wav \
  --enable-parser=h264,hevc,png \
  --enable-protocol=file,pipe \
  --enable-filter=scale,pad,fps,palettegen,paletteuse,setpts,atempo,aresample,aformat,amix,volume,drawbox,format,color,sine,anullsrc,concat,split,trim,atrim,null,anull,volumedetect,testsrc2 \
  --enable-swscale --enable-swresample --enable-avfilter
'@

$depLine = if ($SkipDeps) { '' } else { $deps }
$sh = @'
set -euo pipefail
export MSYSTEM=MINGW64
export PATH=/mingw64/bin:/usr/bin:$PATH
export PKG_CONFIG_PATH=/mingw64/lib/pkgconfig
@@DEPS@@

WORK="@@WORK@@"
PREFIX="$WORK/install"
mkdir -p "$WORK/src"
cd "$WORK/src"

ARCHIVE="@@ARCHIVE@@"
case "$ARCHIVE" in
  *.tar.xz) tar -xf "$ARCHIVE" ;;
  *)        tar -xzf "$ARCHIVE" ;;
esac
DIR=$(tar -tf "$ARCHIVE" | sed -n '1p' | cut -d/ -f1)
cd "$DIR"

# configure 挂掉时它只打一句「C compiler test failed」，真正的原因在 ffbuild/config.log 里
# （缺库 / 找不到 gcc / 链接器报错）。这里先放开 set -e，失败时把报错段捞出来再退出。
set +e
@@CONFIGURE@@
RC=$?
set -e
if [ $RC -ne 0 ]; then
  echo "---- configure 失败（退出码 $RC），config.log 里的报错段 ----"
  grep -n -i -E 'error|denied|conftest|not found|cannot|unable|undefined reference' ffbuild/config.log | head -40 || true
  exit $RC
fi

make -j@@JOBS@@
make install
strip "$PREFIX/bin/ffmpeg.exe" || true
'@

$sh = $sh.Replace('@@WORK@@', (ToMsys $work)).
    Replace('@@ARCHIVE@@', (ToMsys $archive)).
    Replace('@@JOBS@@', $Jobs.ToString()).
    Replace('@@DEPS@@', $depLine).
    Replace('@@CONFIGURE@@', $configure)

$shPath = Join-Path $work 'build.sh'
[IO.File]::WriteAllText($shPath, $sh.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

Write-Host "MSYS2：$Msys2Root"
Write-Host "工作目录：$work"
& $bash -lc "bash '$(ToMsys $shPath)'" 2>&1 | ForEach-Object { $_ }
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE），看上面的输出。" }

$built = Join-Path $work 'install\bin\ffmpeg.exe'
if (-not (Test-Path $built)) { throw "没有产出 ffmpeg.exe：$built" }

New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item $built $outExe -Force
Write-Host ("完成：{0}（{1:N1} MB）" -f $outExe, ((Get-Item $outExe).Length / 1MB))

# 自检：不是在版本串里找名字，而是真问它有哪些组件、真跑一遍 lavfi 编码。
# 漏了哪个滤镜 / 编码器在这里就报出来，别等发版后用户报「导出失败」。
$enc = & $outExe -hide_banner -encoders 2>&1 | Out-String
$flt = & $outExe -hide_banner -filters 2>&1 | Out-String
$mux = & $outExe -hide_banner -muxers 2>&1 | Out-String
$dem = & $outExe -hide_banner -demuxers 2>&1 | Out-String
$dev = & $outExe -hide_banner -devices 2>&1 | Out-String

$checks = @(
    @{ What = 'libx264'; In = $enc }, @{ What = 'libx265'; In = $enc }, @{ What = 'aac'; In = $enc },
    @{ What = 'gif'; In = $enc }, @{ What = 'apng'; In = $enc }, @{ What = 'libwebp_anim'; In = $enc },
    @{ What = 'h264_nvenc'; In = $enc }, @{ What = 'h264_amf'; In = $enc },
    @{ What = 'palettegen'; In = $flt }, @{ What = 'paletteuse'; In = $flt },
    @{ What = 'drawbox'; In = $flt }, @{ What = 'amix'; In = $flt }, @{ What = 'atempo'; In = $flt },
    @{ What = 'scale'; In = $flt }, @{ What = 'pad'; In = $flt },
    @{ What = 'volumedetect'; In = $flt }, @{ What = 'testsrc2'; In = $flt },
    @{ What = 'mp4'; In = $mux }, @{ What = 'webp'; In = $mux }, @{ What = 'null'; In = $mux },
    @{ What = 'wav'; In = $mux }, @{ What = 'pcm_s16le'; In = $enc },
    @{ What = 'rawvideo'; In = $dem }, @{ What = 'wav'; In = $dem }, @{ What = 'mov'; In = $dem },
    @{ What = 'lavfi'; In = $dev }
)

# 清单的版式是「标志列 + 名字列」（标志像 V....D / D / E），demuxer 那一列还可能写成
# 「mov,mp4,m4a,3gp,3g2,mj2」。所以按列取值、按逗号再拆，别拿正则去猜空格。
function Test-Component([string] $listing, [string] $name) {
    foreach ($line in ($listing -split "`n")) {
        $cols = ($line.Trim() -split '\s+')
        if ($cols.Count -ge 2 -and ($cols[1] -split ',') -contains $name) { return $true }
    }
    return $false
}

$missing = @()
foreach ($c in $checks) {
    if (-not (Test-Component $c.In $c.What)) { $missing += $c.What }
}

# 真跑一遍：录屏的喂帧路径（lavfi 造画面 → libx264 → mp4）与硬件探测路径（-f null）
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('ltools-ffcheck-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    & $outExe -y -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=64x64:r=10:d=0.5' `
        -c:v libx264 -pix_fmt yuv420p "$tmp\a.mp4" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $missing += 'lavfi→libx264→mp4（真跑一遍）' }

    & $outExe -y -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=64x64:r=10:d=0.2' `
        -frames:v 2 -c:v libx264 -f null - 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $missing += '硬件探测命令（-f lavfi … -f null）' }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($missing.Count -gt 0) {
    Write-Warning ('自检缺少：' + ($missing -join '、'))
    throw '精简构建缺少录屏需要的组件，别拿这份去发版。'
}
Write-Host '自检通过：编码器 / 滤镜 / 封装 / lavfi 全都在。'
