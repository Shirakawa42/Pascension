# Hidden bridge invoked only by GUI wscript.exe or a native no-console caller.
# Arguments are data, never a PowerShell expression. The target has no console.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ScriptPath,
    [Parameter(Mandatory = $true)] [string] $ArgumentsBase64,
    [Parameter(Mandatory = $true)] [string] $ReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Quote-Arg([string] $Value) {
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    return '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$stdoutPath = $ReportPath + '.' + [Guid]::NewGuid().ToString('N') + '.stdout.log'
$stderrPath = $ReportPath + '.' + [Guid]::NewGuid().ToString('N') + '.stderr.log'
$report = [ordered] @{ schema = 'shards-hidden-launch-v1'; script = $ScriptPath; started = $false; create_no_window = $true; use_shell_execute = $false; bridge_pid = $PID; stdout = $stdoutPath; stderr = $stderrPath }
function Write-Report {
    $temporary = $ReportPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($report | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $ReportPath) { [IO.File]::Replace($temporary, $ReportPath, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $ReportPath) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
$child = New-Object Diagnostics.Process
$stdoutStream = $null; $stderrStream = $null
try {
    if ($env:OS -ne 'Windows_NT' -or -not [IO.Path]::IsPathRooted($ScriptPath) -or $ScriptPath -match '[\x00\r\n]') { throw 'Absolute Windows script path required.' }
    $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
    # -InputObject preserves empty/singleton arrays on Windows PowerShell 5.
    $parsed = ConvertFrom-Json -InputObject $decoded
    if ($decoded.TrimStart() -notmatch '^\[') { throw 'Arguments must be a JSON string array.' }
    $arguments = @($parsed)
    if ($decoded -match '^\s*\[\s*\]\s*$') { $arguments = @() }
    foreach ($argument in $arguments) {
        if ($argument -isnot [string] -or $argument -match '[\x00\r\n]') { throw 'Arguments must be single-line strings.' }
    }
    $child.StartInfo = New-Object Diagnostics.ProcessStartInfo
    $child.StartInfo.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $child.StartInfo.Arguments = (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $arguments | ForEach-Object { Quote-Arg $_ }) -join ' '
    $child.StartInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($ScriptPath)
    $child.StartInfo.UseShellExecute = $false
    $child.StartInfo.CreateNoWindow = $true
    $child.StartInfo.RedirectStandardOutput = $true
    $child.StartInfo.RedirectStandardError = $true
    $stdoutStream = New-Object IO.FileStream($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
    $stderrStream = New-Object IO.FileStream($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
    if (-not $child.Start()) { throw 'Background script did not start.' }
    $report.started = $true; $report['child_pid'] = $child.Id
    $report['child_start_utc'] = $child.StartTime.ToUniversalTime().ToString('o')
    $stdoutPump = $child.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
    $stderrPump = $child.StandardError.BaseStream.CopyToAsync($stderrStream)
    Write-Report
    $child.WaitForExit()
    [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]] @($stdoutPump, $stderrPump))
    $stdoutStream.Flush($true); $stderrStream.Flush($true)
    $report['exit_code'] = $child.ExitCode
    Write-Report
} catch {
    $report['error'] = $_.Exception.ToString()
    Write-Report
    exit 1
} finally {
    if ($null -ne $stdoutStream) { $stdoutStream.Dispose() }
    if ($null -ne $stderrStream) { $stderrStream.Dispose() }
    $child.Dispose()
}
