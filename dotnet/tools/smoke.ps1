# Смоук (docs/dotnet-n7.md, Н7.3): обёртка оркестратора dotnet/tools/Kharos.Smoke. Windows и Linux (pwsh).
# Использование:
#   pwsh dotnet/tools/smoke.ps1 single|server|mixed|soak [-Package <папка упаковки>] [-Build] [-Kharos <путь>] [-Server <путь>]
#        [-Soak 15m] [-Bots 8] [-Gpu none] [-Out <папка>] [-WslServer <путь в WSL>] [-WslDistro <имя>] [-Version 0.2.0] [-Timeout 10m]
# -Package — папка, куда package.ps1/package.sh положили архивы (.zip, .tar.gz и .sha256); архивы распаковываются в папку
#            прогона. Подойдёт и папка с уже распакованными kharos-<версия>-<rid>/ и kharos-server-<версия>-<rid>/.
# -Build   — собрать игру и сервер своей платформы из исходников (package.ps1 или package.sh; моды должны быть собраны).
# -Soak    — нагрузка: сервер и -Bots ботов протокола на заданное время (с single/server/soak — всё равно сценарий soak).
# mixed    — сценарий server с сервером в WSL (-WslServer: kharos-server linux-x64 внутри WSL), клиенты — Windows.
# Код возврата — как у Kharos.Smoke: 0 прошёл, 1 провал, 2 ошибка запуска.
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('single', 'server', 'mixed', 'soak')][string]$Scenario,
    [string]$Package,
    [switch]$Build,
    [string]$Kharos,
    [string]$Server,
    [string]$Soak,
    [int]$Bots = 0,
    [ValidateSet('', 'none')][string]$Gpu = '',
    [string]$Out,
    [string]$WslServer,
    [string]$WslDistro,
    [string]$Version,
    [string]$Timeout
)
$ErrorActionPreference = 'Stop'
$dotnetRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $dotnetRoot 'tools/Kharos.Smoke/Kharos.Smoke.csproj'

dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "сборка Kharos.Smoke упала ($LASTEXITCODE)" }
$tool = Join-Path $dotnetRoot 'tools/Kharos.Smoke/bin/Release/net11.0/Kharos.Smoke.dll'

$argv = @($Scenario)
if ($Package) { $argv += '--package', (Resolve-Path $Package).Path }
if ($Build) { $argv += '--build' }
if ($Kharos) { $argv += '--kharos', $Kharos }
if ($Server) { $argv += '--server', $Server }
if ($Soak) { $argv += '--soak', $Soak }
if ($Bots -gt 0) { $argv += '--bots', "$Bots" }
if ($Gpu) { $argv += '--gpu', $Gpu }
if ($Out) { $argv += '--out', $Out }
if ($WslServer) { $argv += '--wsl-server', $WslServer }
if ($WslDistro) { $argv += '--wsl-distro', $WslDistro }
if ($Version) { $argv += '--version', $Version }
if ($Timeout) { $argv += '--timeout', $Timeout }

dotnet $tool @argv
exit $LASTEXITCODE
