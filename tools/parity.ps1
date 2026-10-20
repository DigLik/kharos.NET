<#
.SYNOPSIS
  Сверка тестов TS и .NET по заголовкам (docs/dotnet-n5.md, Н5.6).

.DESCRIPTION
  Заголовки test('...') из packages/tests/*.test.ts сверяются с DisplayName в [Fact]/[Theory] тестов .NET:
  dotnet/tests/Kharos.Base.Tests/Sim/**, dotnet/tests/Kharos.Engine.Tests/Movement/**, dotnet/tests/Kharos.Host.Tests/**,
  а с Н6 (docs/dotnet-n6.md, Н6.3) и подача: dotnet/tests/Kharos.Client.Tests/**, Kharos.Base.Tests/Client/**,
  Kharos.Base.Tests/AudioTests/** и dotnet/tests/Kharos.Audio.Tests/**.
  С Н7 (docs/dotnet-n7.md, Н7.2) и Kharos.Server.Tests, Kharos.Engine.Tests/Terrain.
  По каждому файлу TS печатается: всего, перенесено, пропущено (Skip = "ждёт ..."), устарело (tools/parity-obsolete.json) и не перенесено.
  Заголовок в .NET — дословный заголовок TS. Инструмент-отчёт: код возврата всегда 0.

  tools/parity-obsolete.json — массив { file, title?, reason }: file — имя файла TS (подстрока, без пути), title — заголовок
  теста; без title устарел весь файл. Перенесённый тест выше «устарело»: запись про него лишняя и отчёт её покажет.
  Пропуски .NET (Skip = "...") должны иметь причину «устарело»: либо текст Skip начинается с «устарело», либо заголовок
  есть в parity-obsolete.json; остальные печатаются в конце отчёта (-Skipped подробно).

.PARAMETER Missing
  Печатать заголовки тестов, которых в .NET нет (по файлам TS).

.PARAMETER Skipped
  Печатать пропущенные тесты с причиной Skip.

.PARAMETER File
  Только эти файлы TS (подстрока имени, например "mining" или "units").

.PARAMETER Extra
  Печатать тесты .NET с DisplayName, которого нет ни в одном файле TS (новые, не из переноса).

.PARAMETER Obsolete
  Печатать устаревшие тесты TS с причинами и записи описи, которым нет тестов TS или которые перенесены.

.PARAMETER Json
  Итог машинным видом (JSON) вместо таблицы.

.EXAMPLE
  pwsh tools/parity.ps1
  pwsh tools/parity.ps1 -File mining -Missing
#>
[CmdletBinding()]
param(
    [switch]$Missing,
    [switch]$Skipped,
    [switch]$Obsolete,
    [switch]$Extra,
    [switch]$Json,
    [string[]]$File
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path -Parent $PSScriptRoot

# ===== Заголовки TS =====
# test('...') и test.skipIf(...)('...'); заголовок — строка в одинарных, двойных или обратных кавычках с экранированием.
$tsPattern = [regex]::new(
    '(?m)^[ \t]*(?:test|it)(?:\.\w+(?:\((?:[^()\r\n]|\([^()\r\n]*\))*\))?)?\(\s*(?<q>[''"`])(?<title>(?:\\.|(?!\k<q>).)*)\k<q>')

function ConvertFrom-TsString([string]$text) {
    # Экранирование TS: \' \" \\ \n \`
    [regex]::Replace($text, '\\(.)', { param($m) switch ($m.Groups[1].Value) { 'n' { "`n" } 't' { "`t" } default { $m.Groups[1].Value } } })
}

function Get-Normalized([string]$text) { ($text -replace '\s+', ' ').Trim() }

$tsDir = Join-Path $root 'packages/tests'
$tsFiles = Get-ChildItem -Path $tsDir -Filter '*.test.ts' | Sort-Object Name
$ts = [ordered]@{}
foreach ($f in $tsFiles) {
    $text = Get-Content -Raw -Encoding utf8 $f.FullName
    $titles = [System.Collections.Generic.List[string]]::new()
    foreach ($m in $tsPattern.Matches($text)) {
        $titles.Add((Get-Normalized (ConvertFrom-TsString $m.Groups['title'].Value)))
    }
    $ts[$f.Name] = $titles
}

# ===== DisplayName в .NET =====
# [Fact(DisplayName = "...", Skip = "...")] и [Theory(...)], атрибут может идти в несколько строк, поля — в любом порядке.
$csAttr = [regex]::new('\[(?:Fact|Theory)\s*\((?<args>(?:[^\]"]|"(?:[^"\\]|\\.)*")*)\)\]', 'Singleline')
$csField = [regex]::new('(?<name>DisplayName|Skip)\s*=\s*"(?<value>(?:[^"\\]|\\.)*)"')

function ConvertFrom-CsString([string]$text) {
    [regex]::Replace($text, '\\(.)', { param($m) switch ($m.Groups[1].Value) { 'n' { "`n" } 't' { "`t" } default { $m.Groups[1].Value } } })
}

$csDirs = @(
    'dotnet/tests/Kharos.Base.Tests/Sim',
    'dotnet/tests/Kharos.Engine.Tests/Movement',
    'dotnet/tests/Kharos.Host.Tests',
    # Н7.2: земля движка и сервер.
    'dotnet/tests/Kharos.Engine.Tests/Terrain',
    'dotnet/tests/Kharos.Server.Tests',
    # Подача (docs/dotnet-n6.md, Н6.3): клиент, клиент base, звук base и движок звука.
    'dotnet/tests/Kharos.Client.Tests',
    'dotnet/tests/Kharos.Base.Tests/Client',
    'dotnet/tests/Kharos.Base.Tests/AudioTests',
    'dotnet/tests/Kharos.Audio.Tests'
) | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ }

# title -> @{ Skip = <причина или $null>; Where = <файл> }
$net = @{}
$netCount = 0
foreach ($dir in $csDirs) {
    foreach ($f in Get-ChildItem -Path $dir -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) {
        $text = Get-Content -Raw -Encoding utf8 $f.FullName
        foreach ($attr in $csAttr.Matches($text)) {
            $display = $null
            $skip = $null
            foreach ($field in $csField.Matches($attr.Groups['args'].Value)) {
                $value = ConvertFrom-CsString $field.Groups['value'].Value
                if ($field.Groups['name'].Value -eq 'DisplayName') { $display = Get-Normalized $value } else { $skip = $value }
            }
            if ($null -eq $display) { continue }
            $netCount++
            $relative = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
            # Тест без Skip сильнее пропущенного с тем же заголовком.
            if (-not $net.ContainsKey($display) -or ($null -ne $net[$display].Skip -and $null -eq $skip)) {
                $net[$display] = @{ Skip = $skip; Where = $relative }
            }
        }
    }
}

# ===== Опись устаревшего (tools/parity-obsolete.json) =====
$obsoleteFile = Join-Path $PSScriptRoot 'parity-obsolete.json'
$obsoleteEntries = if (Test-Path $obsoleteFile) { @(Get-Content -Raw -Encoding utf8 $obsoleteFile | ConvertFrom-Json) } else { @() }
$obsoleteUsed = [System.Collections.Generic.HashSet[object]]::new()

function Find-Obsolete([string]$tsFile, [string]$title) {
    foreach ($entry in $obsoleteEntries) {
        if ($tsFile -notlike "*$($entry.file)*") { continue }
        if ($entry.PSObject.Properties['title'] -and $entry.title) {
            if ((Get-Normalized $entry.title) -ne $title) { continue }
        }
        return $entry
    }
    return $null
}

# ===== Сверка =====
$rows = [System.Collections.Generic.List[object]]::new()
$tsAll = [System.Collections.Generic.HashSet[string]]::new()
foreach ($name in $ts.Keys) {
    if ($File -and -not ($File | Where-Object { $name -like "*$_*" })) { continue }
    $portedCount = 0; $skippedCount = 0; $obsoleteCount = 0
    $obsoleteTitles = [System.Collections.Generic.List[string]]::new()
    $missingTitles = [System.Collections.Generic.List[string]]::new()
    $skippedTitles = [System.Collections.Generic.List[string]]::new()
    foreach ($title in $ts[$name]) {
        [void]$tsAll.Add($title)
        $entry = Find-Obsolete $name $title
        if ($entry) { [void]$obsoleteUsed.Add($entry) }
        if ($net.ContainsKey($title) -and $null -eq $net[$title].Skip) { $portedCount++ }
        elseif ($entry) { $obsoleteCount++; $obsoleteTitles.Add("$title  [$($entry.reason)]") }
        elseif ($net.ContainsKey($title) -and $net[$title].Skip -like 'устарело*') { $obsoleteCount++; $obsoleteTitles.Add("$title  [$($net[$title].Skip)]") }
        elseif ($net.ContainsKey($title)) { $skippedCount++; $skippedTitles.Add("$title  [$($net[$title].Skip)]") }
        else { $missingTitles.Add($title) }
    }
    $rows.Add([pscustomobject]@{
            File    = $name
            Total   = $ts[$name].Count
            Ported  = $portedCount
            Skipped = $skippedCount
            Obsolete = $obsoleteCount
            Missing = $missingTitles.Count
            ObsoleteTitles = $obsoleteTitles
            MissingTitles = $missingTitles
            SkippedTitles = $skippedTitles
        })
}

# Все заголовки TS, чтобы найти новые тесты .NET (при фильтре по файлам — только из выбранных не считаем).
if ($File) { foreach ($name in $ts.Keys) { foreach ($title in $ts[$name]) { [void]$tsAll.Add($title) } } }
$netExtra = @($net.Keys | Where-Object { -not $tsAll.Contains($_) } | Sort-Object)

# Пропуски атрибутом (Skip = "...") во всех тестах .NET, в том числе без DisplayName и вне сверяемых папок.
$allSkips = [System.Collections.Generic.List[object]]::new()
foreach ($f in Get-ChildItem -Path (Join-Path $root 'dotnet/tests') -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) {
    $text = Get-Content -Raw -Encoding utf8 $f.FullName
    foreach ($attr in $csAttr.Matches($text)) {
        $display = $null
        $skip = $null
        foreach ($field in $csField.Matches($attr.Groups['args'].Value)) {
            $value = ConvertFrom-CsString $field.Groups['value'].Value
            if ($field.Groups['name'].Value -eq 'DisplayName') { $display = Get-Normalized $value } else { $skip = $value }
        }
        if ($null -eq $skip) { continue }
        $relative = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
        $allSkips.Add([pscustomobject]@{ Title = if ($display) { $display } else { '(без DisplayName)' }; Skip = $skip; Where = $relative })
    }
}

# Пропуски .NET без причины «устарело»: текст Skip начинается с «устарело» или заголовок есть в описи.
function Test-SkipHasReason($item) {
    if ($item.Skip -like 'устарело*') { return $true }
    foreach ($entry in $obsoleteEntries) {
        if ($entry.PSObject.Properties['title'] -and $entry.title -and (Get-Normalized $entry.title) -eq $item.Title) { return $true }
    }
    return $false
}
$skipsWithoutReason = @($allSkips | Where-Object { -not (Test-SkipHasReason $_) })
# Записи описи, которым не соответствует ни один тест TS (опечатка в file или title).
$unusedEntries = @($obsoleteEntries | Where-Object { -not $File -and -not $obsoleteUsed.Contains($_) })

$total = [int]($rows | Measure-Object Total -Sum).Sum
$portedSum = [int]($rows | Measure-Object Ported -Sum).Sum
$skippedSum = [int]($rows | Measure-Object Skipped -Sum).Sum
$obsoleteSum = [int]($rows | Measure-Object Obsolete -Sum).Sum
$missingSum = [int]($rows | Measure-Object Missing -Sum).Sum

if ($Json) {
    [pscustomobject]@{
        Files  = $rows | Select-Object File, Total, Ported, Skipped, Obsolete, Missing
        Totals = [pscustomobject]@{
            Total   = $total
            Ported  = $portedSum
            Skipped = $skippedSum
            Obsolete = $obsoleteSum
            Missing = $missingSum
            NetTests = $netCount
            NetExtra = $netExtra.Count
            NetSkipsWithoutReason = $skipsWithoutReason.Count
            UnusedObsoleteEntries = $unusedEntries.Count
        }
    } | ConvertTo-Json -Depth 4
    exit 0
}

$format = '{0,-26} {1,6} {2,10} {3,9} {4,8} {5,6}'
$format -f 'файл TS', 'всего', 'перенесено', 'пропущено', 'устарело', 'нет' | Write-Host
$format -f ('-' * 26), '-----', '----------', '---------', '--------', '---' | Write-Host
foreach ($row in $rows) {
    $format -f $row.File, $row.Total, $row.Ported, $row.Skipped, $row.Obsolete, $row.Missing | Write-Host
    if ($Missing) { foreach ($title in $row.MissingTitles) { Write-Host "    нет: $title" } }
    if ($Skipped) { foreach ($title in $row.SkippedTitles) { Write-Host "    пропущен: $title" } }
    if ($Obsolete) { foreach ($title in $row.ObsoleteTitles) { Write-Host "    устарел: $title" } }
}
$format -f ('-' * 26), '-----', '----------', '---------', '--------', '---' | Write-Host
$format -f 'Итого', $total, $portedSum, $skippedSum, $obsoleteSum, $missingSum | Write-Host
Write-Host ("Тестов .NET с DisplayName: {0}; из них не из TS: {1}" -f $netCount, $netExtra.Count)
if ($Extra) { foreach ($title in $netExtra) { Write-Host "    новый: $title  ($($net[$title].Where))" } }
Write-Host ("Пропусков .NET: {0}; без причины «устарело»: {1}" -f $allSkips.Count, $skipsWithoutReason.Count)
foreach ($item in $skipsWithoutReason) { Write-Host "    пропуск без причины «устарело»: $($item.Title)  [$($item.Skip)]  ($($item.Where))" }
if ($unusedEntries.Count -gt 0) {
    Write-Host ("Записей описи без теста TS: {0}" -f $unusedEntries.Count)
    foreach ($entry in $unusedEntries) { Write-Host "    запись описи: $($entry.file) / $($entry.title)" }
}
exit 0
