# Упаковка релиза (docs/dotnet-n7.md, Н7-6, Н7-7): NativeAOT-публикация, проверка состава, лицензии, README, архив и SHA256.
# Использование: pwsh dotnet/tools/package.ps1 -Rid win-x64|linux-x64|linux-arm64 -Kind desktop|server -Version 0.2.0 -Out <папка>
# Результат в <Out>: kharos-<rid>.zip | kharos-<rid>.tar.gz | kharos-server-<rid>.tar.gz (win-x64 — .zip) и рядом <архив>.sha256.
# Внутри архива папка kharos-<версия>-<rid>/ (у сервера kharos-server-<версия>-<rid>/). Версии в имени файла нет (Н7-7).
# Моды (host.wasm, client.wasm) должны быть собраны заранее: tools/build-mods.ps1 или tools/wasi-sdk.sh.
# Архив linux-* собирайте на Linux: на Windows бит исполнения в tar не сохранить.
# -SkipPublish: взять уже опубликованную папку из <Out>/work-<kind>-<rid>/publish (для отладки самой упаковки).
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'linux-arm64')][string]$Rid,
    [Parameter(Mandatory)][ValidateSet('desktop', 'server')][string]$Kind,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'

$dotnetRoot = Split-Path $PSScriptRoot -Parent
$isWin = $Rid -like 'win-*'
if (-not $isWin -and $IsWindows) { throw 'linux-архив собирается на Linux: tar на Windows не сохраняет бит исполнения' }
$project = if ($Kind -eq 'desktop') { 'apps/Kharos.Desktop/Kharos.Desktop.csproj' } else { 'apps/Kharos.Server/Kharos.Server.csproj' }
$exeBase = if ($Kind -eq 'desktop') { 'kharos' } else { 'kharos-server' }
$exe = if ($isWin) { "$exeBase.exe" } else { $exeBase }
$topName = if ($Kind -eq 'desktop') { "kharos-$Version-$Rid" } else { "kharos-server-$Version-$Rid" }
# Сервер win-x64 в релиз не идёт (Н7.4), но собирается для смоука на Windows (Н7.3) — тогда zip, как у игры.
$archiveName = if ($isWin) { if ($Kind -eq 'desktop') { "kharos-$Rid.zip" } else { "kharos-server-$Rid.zip" } } elseif ($Kind -eq 'desktop') { "kharos-$Rid.tar.gz" } else { "kharos-server-$Rid.tar.gz" }

# Нативные библиотеки по RID (пакеты Silk.NET.WebGPU.Native.WGPU, Silk.NET.OpenAL.Soft.Native, Ultz.Native.GLFW, Wasmtime).
$nativeDesktop = if ($isWin) { 'wgpu_native.dll', 'soft_oal.dll', 'glfw3.dll', 'wasmtime.dll' }
                 else { 'libwgpu_native.so', 'libopenal.so', 'libglfw.so.3', 'libwasmtime.so' }
$nativeServer = if ($isWin) { 'wasmtime.dll' } else { 'libwasmtime.so' }
$required = @($exe) + $(if ($Kind -eq 'desktop') { $nativeDesktop } else { $nativeServer })
$required += 'mods/base/mod.json', 'mods/base/host.wasm', 'mods/base/client.wasm'
if ($Kind -eq 'desktop' -and -not $isWin) { $required += 'kharos.desktop', 'kharos.png' }

$Out = [IO.Path]::GetFullPath($Out)
$work = Join-Path $Out "work-$Kind-$Rid"
$publishDir = Join-Path $work 'publish'
$top = Join-Path $work $topName
if (-not $SkipPublish) {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $publishDir | Out-Null
    Write-Host "== publish $Kind $Rid $Version"
    dotnet publish (Join-Path $dotnetRoot $project) -c Release -r $Rid -o $publishDir "-p:KharosVersion=$Version" '-p:KharosShipMods=base' -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish упал ($LASTEXITCODE)" }
}
if (Test-Path $top) { Remove-Item $top -Recurse -Force }
Move-Item $publishDir $top

# --- состав ---
# Символы отладки проектных сборок NativeAOT всё равно кладёт в публикацию - в архив их не берём.
Get-ChildItem $top -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.dbg' -or $_.Name -like 'Kharos.*.xml' } | Remove-Item -Force
$rel = Get-ChildItem $top -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($top, $_.FullName).Replace('\', '/') }
$problems = @()
foreach ($r in $required) { if ($rel -notcontains $r) { $problems += "нет $r" } }
foreach ($m in 'data', 'schema', 'i18n') {
    if (-not ($rel | Where-Object { $_ -like "mods/base/$m/*.json" })) { $problems += "нет mods/base/$m/*.json" }
}
foreach ($r in $rel) {
    if ($r -match '(^|/)hello(/|$)') { $problems += "лишнее (hello): $r" }
    elseif ($r -like 'mods/*' -and $r -notlike 'mods/base/*') { $problems += "лишний мод: $r" }
    if ($r -like '*.pdb' -or $r -like '*.dbg') { $problems += "лишнее (отладка): $r" }
}
if ($problems) { throw "Состав сборки не годится:`n  " + ($problems -join "`n  ") }
Write-Host ('== верхний уровень: ' + (($rel | Where-Object { $_ -notlike 'mods/*' }) -join ', '))

# --- лицензии и README ---
$lic = Join-Path $top 'LICENSES'
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item (Join-Path $dotnetRoot 'licenses/*') $lic -Recurse
$readme = [IO.File]::ReadAllText((Join-Path $dotnetRoot 'dist/README.txt')).Replace("`r`n", "`n")
$keepKind = if ($Kind -eq 'desktop') { 'DESKTOP' } else { 'SERVER' }
$dropKind = if ($Kind -eq 'desktop') { 'SERVER' } else { 'DESKTOP' }
$keepOs = if ($isWin) { 'WIN' } else { 'LINUX' }
$dropOs = if ($isWin) { 'LINUX' } else { 'WIN' }
foreach ($tag in $dropKind, $dropOs) { $readme = [regex]::Replace($readme, "(?s)\[\[$tag\]\].*?\[\[/$tag\]\]\n?", '') }
foreach ($tag in $keepKind, $keepOs) { $readme = $readme.Replace("[[$tag]]`n", '').Replace("[[/$tag]]`n", '') }
$readme = $readme.Replace('{VERSION}', $Version).Replace('{RID}', $Rid).Replace('{EXE}', $exe).Replace('{ARCHIVE}', $archiveName)
[IO.File]::WriteAllText((Join-Path $top 'README.txt'), $readme, (New-Object Text.UTF8Encoding $false))

# --- архив ---
$archive = Join-Path $Out $archiveName
if (Test-Path $archive) { Remove-Item $archive -Force }
if ($archiveName.EndsWith('.zip')) {
    Compress-Archive -Path $top -DestinationPath $archive -CompressionLevel Optimal
} else {
    & chmod +x (Join-Path $top $exe)
    tar -czf $archive -C $work $topName
    if ($LASTEXITCODE -ne 0) { throw 'tar упал' }
}
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $archiveName`n", (New-Object Text.UTF8Encoding $false))
Write-Host ('== {0}: {1:N1} МБ, sha256 {2}' -f $archiveName, ((Get-Item $archive).Length / 1MB), $hash)
Remove-Item $work -Recurse -Force
Write-Output $archive
