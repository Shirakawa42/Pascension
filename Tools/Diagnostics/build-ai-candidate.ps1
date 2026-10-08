param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$ExpectedPolicySha256,
    [string]$ProjectDirectory = 'F:\Unity\projects\pascension',
    [string]$UnityEditor = 'F:\Unity\editors\6000.3.7f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
# Packaging only. Simulations and tactical tests use the native headless host.
# One hidden editor process; no polling shells or recurring console launchers.
if (Get-Process Unity -ErrorAction SilentlyContinue) {
    throw 'An editor is already running. Do not build over an active project.'
}
$policyPath = Join-Path $ProjectDirectory 'Assets\Resources\AI\shards-policy.bytes'
$settingsPath = Join-Path $ProjectDirectory 'Assets\Resources\AI\shards-search-settings.json'
if (Test-Path (Join-Path $ProjectDirectory 'Assets\Resources\AI\shards-policy.json')) {
    throw 'Rename metadata to shards-policy-metadata.json before packaging; it collides with the model resource path.'
}
if (-not (Test-Path $UnityEditor) -or -not (Test-Path $settingsPath)) {
    throw 'Editor or selected runtime search settings are missing.'
}
if ((Get-FileHash $policyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedPolicySha256.ToLowerInvariant()) {
    throw 'Project policy differs from the selected, validated policy.'
}
if (Test-Path $OutputDirectory) { throw 'Use a new output directory; existing builds are never overwritten.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$buildLog = Join-Path $OutputDirectory 'unity-build.log'
$buildStatus = Join-Path $OutputDirectory 'build-status.json'
$executable = Join-Path $OutputDirectory 'pascension.exe'
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', $ProjectDirectory,
    '-buildTarget', 'StandaloneWindows64', '-executeMethod', 'Pascension.Editor.CiBuild.Build',
    '-customBuildPath', $executable, '-buildVersion', '1.0', '-logFile', $buildLog,
    '-job-worker-count', '8')
function Save-Status($value) {
    $value | ConvertTo-Json -Depth 8 | Set-Content -Path $buildStatus -Encoding UTF8
}
$editorProcess = $null
try {
    $quoted = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $editorProcess = Start-Process -FilePath $UnityEditor -ArgumentList $quoted -PassThru -WindowStyle Hidden
    # Same eight logical processors used by the native evaluation jobs.
    $editorProcess.ProcessorAffinity = [IntPtr]0x5555
    Save-Status @{ state = 'building'; pid = $editorProcess.Id; policy_sha256 = $ExpectedPolicySha256;
                   utc = [DateTime]::UtcNow.ToString('o'); unity_tests = $false; cpu_affinity = '0,2,4,6,8,10,12,14' }
    $editorProcess.WaitForExit()
    if ($editorProcess.ExitCode -ne 0) { throw "Unity build exited $($editorProcess.ExitCode). See $buildLog" }
    $managed = Join-Path $OutputDirectory 'pascension_Data\Managed'
    $hashes = @{}
    foreach ($name in @('Pascension.Core.dll', 'Shards.Engine.dll', 'Shards.Content.dll', 'Shards.AI.dll')) {
        $path = Join-Path $managed $name
        if (-not (Test-Path $path)) { throw "Built assembly missing: $name" }
        $hashes[$name] = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (-not (Test-Path $executable)) { throw 'Built executable missing.' }
    Copy-Item (Join-Path $ProjectDirectory 'Assets\Resources\AI\shards-inference.json') $OutputDirectory
    Save-Status @{ state = 'built_not_installed'; policy_sha256 = $ExpectedPolicySha256;
                   assemblies = $hashes; utc = [DateTime]::UtcNow.ToString('o'); unity_tests = $false }
} catch {
    if ($editorProcess -and -not $editorProcess.HasExited) { $editorProcess.Kill() }
    Save-Status @{ state = 'failed'; error = $_.Exception.Message; utc = [DateTime]::UtcNow.ToString('o') }
    throw
}
