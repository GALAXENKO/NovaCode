<#
    Сборка NovaCode — агент в стиле OpenCode для Windows XP.

    Компилирует C# 5 строго против reference assemblies .NET Framework 4.0,
    поэтому итоговый EXE не сможет случайно использовать API из 4.5+ и
    гарантированно запускается на Windows XP SP3 с установленным .NET 4.0.

    Пример:
        powershell -ExecutionPolicy Bypass -File build.ps1
        powershell -ExecutionPolicy Bypass -File build.ps1 -SelfTest
        powershell -ExecutionPolicy Bypass -File build.ps1 -Run
        powershell -ExecutionPolicy Bypass -File build.ps1 -Package
#>
param(
    [switch]$Run,
    [switch]$SelfTest,
    [switch]$Package,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = Join-Path $here "bin" }

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- reference assemblies

function Get-RefAssemblies {
    $candidates = @(
        (Join-Path $here "tools\net40ref"),
        (Join-Path $env:TEMP "NovaCode\net40ref")
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "mscorlib.dll")) { return $c }
    }

    $target = Join-Path $env:TEMP "NovaCode\net40ref"
    Write-Step "Скачиваю reference assemblies .NET 4.0 (один раз)"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $pkg = Join-Path $env:TEMP "NovaCode\net40.nupkg"
    $zip = Join-Path $env:TEMP "NovaCode\net40.zip"
    New-Item -ItemType Directory -Force -Path (Split-Path $pkg) | Out-Null
    $url = "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net40/1.0.3"
    try {
        if (-not (Test-Path $pkg)) {
            Invoke-WebRequest -Uri $url -OutFile $pkg -UseBasicParsing -TimeoutSec 180
        }
        Copy-Item $pkg $zip -Force
        Expand-Archive -Path $zip -DestinationPath (Join-Path $env:TEMP "NovaCode\pkg") -Force
        $src = Join-Path $env:TEMP "NovaCode\pkg\build\.NETFramework\v4.0"
        Get-ChildItem $src -Filter *.dll | Copy-Item -Destination $target -Force
        return $target
    }
    catch {
        Write-Warning "Не удалось скачать reference assemblies: $($_.Exception.Message)"
        Write-Warning "Собираю против системных сборок — проверьте, что не используются API .NET 4.5+."
        return $null
    }
}

# ---------------------------------------------------------------- компилятор

function Get-Csc {
    $paths = @(
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $p } }
    throw "csc.exe (.NET Framework 4.x) не найден. Установите .NET Framework 4.x."
}

$csc = Get-Csc
$ref = Get-RefAssemblies

Write-Step "Сборка NovaCode"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sources = Get-ChildItem -Path $here -Recurse -Filter *.cs |
           Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist)[\\/]' } |
           Sort-Object FullName |
           ForEach-Object { $_.FullName }

if (-not $sources) { throw "Не найдено ни одного .cs файла" }

$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.Add("/nologo")
$cscArgs.Add("/noconfig")
$cscArgs.Add("/target:winexe")
$cscArgs.Add("/platform:x86")
$cscArgs.Add("/langversion:5")
$cscArgs.Add("/optimize+")
$cscArgs.Add("/debug:pdbonly")
$cscArgs.Add("/warn:4")
$cscArgs.Add("/out:$OutDir\NovaCode.exe")
$cscArgs.Add("/win32manifest:$here\app.manifest")

$icon = Join-Path $here "Assets\novacode.ico"
if (Test-Path $icon) { $cscArgs.Add("/win32icon:$icon") }

if ($ref) {
    $cscArgs.Add("/nostdlib+")
    $needed = @(
        "mscorlib.dll", "System.dll", "System.Core.dll",
        "System.Drawing.dll", "System.Windows.Forms.dll",
        "System.Xml.dll", "System.Configuration.dll"
    )
    foreach ($asm in $needed) {
        $path = Join-Path $ref $asm
        if (-not (Test-Path $path)) { throw "В reference assemblies нет $asm" }
        $cscArgs.Add("/reference:$path")
    }
}

foreach ($source in $sources) { $cscArgs.Add($source) }

Write-Host "csc $($cscArgs -join ' ')" -ForegroundColor DarkGray

$savedPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$output = & $csc $cscArgs 2>&1
$exitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference

$warnings = 0
foreach ($line in $output) {
    $text = "$line"
    if ($text -match "warning CS") { $warnings++ }
    Write-Host $text
}

if ($exitCode -ne 0) { throw "Компиляция не удалась (код $exitCode)" }

Copy-Item (Join-Path $here "app.config") (Join-Path $OutDir "NovaCode.exe.config") -Force -ErrorAction SilentlyContinue

$exe = Join-Path $OutDir "NovaCode.exe"
$info = Get-Item $exe
Write-Step ("Готово: {0} ({1:N0} КБ, предупреждений: {2})" -f $info.FullName, ($info.Length / 1KB), $warnings)

if ($Package) {
    Write-Step "Собираю пакет для Windows XP"
    $dist = Join-Path $here "dist"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "NovaCode-xp.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    $stage = Join-Path $env:TEMP ("NovaCode\stage-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $exe (Join-Path $stage "NovaCode.exe")
    $cfg = Join-Path $OutDir "NovaCode.exe.config"
    if (Test-Path $cfg) { Copy-Item $cfg (Join-Path $stage "NovaCode.exe.config") }

    # Блокнот XP не понимает UTF-8 без BOM — перекодируем, чтобы README открывался двойным щелчком.
    $readme = Join-Path $here "README.md"
    if (Test-Path $readme) {
        $text = [System.IO.File]::ReadAllText($readme, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText((Join-Path $stage "README.txt"), $text,
            (New-Object System.Text.UTF8Encoding($true)))
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Remove-Item $stage -Recurse -Force
    Write-Step ("Пакет: {0} ({1:N0} КБ)" -f $zip, ((Get-Item $zip).Length / 1KB))
}

if ($SelfTest) {
    & $exe --selftest
    exit $LASTEXITCODE
}
if ($Run) {
    Start-Process $exe
}
