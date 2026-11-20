<#
柠檬纸飞机 Windows 一键构建：在任何一台 Windows 10/11 上（包括全新的）执行，缺什么自动准备，产出安装包和免安装 zip。

  build.cmd [-Version 0.1.0] [-Arch all|x64|arm64]
  build.cmd -LinuxHeadless        改为构建 Linux 无头版（单个可执行文件，10 个架构）→ dist\linux-headless\
  （等同 powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 …）

产物在 dist\windows\：
  LemonPaperplane-<版本>-windows-x64-setup.exe     安装包（装到当前用户，不需要管理员权限）
  LemonPaperplane-<版本>-windows-x64.zip           免安装版
  LemonPaperplane-<版本>-windows-arm64-setup.exe / -arm64.zip
版本默认取 Xcode 工程的 MARKETING_VERSION（和 Mac 版一致）。

自动准备的依赖（都放在工具缓存目录，不需要管理员权限，不改系统设置，不需要 Visual Studio）：
  Go、Node.js          版本不够时下载（自动挑最快的下载源，含国内镜像）
  C# 编译器            Roslyn（NuGet: Microsoft.Net.Compilers.Toolset），配合 Windows 自带的 MSBuild
  .NET Framework 4.8 参考程序集（NuGet: Microsoft.NETFramework.ReferenceAssemblies.net48）
  Inno Setup 6         便携安装到工具缓存目录
  插件源码             旁边没有 lemon-paperplane-plugin-* 时从 GitHub 下载
环境变量：
  PAPERPLANE_BUILD_CACHE   工具缓存目录（默认 %LOCALAPPDATA%\LemonPaperplane\build-tools）
  GOPROXY / NPM_REGISTRY / PAPERPLANE_NUGET   指定后不再自动挑下载源
