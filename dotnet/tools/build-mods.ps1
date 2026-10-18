# Собирает моды dotnet/mods/*/ в wasm (docs/dotnet.md, Р5): каждый проект *.Host → host.wasm, *.Client → client.wasm
# рядом с mod.json. Компилятор — NativeAOT-LLVM (фид dotnet-experimental), WASI SDK — tools/wasi-sdk.ps1.
# Использование: pwsh dotnet/tools/build-mods.ps1 [-Mod hello] [-Configuration Release]
param(
    [string]$Mod = '*',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $env:WASI_SDK_PATH) {
    $env:WASI_SDK_PATH = (& (Join-Path $PSScriptRoot 'wasi-sdk.ps1') | Select-Object -Last 1)
}

$projects = Get-ChildItem (Join-Path $root "mods/$Mod/*/*.csproj") |
    Where-Object { $_.BaseName -match '\.(Host|Client)$' }
if (-not $projects) { throw "Моды: нет проектов mods/$Mod/*/*.Host|Client.csproj" }

foreach ($project in $projects) {
    $started = Get-Date
    # MSB4011 (двойной импорт целей ILCompiler: пакетом LLVM и SDK) — безвреден, не показываем.
    dotnet publish $project.FullName -c $Configuration -p:KharosWasm=true -nologo -v quiet |
        Where-Object { $_ -notmatch 'MSB4011' } | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Моды: $($project.Name) не собрался" }
    $name = if ($project.BaseName -match '\.Client$') { 'client.wasm' } else { 'host.wasm' }
    $wasm = Join-Path $project.Directory.Parent.FullName $name
    $size = (Get-Item $wasm).Length
    Write-Host ("{0} → {1} ({2:N0} байт, {3:N1} с)" -f $project.BaseName, $wasm, $size, ((Get-Date) - $started).TotalSeconds)
}
