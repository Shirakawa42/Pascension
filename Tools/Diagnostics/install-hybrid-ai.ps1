param(
    [string]$Target = 'E:\Bureau\pascension-windows-v1.0.4'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $project 'Tools\NativeAIRelease\bin\Release\netstandard2.1\Shards.AI.dll'
$tested = Join-Path $project 'Builds\WindowsTacticalAI\pascension_Data\Managed'
$managed = Join-Path $Target 'pascension_Data\Managed'
$status = Join-Path $project 'Tools\TrainingPreflight\results\hybrid-installation-2026-09-28.json'
$destination = Join-Path $managed 'Shards.AI.dll'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$assembly = [Reflection.AssemblyName]::GetAssemblyName($source).FullName
if ($assembly -ne 'Shards.AI, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null') {
    throw 'Unexpected AI assembly identity.'
}
foreach ($name in @('Pascension.Core.dll', 'Shards.Engine.dll', 'Shards.Content.dll')) {
    if ((Get-FileHash (Join-Path $tested $name)).Hash -ne (Get-FileHash (Join-Path $managed $name)).Hash) {
        throw "Installed dependency differs from the headless-tested binary: $name"
    }
}
# The AI now also consumes public-event metadata from the rebuilt Engine and
# Content assemblies. This legacy single-DLL installer must never pair it with
# older installed dependencies; use a matching full build in that case.
foreach ($name in @('Shards.Engine.dll', 'Shards.Content.dll')) {
    $compiledDependency = Join-Path (Split-Path -Parent $source) $name
    if (!(Test-Path -LiteralPath $compiledDependency) -or
        (Get-FileHash -LiteralPath $compiledDependency).Hash -ne (Get-FileHash (Join-Path $managed $name)).Hash) {
        throw "AI dependency requires a matching full Engine/Content deployment: $name"
    }
}
# A single foreground invocation: no periodic process, console, or game restart.
if (@(Get-Process -Name pascension -ErrorAction SilentlyContinue).Count -gt 0) {
    $result = @{ installed=$false; reason='Game is running'; source=$source; source_sha256=$sourceHash; unity_used=$false }
} else {
    $backup = Join-Path $Target ('ai-backups\before-hybrid-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backup -ErrorAction Stop | Out-Null
    $staged = Join-Path $managed ('Shards.AI.hybrid-' + [Guid]::NewGuid().ToString('N') + '.dll')
    Copy-Item -LiteralPath $source -Destination $staged
    if ((Get-FileHash -LiteralPath $staged).Hash -ne $sourceHash) { throw 'Staged AI hash mismatch.' }
    [IO.File]::Replace($staged, $destination, (Join-Path $backup 'Shards.AI.dll'))
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $sourceHash) { throw 'Installed AI hash mismatch.' }
    $result = @{ installed=$true; path=$Target; backup=$backup; ai_sha256=$sourceHash; assembly=$assembly; unity_used=$false }
}
$json = $result | ConvertTo-Json
[IO.File]::WriteAllText($status, $json)
Write-Output $json
