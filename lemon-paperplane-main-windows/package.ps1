# 打 Windows 发布包：Release 编译界面 → 按架构整理安装目录 → 免安装 zip + 安装包（Inno Setup）。
#
#   .\package.ps1 -Version 0.1.0 -RuntimeRoot <内核目录> -EditorDir <编辑器网页 dist> [-Arch x64|arm64|all]
#
# RuntimeRoot 下每个架构一个子目录（amd64 / arm64），内容由 ../scripts/build-windows-runtime.sh 生成：
#   paperplane.exe、paperplane-plugins.exe、plugins\、store\
# 产物在 dist\：LemonPaperplane-<版本>-windows-<架构>.zip 与 …-setup.exe
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [string]$EditorDir = "",
    [ValidateSet("x64", "arm64", "all")][string]$Arch = "all",
    [string]$ISCC = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $here "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

function Find-ISCC {
    if ($ISCC -and (Test-Path $ISCC)) { return $ISCC }
    if ($env:PAPERPLANE_ISCC -and (Test-Path $env:PAPERPLANE_ISCC)) { return $env:PAPERPLANE_ISCC }
    foreach ($c in @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

Write-Host "==> 编译界面（Release $Version）"
& (Join-Path $here "build.ps1") -Release -Target Rebuild -Verbosity minimal -Version $Version
if ($LASTEXITCODE -ne 0) { throw "界面编译失败" }
$ui = Join-Path $here "LemonPaperplane\bin\Release"

$archs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$iscc = Find-ISCC
foreach ($a in $archs) {
    $goarch = if ($a -eq "x64") { "amd64" } else { "arm64" }
    $rt = Join-Path $RuntimeRoot $goarch
    if (-not (Test-Path "$rt\paperplane.exe")) { throw "没有 $a 的内核：$rt\paperplane.exe" }
    $stage = Join-Path $dist "stage-$a\Lemon Paperplane"
    Write-Host "==> 整理 $a → $stage"
    Remove-Item -Recurse -Force (Split-Path $stage) -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($f in @("LemonPaperplane.exe", "LemonPaperplane.exe.config", "PaperplaneKit.dll", "Newtonsoft.Json.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll")) {
        Copy-Item (Join-Path $ui $f) $stage
    }
    # WebView2 加载器：按进程架构加载。ARM64 机器上装着 .NET Framework 4.8（不是 4.8.1）时界面以 x64 仿真运行，两份都带上
    $loaders = if ($a -eq "x64") { @("win-x64") } else { @("win-arm64", "win-x64") }
    foreach ($l in $loaders) {
        New-Item -ItemType Directory -Force "$stage\runtimes\$l\native" | Out-Null
        Copy-Item "$ui\runtimes\$l\native\WebView2Loader.dll" "$stage\runtimes\$l\native\"
    }
    Copy-Item "$rt\paperplane.exe", "$rt\paperplane-plugins.exe" $stage
    Copy-Item -Recurse "$rt\plugins" "$stage\plugins"
    if (Test-Path "$rt\store") { Copy-Item -Recurse "$rt\store" "$stage\store" }
    if ($EditorDir -and (Test-Path "$EditorDir\index.html")) { Copy-Item -Recurse $EditorDir "$stage\editor" }
    else { Write-Warning "没有编辑器网页（-EditorDir），编辑窗口和终端不可用" }
    Copy-Item (Join-Path $here "THIRD-PARTY-NOTICES.txt") $stage

    $zip = Join-Path $dist "LemonPaperplane-$Version-windows-$a.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    # 解压后是一个「Lemon Paperplane」文件夹
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("    zip {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

    if ($iscc) {
        & $iscc /Q "/DAppVersion=$Version" "/DArch=$a" "/DSourceDir=$stage" "/O$dist" (Join-Path $here "installer\LemonPaperplane.iss")
        if ($LASTEXITCODE -ne 0) { throw "安装包生成失败（$a）" }
        $setup = Join-Path $dist "LemonPaperplane-$Version-windows-$a-setup.exe"
        Write-Host ("    setup {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
    } else {
        Write-Warning "没有找到 Inno Setup（ISCC.exe），只出 zip"
    }
}
Write-Host "完成：$dist"
