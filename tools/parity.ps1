<#
.SYNOPSIS
  Сверка тестов TS и .NET по заголовкам (docs/dotnet-n5.md, Н5.6).

.DESCRIPTION
  Заголовки test('...') из packages/tests/*.test.ts сверяются с DisplayName в [Fact]/[Theory] тестов .NET:
  dotnet/tests/Kharos.Base.Tests/Sim/**, dotnet/tests/Kharos.Engine.Tests/Movement/**, dotnet/tests/Kharos.Host.Tests/**,
  а с Н6 (docs/dotnet-n6.md, Н6.3) и подача: dotnet/tests/Kharos.Client.Tests/**, Kharos.Base.Tests/Client/**,
  Kharos.Base.Tests/AudioTests/** и dotnet/tests/Kharos.Audio.Tests/**.
  По каждому файлу TS печатается: всего, перенесено, пропущено (Skip = "ждёт ...") и не перенесено.
  Заголовок в .NET — дословный заголовок TS. Инструмент-отчёт: код возврата всегда 0.

.PARAMETER Missing
  Печатать заголовки тестов, которых в .NET нет (по файлам TS).

.PARAMETER Skipped
  Печатать пропущенные тесты с причиной Skip.

.PARAMETER File
  Только эти файлы TS (подстрока имени, например "mining" или "units").

.PARAMETER Extra
  Печатать тесты .NET с DisplayName, которого нет ни в одном файле TS (новые, не из переноса).

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

# ===== Сверка =====
$rows = [System.Collections.Generic.List[object]]::new()
$tsAll = [System.Collections.Generic.HashSet[string]]::new()
foreach ($name in $ts.Keys) {
    if ($File -and -not ($File | Where-Object { $name -like "*$_*" })) { continue }
    $portedCount = 0; $skippedCount = 0
    $missingTitles = [System.Collections.Generic.List[string]]::new()
    $skippedTitles = [System.Collections.Generic.List[string]]::new()
    foreach ($title in $ts[$name]) {
        [void]$tsAll.Add($title)
        if (-not $net.ContainsKey($title)) { $missingTitles.Add($title) }
        elseif ($null -ne $net[$title].Skip) { $skippedCount++; $skippedTitles.Add("$title  [$($net[$title].Skip)]") }
        else { $portedCount++ }
    }
    $rows.Add([pscustomobject]@{
            File    = $name
            Total   = $ts[$name].Count
            Ported  = $portedCount
            Skipped = $skippedCount
            Missing = $missingTitles.Count
            MissingTitles = $missingTitles
            SkippedTitles = $skippedTitles
        })
}

# Все заголовки TS, чтобы найти новые тесты .NET (при фильтре по файлам — только из выбранных не считаем).
if ($File) { foreach ($name in $ts.Keys) { foreach ($title in $ts[$name]) { [void]$tsAll.Add($title) } } }
$netExtra = @($net.Keys | Where-Object { -not $tsAll.Contains($_) } | Sort-Object)

if ($Json) {
    [pscustomobject]@{
        Files  = $rows | Select-Object File, Total, Ported, Skipped, Missing
        Totals = [pscustomobject]@{
            Total   = [int]($rows | Measure-Object Total -Sum).Sum
            Ported  = [int]($rows | Measure-Object Ported -Sum).Sum
            Skipped = [int]($rows | Measure-Object Skipped -Sum).Sum
            Missing = [int]($rows | Measure-Object Missing -Sum).Sum
            NetTests = $netCount
            NetExtra = $netExtra.Count
        }
    } | ConvertTo-Json -Depth 4
    exit 0
}

'{0,-26} {1,6} {2,9} {3,8} {4,8}' -f 'файл TS', 'всего', 'перенесено', 'пропущено', 'нет' | Write-Host
'{0,-26} {1,6} {2,9} {3,8} {4,8}' -f ('-' * 26), '-----', '---------', '--------', '---' | Write-Host
foreach ($row in $rows) {
    '{0,-26} {1,6} {2,9} {3,8} {4,8}' -f $row.File, $row.Total, $row.Ported, $row.Skipped, $row.Missing | Write-Host
    if ($Missing) { foreach ($title in $row.MissingTitles) { Write-Host "    нет: $title" } }
    if ($Skipped) { foreach ($title in $row.SkippedTitles) { Write-Host "    пропущен: $title" } }
}
$total = ($rows | Measure-Object Total -Sum).Sum
$portedSum = ($rows | Measure-Object Ported -Sum).Sum
$skippedSum = ($rows | Measure-Object Skipped -Sum).Sum
$missingSum = ($rows | Measure-Object Missing -Sum).Sum
'{0,-26} {1,6} {2,9} {3,8} {4,8}' -f ('-' * 26), '-----', '---------', '--------', '---' | Write-Host
'{0,-26} {1,6} {2,9} {3,8} {4,8}' -f 'Итого', $total, $portedSum, $skippedSum, $missingSum | Write-Host
Write-Host ("Тестов .NET с DisplayName: {0}; из них не из TS: {1}" -f $netCount, $netExtra.Count)
if ($Extra) { foreach ($title in $netExtra) { Write-Host "    новый: $title  ($($net[$title].Where))" } }
exit 0