#>
param(
    [string]$Version = "",
    [ValidateSet("all", "x64", "arm64")][string]$Arch = "all",
    [switch]$LinuxHeadless,
    # 测试用：不用系统里已装的 Go / Node / Inno Setup，模拟全新机器
    [switch]$IgnoreSystemTools
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$Main = Split-Path -Parent $MyInvocation.MyCommand.Path
$Cache = if ($env:PAPERPLANE_BUILD_CACHE) { $env:PAPERPLANE_BUILD_CACHE } else { Join-Path $env:LOCALAPPDATA "LemonPaperplane\build-tools" }
$Work = Join-Path $Main "dist\.work-windows"
$Dist = Join-Path $Main "dist\windows"
$Win = Join-Path $Main "lemon-paperplane-main-windows"
$HostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64" -or $env:PROCESSOR_ARCHITEW6432 -eq "ARM64") { "arm64" } else { "amd64" }

# 固定版本的下载（带 SHA-256，从哪个镜像下都能校验）
$InnoVersion = "6.7.4"
$InnoSha = "a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c"
$RefsVersion = "1.0.3"
$RefsSha = "8a7e348538e7eb91351696911689f49e3d4f63f8bab517432bbe159b8b1104a2"
$RoslynVersion = "5.12.0"
$RoslynSha = "1a498048ddc7bd5fdc2883e7c77f733a3727693e88518851cf8a8ef93cb5fa8d"

function Say($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m) { Write-Host "    $m" }
function Die($m) { Write-Host ""; Write-Host "错误：$m" -ForegroundColor Red; exit 1 }

# 运行原生程序，失败就停（用 $args 而不是 param：参数里的 -C、-main 原样传给程序）
function Run {
    $exe = $args[0]
    $rest = @($args | Select-Object -Skip 1)
    & $exe @rest
    if ($LASTEXITCODE -ne 0) { Die "$exe $($rest -join ' ') 失败（退出码 $LASTEXITCODE）" }
}

# 运行原生程序取输出，不报错（Windows PowerShell 5.1 里重定向 stderr 会被当成错误）
function Try-Output {
    $exe = $args[0]
    $rest = @($args | Select-Object -Skip 1)
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $text = ""
    try {
        $out = & $exe @rest 2>$null
        if ($LASTEXITCODE -eq 0) { $text = (@($out) -join "`n").Trim() }
    } catch { } finally { $ErrorActionPreference = $old }
    return $text
}

function Parse-Version($s) {
    $m = [regex]::Match("$s", '(\d+)(?:\.(\d+))?(?:\.(\d+))?')
    if (-not $m.Success) { return [version]"0.0" }
    $p = @($m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value) | ForEach-Object { if ($_) { $_ } else { "0" } }
    return [version]("{0}.{1}.{2}" -f $p[0], $p[1], $p[2])
}

function Add-Path($dir) { $env:Path = "$dir;$env:Path" }

# ---------------------------------------------------------------- 下载
$Curl = (Get-Command curl.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
# 通用参数：HTTP/1.1（有的代理会掐断 HTTP/2 长下载）；不查证书吊销（schannel 在连不上吊销服务器的网络里会直接拒绝连接，
# 证书本身照常校验，下载的文件另有 SHA-256 校验）
$CurlCommon = @("--http1.1")
$CurlRetryAll = $false
if ($Curl) {
    $help = Try-Output $Curl --help all
    $CurlRetryAll = $help -match "retry-all-errors"
    if ($help -match "ssl-no-revoke") { $CurlCommon += "--ssl-no-revoke" }
}
$Quiet = $true
try { $Quiet = [Console]::IsErrorRedirected } catch { }

function Fetch($url, $out) {
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    $part = "$out.part"
    if ($Curl) {
        $a = $CurlCommon + @("-fL", "--connect-timeout", "15", "--retry", "6", "--retry-delay", "3", "-C", "-", "-o", $part)
        if ($CurlRetryAll) { $a += "--retry-all-errors" }
        if ($Quiet) { $a += "-sS" } else { $a += "--progress-bar" }
        $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
        & $Curl @a $url
        $ok = $LASTEXITCODE -eq 0
        $ErrorActionPreference = $old
    } else {
        try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $part; $ok = $true } catch { $ok = $false }
    }
    if (-not $ok) { Remove-Item $part -ErrorAction SilentlyContinue; return $false }
    Move-Item -Force $part $out
    return $true
}

function Fetch-Json($url) {
    $tmp = Join-Path $Cache ("tmp-" + [guid]::NewGuid().ToString("N") + ".json")
    if (-not (Fetch $url $tmp)) { return $null }
    try { return (Get-Content -Raw -Encoding UTF8 $tmp | ConvertFrom-Json) } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

# 不依赖 Get-FileHash（从 PowerShell 7 里调用 5.1 时模块路径错乱会找不到它）
function Sha256($p) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $p).ProviderPath)
    try { ([System.BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() } finally { $fs.Dispose(); $sha.Dispose() }
}

# 从候选地址里挑下载最快的：并行各下 512 KB，返回最快的下标
function Pick-Fastest($probe, [string[]]$bases) {
    if (-not $Curl -or $bases.Count -le 1) { return 0 }
    $dir = Join-Path $Cache "probe-$PID"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $procs = @()
    for ($i = 0; $i -lt $bases.Count; $i++) {
        $procs += Start-Process -FilePath $Curl -NoNewWindow -PassThru -RedirectStandardOutput "$dir\$i" `
            -ArgumentList ($CurlCommon + @("-s", "-o", "NUL", "-r", "0-524287", "-m", "8", "-L", "-w", "%{http_code}:%{speed_download}", "$($bases[$i])$probe"))
    }
    foreach ($p in $procs) { $p.WaitForExit() }
    $best = 0; $bestSpeed = -1
    for ($i = 0; $i -lt $bases.Count; $i++) {
        $r = (Get-Content -Raw "$dir\$i" -ErrorAction SilentlyContinue)
        if (-not $r) { continue }
        $code, $speed = $r.Trim().Split(":")
        if ($code -ne "200" -and $code -ne "206") { continue }
        $s = [double]::Parse($speed, [Globalization.CultureInfo]::InvariantCulture)
        if ($s -gt $bestSpeed) { $best = $i; $bestSpeed = $s }
    }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    return $best
}

# 按快慢顺序依次尝试各个源，下载并校验
function Fetch-Verified($rel, [string[]]$bases, $out, $sha) {
    if ((Test-Path $out) -and (Sha256 $out) -eq $sha) { return }
    $first = Pick-Fastest $rel $bases
    $order = @($bases[$first]) + @($bases | Where-Object { $_ -ne $bases[$first] })
    foreach ($b in $order) {
        Info "下载 $b$rel"
        if ((Fetch "$b$rel" $out) -and (Sha256 $out) -eq $sha) { return }
        Remove-Item $out -ErrorAction SilentlyContinue
    }
    Die "下载失败：$rel"
}

function Expand-To($archive, $dest) {
    $tmp = "$dest.tmp"
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $tar = Join-Path $env:WINDIR "System32\tar.exe"
    if (Test-Path $tar) {
        & $tar -xf $archive -C $tmp
        if ($LASTEXITCODE -ne 0) { Die "解压失败：$archive" }
    } else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $tmp)
    }
    Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
    Move-Item $tmp $dest
}

# ---------------------------------------------------------------- Go
function Ensure-Go {
    Say "检查 Go"
    $need = Parse-Version ((Select-String -Path "$Main\lemon-paperplane-main-core\go.mod" -Pattern '^go (\S+)').Matches[0].Groups[1].Value)
    $cands = @()
    $cands += Get-ChildItem "$Cache\go-*\go\bin\go.exe" -ErrorAction SilentlyContinue | Sort-Object { Parse-Version $_.Directory.Parent.Parent.Name } -Descending | ForEach-Object { $_.FullName }
    if (-not $IgnoreSystemTools) {
        $cands += (Get-Command go.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
        $cands += "$env:ProgramFiles\Go\bin\go.exe"
    }
    foreach ($c in $cands) {
        if (-not $c -or -not (Test-Path $c)) { continue }
        $v = Try-Output $c env GOVERSION
        if ($v -and (Parse-Version $v) -ge $need) { Add-Path (Split-Path $c); Info "$v（$c）"; return }
    }
    $rels = Fetch-Json "https://golang.google.cn/dl/?mode=json"
    if (-not $rels) { $rels = Fetch-Json "https://go.dev/dl/?mode=json" }
    if (-not $rels) { Die "取不到 Go 的版本列表" }
    $rel = $rels[0]
    $f = $rel.files | Where-Object { $_.os -eq "windows" -and $_.arch -eq $HostArch -and $_.kind -eq "archive" } | Select-Object -First 1
    $zip = Join-Path $Cache $f.filename
    Fetch-Verified $f.filename @("https://dl.google.com/go/", "https://mirrors.nju.edu.cn/golang/", "https://mirrors.ustc.edu.cn/golang/", "https://mirrors.aliyun.com/golang/") $zip $f.sha256
    $dest = Join-Path $Cache ("go-" + $rel.version.TrimStart("go"))
    Expand-To $zip $dest
    Remove-Item $zip
    Add-Path "$dest\go\bin"
    Info "$(Try-Output go env GOVERSION)（$dest）"
}

# 没配置过 Go 模块代理时，在官方代理和 goproxy.cn 之间挑快的
function Ensure-GoProxy {
    $env:GOTOOLCHAIN = "local"
    if ($env:GOPROXY -or (Try-Output go env GOPROXY) -ne "https://proxy.golang.org,direct") { return }
    if ((Pick-Fastest "golang.org/x/sys/@v/v0.29.0.zip" @("https://proxy.golang.org/", "https://goproxy.cn/")) -eq 1) {
        $env:GOPROXY = "https://goproxy.cn,direct"
        $env:GOSUMDB = "sum.golang.google.cn"
        Info "Go 模块代理：goproxy.cn"
    }
}

# ---------------------------------------------------------------- Node.js
function Ensure-Node {
    Say "检查 Node.js"
    $cands = @()
    $cands += Get-ChildItem "$Cache\node-*\node.exe" -ErrorAction SilentlyContinue | Sort-Object { Parse-Version $_.Directory.Name } -Descending | ForEach-Object { $_.FullName }
    if (-not $IgnoreSystemTools) {
        $cands += (Get-Command node.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
        $cands += "$env:ProgramFiles\nodejs\node.exe"
    }
    foreach ($c in $cands) {
        if (-not $c -or -not (Test-Path $c)) { continue }
        $v = Try-Output $c --version
        if ($v -and (Parse-Version $v) -ge [version]"18.0.0" -and (Test-Path (Join-Path (Split-Path $c) "npm.cmd"))) {
            Add-Path (Split-Path $c); Info "node $v（$c）"; return
        }
    }
    $bases = @("https://nodejs.org/dist/", "https://registry.npmmirror.com/-/binary/node/")
    $order = @($bases[(Pick-Fastest "index.json" $bases)]) + $bases
    $idx = $null
    foreach ($b in $order) { $idx = Fetch-Json "${b}index.json"; if ($idx) { $base = $b; break } }
    if (-not $idx) { Die "取不到 Node.js 的版本列表" }
    $ver = ($idx | Where-Object { $_.lts } | Select-Object -First 1).version
    $a = if ($HostArch -eq "arm64") { "arm64" } else { "x64" }
    $name = "node-$ver-win-$a"
    $sums = Join-Path $Cache "node-$ver-SHASUMS256.txt"
    if (-not (Fetch "$base$ver/SHASUMS256.txt" $sums)) { Die "下载 Node.js 校验文件失败" }
    $sha = ((Get-Content $sums) | Where-Object { $_ -match "\s$name\.zip$" } | ForEach-Object { ($_ -split "\s+")[0] }) | Select-Object -First 1
    Remove-Item $sums
    $zip = Join-Path $Cache "$name.zip"
    Fetch-Verified "$ver/$name.zip" @($base) $zip $sha
    $tmp = Join-Path $Cache "node-unpack"
    Expand-To $zip $tmp
    $dest = Join-Path $Cache ("node-" + $ver.TrimStart("v"))
    Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
    Move-Item (Join-Path $tmp $name) $dest
    Remove-Item -Recurse -Force $tmp, $zip
    Add-Path $dest
    Info "node $(Try-Output "$dest\node.exe" --version)（$dest）"
}

function Ensure-NpmRegistry {
    if (-not $env:NPM_REGISTRY) {
        $regs = @("https://registry.npmjs.org/", "https://registry.npmmirror.com/")
        $env:NPM_REGISTRY = $regs[(Pick-Fastest "esbuild/-/esbuild-0.28.2.tgz" $regs)]
    }
    Info "npm 源：$env:NPM_REGISTRY"
}

# ---------------------------------------------------------------- C# 编译器、参考程序集
function Ensure-NugetPackage($id, $ver, $sha) {
    $dir = Join-Path $Cache "nuget\$($id.ToLowerInvariant()).$ver"
    if (Test-Path "$dir\.ok") { return $dir }
    $bases = if ($env:PAPERPLANE_NUGET) { @($env:PAPERPLANE_NUGET.TrimEnd("/") + "/") } else {
        @("https://api.nuget.org/v3-flatcontainer/", "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote/")
    }
    $lid = $id.ToLowerInvariant()
    $pkg = Join-Path $Cache "$lid.$ver.zip"
    Fetch-Verified "$lid/$ver/$lid.$ver.nupkg" $bases $pkg $sha
    Expand-To $pkg $dir
    Remove-Item $pkg
    Set-Content "$dir\.ok" $ver
    return $dir
}

function Ensure-CSharp {
    Say "检查 C# 编译环境"
    $fw = if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe") { "Framework64" } else { "Framework" }
    $msbuild = "$env:WINDIR\Microsoft.NET\$fw\v4.0.30319\MSBuild.exe"
    if (-not (Test-Path $msbuild)) { Die "没有 .NET Framework 4.x（$msbuild），Windows 10 1903 起自带，请先装 .NET Framework 4.8" }
    $release = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction SilentlyContinue).Release
    if ($release -lt 528040) { Die ".NET Framework 版本低于 4.8，请先安装 .NET Framework 4.8" }
    $roslyn = Ensure-NugetPackage "Microsoft.Net.Compilers.Toolset" $RoslynVersion $RoslynSha
    $refs = Ensure-NugetPackage "Microsoft.NETFramework.ReferenceAssemblies.net48" $RefsVersion $RefsSha
    $env:PAPERPLANE_MSBUILD = $msbuild
    $env:PAPERPLANE_CSC_DIR = Join-Path $roslyn "tasks\net472"
    $env:PAPERPLANE_REFASM = Join-Path $refs "build"
    if (-not (Test-Path "$env:PAPERPLANE_CSC_DIR\csc.exe")) { Die "Roslyn 包里没有 csc.exe" }
    if (-not (Test-Path "$env:PAPERPLANE_REFASM\.NETFramework\v4.8\mscorlib.dll")) { Die "参考程序集包不完整" }
    Info "MSBuild：$msbuild"
    Info "C# 编译器：Roslyn $RoslynVersion"
}

# ---------------------------------------------------------------- Inno Setup
function Ensure-Inno {
    Say "检查 Inno Setup"
    $dir = Join-Path $Cache "innosetup-$InnoVersion"
    $cands = @("$dir\ISCC.exe")
    if (-not $IgnoreSystemTools) {
        $cands += @($env:PAPERPLANE_ISCC, "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    }
    foreach ($c in $cands) {
        if ($c -and (Test-Path $c)) { $env:PAPERPLANE_ISCC = $c; Info $c; return }
    }
    $exe = Join-Path $Cache "innosetup-$InnoVersion.exe"
    $tag = "is-" + $InnoVersion.Replace(".", "_")
    Fetch-Verified "$tag/innosetup-$InnoVersion.exe" @("https://github.com/jrsoftware/issrc/releases/download/") $exe $InnoSha
    # 便携安装到缓存目录：当前用户、不登记卸载、不建快捷方式
    $p = Start-Process -FilePath $exe -Wait -PassThru -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/PORTABLE=1", "/NOICONS", "/DIR=`"$dir`"")
    if ($p.ExitCode -ne 0 -or -not (Test-Path "$dir\ISCC.exe")) { Die "Inno Setup 安装失败（退出码 $($p.ExitCode)）" }
    Remove-Item $exe
    $env:PAPERPLANE_ISCC = "$dir\ISCC.exe"
    Info $env:PAPERPLANE_ISCC
}

# ---------------------------------------------------------------- 编辑器网页
function Build-Editor {
    Say "构建编辑器网页"
    $ed = Join-Path $Main "lemon-paperplane-main-editor"
    # node_modules 是别的系统装的（比如从 Mac 拷过来）就重装：esbuild 带的是本机平台的程序
    $plat = Try-Output node -p "process.platform + '-' + process.arch"
    Push-Location $ed
    try {
        if (-not (Test-Path "node_modules\@esbuild\$plat")) {
            Remove-Item -Recurse -Force node_modules -ErrorAction SilentlyContinue
            Run npm.cmd ci "--registry=$env:NPM_REGISTRY" --no-audit --no-fund
        }
        Run npm.cmd run build --silent
    } finally { Pop-Location }
    if (-not (Test-Path "$ed\dist\index.html")) { Die "没有生成 lemon-paperplane-main-editor\dist\index.html" }
}

# ---------------------------------------------------------------- 主流程
if (-not $Version) {
    $m = Select-String -Path "$Main\lemon-paperplane-main-macos\LemonPaperplane.xcodeproj\project.pbxproj" -Pattern 'MARKETING_VERSION = ([^;]+);' | Select-Object -First 1
    $Version = if ($m) { $m.Matches[0].Groups[1].Value.Trim('"') } else { "0.1.0" }
}
New-Item -ItemType Directory -Force $Cache | Out-Null
Say "柠檬纸飞机 $Version · Windows 构建（工具缓存：$Cache）"

# 从别的电脑拷来 / 从网上下载的文件带「来自网络」标记，清掉（脚本、程序集）
$srcDirs = @($Main) + @(Get-ChildItem -Directory (Split-Path $Main) -Filter "lemon-paperplane-plugin-*" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Get-ChildItem -Path $srcDirs -Recurse -File -Include *.ps1, *.dll, *.exe, *.cmd -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(node_modules|dist|bin|obj)\\' } | Unblock-File -ErrorAction SilentlyContinue

Ensure-Go
Ensure-GoProxy
if ($LinuxHeadless) {
    Say "准备插件源码"
    Run go -C "$Main\scripts\ppbuild" run . fetch-plugins -main $Main
    Say "编译 Linux 无头版（内核 + 内置插件，每个架构一个文件）"
    Remove-Item -Recurse -Force "$Main\dist\linux-headless" -ErrorAction SilentlyContinue
    Run go -C "$Main\scripts\ppbuild" run . headless -main $Main -out "$Main\dist\linux-headless" -version $Version
    Say "完成"
    Info "$Main\dist\linux-headless\"
    exit 0
}
Ensure-Node
Ensure-NpmRegistry
Ensure-CSharp
Ensure-Inno

Say "准备插件源码"
Run go -C "$Main\scripts\ppbuild" run . fetch-plugins -main $Main

Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null
Build-Editor

$archs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
Say "内核与内置插件（$($archs -join '、')）"
foreach ($a in $archs) {
    $goarch = if ($a -eq "x64") { "amd64" } else { "arm64" }
    Run go -C "$Main\scripts\ppbuild" run . runtime -main $Main -out "$Work\runtime\$goarch" -os windows -arch $goarch -version $Version
}

Say "界面、免安装 zip 与安装包"
& (Join-Path $Win "package.ps1") -Version $Version -RuntimeRoot "$Work\runtime" -EditorDir "$Main\lemon-paperplane-main-editor\dist" -Arch $Arch -ISCC $env:PAPERPLANE_ISCC
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { Die "打包失败" }

New-Item -ItemType Directory -Force $Dist | Out-Null
$outs = @()
foreach ($a in $archs) {
    foreach ($f in @("LemonPaperplane-$Version-windows-$a-setup.exe", "LemonPaperplane-$Version-windows-$a.zip")) {
        $src = Join-Path $Win "dist\$f"
        if (-not (Test-Path $src)) { Die "没有生成 $f" }
        Move-Item -Force $src (Join-Path $Dist $f)
        $outs += Join-Path $Dist $f
    }
}
Remove-Item -Recurse -Force (Join-Path $Win "dist"), $Work -ErrorAction SilentlyContinue

Say "完成"
foreach ($o in $outs) { Info ("{0,8:N1} MB  {1}" -f ((Get-Item $o).Length / 1MB), $o) }
exit 0
