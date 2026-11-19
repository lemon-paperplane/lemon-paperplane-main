# 编译 Windows 界面（PowerShell 5.1+）。
#   .\build.ps1                 Debug 编译全部工程
#   .\build.ps1 -Release        Release 编译
#   .\build.ps1 -Test           编译后跑 PaperplaneKit.Tests
#   .\build.ps1 -Test -Filter Ansi
#   .\build.ps1 -Release -Version 1.2.3    带版本号（package.ps1 用）
#
# 需要：Visual Studio 2022+ 或 Build Tools（含 MSBuild），或挂载好的 EWDK。
# 机器上没有 .NET Framework 4.8 目标包时，自动下载参考程序集（NuGet: Microsoft.NETFramework.ReferenceAssemblies.net48）。
# 什么都没装的机器用仓库根目录的 build.ps1：它准备好 Roslyn 后设置下面的环境变量再调用本脚本。
#   PAPERPLANE_MSBUILD   MSBuild.exe（可以是 Windows 自带的 Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe）
#   PAPERPLANE_CSC_DIR   Roslyn 的 csc.exe 所在目录（NuGet: Microsoft.Net.Compilers.Toolset 的 tasks\net472）
#   PAPERPLANE_REFASM    参考程序集目录（含 .NETFramework\v4.8）
param(
    [switch]$Release,
    [switch]$Test,
    [string]$Filter = "",
    [string]$Target = "Build",
    [string]$Verbosity = "minimal",
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$config = if ($Release) { "Release" } else { "Debug" }

function Find-MSBuild {
    if ($env:PAPERPLANE_MSBUILD -and (Test-Path $env:PAPERPLANE_MSBUILD)) { return $env:PAPERPLANE_MSBUILD }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" | Select-Object -First 1
        if ($p) { return $p }
    }
    # 挂载的 EWDK（Enterprise WDK 自带 Build Tools）
    foreach ($d in (Get-PSDrive -PSProvider FileSystem).Root) {
        $hit = Get-ChildItem -ErrorAction SilentlyContinue "$($d)Program Files\Microsoft Visual Studio\*\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "找不到 MSBuild：装 Visual Studio Build Tools，或设置 PAPERPLANE_MSBUILD"
}

function Find-RefAssemblies {
    $sys = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
    if (Test-Path "$sys\mscorlib.dll") { return "" }
    $cands = @()
    if ($env:PAPERPLANE_REFASM) { $cands += $env:PAPERPLANE_REFASM }
    $cache = Join-Path $env:LOCALAPPDATA "LemonPaperplaneBuild\net48refs"
    $cands += @("$cache\build")
    foreach ($c in $cands) {
        if (Test-Path "$c\.NETFramework\v4.8\mscorlib.dll") { return (Resolve-Path $c).Path + "\" }
    }
    # 退一步：4.8.1 目标包（VS / EWDK 自带）。程序的 App.config 仍声明 4.8，装 4.8 的 Windows 10 照常运行
    foreach ($d in @("${env:ProgramFiles(x86)}\") + (Get-PSDrive -PSProvider FileSystem).Root + ((Get-PSDrive -PSProvider FileSystem).Root | ForEach-Object { "$($_)Program Files\" })) {
        $root = Join-Path $d "Reference Assemblies\Microsoft\Framework\"
        if (Test-Path "$root.NETFramework\v4.8.1\mscorlib.dll") {
            $script:frameworkVersion = "v4.8.1"
            Write-Host "没有 4.8 参考程序集，改用 4.8.1：$root"
            return $root
        }
    }
    Write-Host "下载 .NET Framework 4.8 参考程序集…"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = "$cache\refs.zip"
    curl.exe -sSL -o $zip "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
    Expand-Archive -Force $zip $cache
    Remove-Item $zip
    return "$cache\build\"
}

$script:frameworkVersion = ""
$msbuild = Find-MSBuild
$refs = Find-RefAssemblies
$props = @("/p:Configuration=$config", "/m", "/nologo", "/v:$Verbosity", "/t:$Target", "/clp:ErrorsOnly;Summary")
if ($env:PAPERPLANE_CSC_DIR) {
    if (-not (Test-Path (Join-Path $env:PAPERPLANE_CSC_DIR "csc.exe"))) { throw "PAPERPLANE_CSC_DIR 下没有 csc.exe：$env:PAPERPLANE_CSC_DIR" }
    $props += "/p:CscToolPath=$($env:PAPERPLANE_CSC_DIR.TrimEnd('\'))", "/p:CscToolExe=csc.exe"
}
# Windows 自带的 MSBuild 4.x：不认识 ToolsVersion="Current"，也不一定能读新版 .sln，直接按 4.0 编译各工程
$legacy = $msbuild -like "*\Microsoft.NET\Framework*\v4.0.30319\*"
if ($legacy) {
    if (-not $env:PAPERPLANE_CSC_DIR) { throw "Windows 自带的 MSBuild 只带 C# 5 编译器：用仓库根目录的 build.ps1（会准备新版 Roslyn），或装 Visual Studio Build Tools" }
    $props += "/tv:4.0"
}
# 路径末尾的反斜杠会在传给 MSBuild 时吃掉引号，去掉后由 Common.props 补上
if ($refs) { $props += "/p:PaperplaneRefAssemblies=$($refs.TrimEnd('\'))" }
if ($script:frameworkVersion) { $props += "/p:TargetFrameworkVersion=$($script:frameworkVersion)" }
if ($Version) { $props += "/p:PaperplaneVersion=$Version" }
Write-Host "MSBuild: $msbuild"
if ($legacy) {
    $projects = @("LemonPaperplane\LemonPaperplane.csproj")
    if ($Test) { $projects += "PaperplaneKit.Tests\PaperplaneKit.Tests.csproj" }
    foreach ($p in $projects) {
        & $msbuild (Join-Path $here $p) @props
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
} else {
    & $msbuild (Join-Path $here "LemonPaperplane.sln") @props
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Test) {
    $exe = Join-Path $here "PaperplaneKit.Tests\bin\$config\PaperplaneKit.Tests.exe"
    if ($Filter) { & $exe $Filter } else { & $exe }
    exit $LASTEXITCODE
}
