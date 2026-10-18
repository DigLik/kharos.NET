# Ставит WASI SDK для сборки модов NativeAOT-LLVM (docs/dotnet.md, Р5) в dotnet/.cache/wasi-sdk и печатает путь.
# Версия — та, что ждёт Microsoft.DotNet.ILCompiler.LLVM (_ExpectedWasiSdkVersions в его Wasm.targets).
# Использование: pwsh dotnet/tools/wasi-sdk.ps1 [-Version 29]
param(
    [string]$Version = '29'
)
$ErrorActionPreference = 'Stop'

$cache = Join-Path (Split-Path $PSScriptRoot -Parent) '.cache/wasi-sdk'
$arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'x86_64' }
    'Arm64' { 'arm64' }
    default { throw "WASI SDK: архитектура $_ не поддерживается" }
}
$os = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
$name = "wasi-sdk-$Version.0-$arch-$os"
$target = Join-Path $cache $name

if (-not (Test-Path (Join-Path $target 'VERSION'))) {
    New-Item -ItemType Directory -Force $cache | Out-Null
    $archive = Join-Path $cache "$name.tar.gz"
    $url = "https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-$Version/$name.tar.gz"
    Write-Host "WASI SDK: качаю $url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $archive
    tar -xzf $archive -C $cache
    if ($LASTEXITCODE -ne 0) { throw "WASI SDK: tar завершился с кодом $LASTEXITCODE" }
    Remove-Item $archive
}

(Resolve-Path $target).Path
