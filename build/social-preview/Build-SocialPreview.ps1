<#
.SYNOPSIS
    把本目录的 social-preview*.html 渲染成 GitHub 社交预览图(Settings → Social preview)。

.DESCRIPTION
    用 Edge(或 Chrome)的 headless 截图渲染,每个 HTML 产出同名 .png。
    GitHub 建议 1280×640、上限 1MB;这里默认按 2 倍设备像素渲染成 2560×1280,
    在 GitHub 卡片与社交平台的高分屏预览下更锐利。带立绘的版本 PNG 很容易超 1MB,
    超限时自动改存同名 .jpg(质量 92,GitHub 同样接受)并删掉 PNG。

.EXAMPLE
    pwsh build/social-preview/Build-SocialPreview.ps1
    pwsh build/social-preview/Build-SocialPreview.ps1 -Name social-preview-dark
#>
[CmdletBinding()]
param(
    # 设备像素比:2 → 2560×1280(默认),1 → 1280×640。
    [ValidateSet(1, 2)]
    [int]$Scale = 2,

    # 只渲染指定页面(不带扩展名);默认渲染全部 social-preview*.html。
    [string[]]$Name,

    # 浏览器可执行文件;默认自动探测 Edge、其次 Chrome。
    [string]$Browser
)

$ErrorActionPreference = 'Stop'

if (-not $Browser) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )
    $Browser = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser -or -not (Test-Path $Browser)) {
    throw '找不到 Edge/Chrome,请用 -Browser 指定浏览器可执行文件路径。'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$pages = Get-ChildItem $here -Filter 'social-preview*.html'
if ($Name) {
    $pages = $pages | Where-Object { $Name -contains $_.BaseName }
}

Add-Type -AssemblyName System.Drawing
$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -EQ 'image/jpeg'

foreach ($page in $pages) {
    $out = [System.IO.Path]::ChangeExtension($page.FullName, '.png')
    $jpg = [System.IO.Path]::ChangeExtension($page.FullName, '.jpg')
    Remove-Item $out, $jpg -ErrorAction SilentlyContinue

    # --headless=new 才支持整窗截图;--hide-scrollbars 避免右侧多出一条滚动条。
    & $Browser --headless=new --disable-gpu --hide-scrollbars `
        --force-device-scale-factor=$Scale --window-size=1280,640 `
        --screenshot="$out" "file:///$($page.FullName -replace '\\', '/')" 2>$null | Out-Null

    if (-not (Test-Path $out)) {
        throw "渲染失败,未生成 $out"
    }

    $size = (Get-Item $out).Length
    if ($size -le 1MB) {
        "$($page.BaseName).png — $([math]::Round($size / 1KB, 1)) KB"
        continue
    }

    $img = [System.Drawing.Image]::FromFile($out)
    try {
        $params = New-Object System.Drawing.Imaging.EncoderParameters 1
        $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
        $img.Save($jpg, $jpegCodec, $params)
    }
    finally {
        $img.Dispose()
    }
    Remove-Item $out
    "$($page.BaseName).jpg — $([math]::Round((Get-Item $jpg).Length / 1KB, 1)) KB(PNG 超 1MB,已转 JPEG)"
}
