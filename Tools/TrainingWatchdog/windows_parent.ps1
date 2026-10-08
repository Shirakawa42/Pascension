# Background Windows owner with an attached WSL process for one pinned campaign.
# This parent adopts through a read-only sentinel before requesting any runner.
# Training locks, outcomes, checkpoints and learning budgets stay in Linux.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ManifestPath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[a-fA-F0-9]{64}$')] [string] $ManifestSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($env:OS -ne 'Windows_NT') { throw 'Windows PowerShell is required.' }

function Quote-Arg([string] $Value) {
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    return '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
}
function Sha([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Utc-Now { return [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 }
function Assert-Hash([string] $Path, [string] $Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$' -or (Sha $Path) -cne $Expected.ToLowerInvariant()) { throw ('Pinned file changed: ' + $Path) }
}
function Save-State {
    $temporary = $manifest.state_windows_path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Depth 8))
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        # The state must already exist. Missing state never resets retry limits.
        [IO.File]::Replace($temporary, $manifest.state_windows_path, [NullString]::Value)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Log-Event([string] $Kind, $Details) {
    $row = [ordered] @{ utc = [DateTimeOffset]::UtcNow.ToString('o'); event = $Kind; campaign_id = $manifest.campaign_id; parent_pid = $PID; details = $Details }
    [IO.File]::AppendAllText($manifest.log_windows_path, (($row | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
}
function Assert-Pins {
    Assert-Hash $ManifestPath $ManifestSha256
    Assert-Hash $PSCommandPath $manifest.parent_sha256
    Assert-Hash $manifest.runner_windows_path $manifest.runner_sha256
    Assert-Hash $manifest.sentinel_windows_path $manifest.sentinel_sha256
    Assert-Hash $manifest.config_snapshot_windows_path $manifest.config_sha256
    Assert-Hash $manifest.bootstrap_windows_path $manifest.bootstrap_sha256
    Assert-Hash $manifest.launcher_windows_path $manifest.launcher_sha256
}
function Startup-Arguments {
    $payload = ConvertTo-Json -InputObject @('-ManifestPath', $ManifestPath, '-ManifestSha256', $ManifestSha256) -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    return (@('//B', '//Nologo', $manifest.bootstrap_windows_path, $manifest.parent_windows_path, $encoded, ($manifest.log_windows_path + '.bootstrap.json')) | ForEach-Object { Quote-Arg $_ }) -join ' '
}
function Remove-OwnedStartup {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) ('ShardsTrainingRecovery-' + $manifest.campaign_id + '.lnk')
    if (Test-Path -LiteralPath $shortcutPath) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $expectedTarget = Join-Path $env:WINDIR 'System32\wscript.exe'
        $expectedDescription = 'Shards training recovery ' + $manifest.campaign_id + '; manifest_sha256=' + $ManifestSha256
        if (-not [string]::Equals($shortcut.TargetPath, $expectedTarget, [StringComparison]::OrdinalIgnoreCase) -or $shortcut.Arguments -cne (Startup-Arguments) -or $shortcut.Description -cne $expectedDescription -or -not [string]::Equals($shortcut.WorkingDirectory, $manifest.working_directory_windows, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a foreign or changed Startup shortcut.'
        }
        Remove-Item -LiteralPath $shortcutPath
        Log-Event 'startup_removed' @{ path = $shortcutPath }
    }
}
function Latch-Terminal([string] $Reason, [bool] $Unsafe) {
    $state.terminal_latched = $true; $state.terminal_reason = $Reason; $state.unsafe = $Unsafe
    Save-State
    Log-Event 'terminal' @{ reason = $Reason; unsafe = $Unsafe }
    Remove-OwnedStartup
}
function Run-Wsl([string] $Role) {
    Assert-Pins
    $arguments = @('-d', $manifest.distribution, '--user', $manifest.linux_user, '--exec', $manifest.python_linux_path)
    if ($Role -eq 'sentinel') {
        $arguments += @($manifest.sentinel_linux_path, '--config', $manifest.config_linux_path, '--expected-sha', $manifest.config_sha256, '--hard-deadline-wall', $deadlineArgument, '--expected-runner-sha', $manifest.runner_sha256)
    } else { $arguments += @($manifest.runner_linux_path, '--config', $manifest.config_linux_path) }
    $nonce = [Guid]::NewGuid().ToString('N')
    $stdout = Join-Path ([IO.Path]::GetDirectoryName($manifest.log_windows_path)) ($Role + '-' + $nonce + '-stdout.log')
    $stderr = Join-Path ([IO.Path]::GetDirectoryName($manifest.log_windows_path)) ($Role + '-' + $nonce + '-stderr.log')
    $command = ($arguments | ForEach-Object { Quote-Arg $_ }) -join ' '
    Log-Event 'child_start' @{ role = $Role; arguments = $command; stdout = $stdout; stderr = $stderr }
    # Native no-console creation also applies when this parent was launched by
    # Explorer at login. Keep the WSL client attached throughout evaluation.
    # Pure .NET stream pumps avoid PowerShell callback/runspace requirements;
    # one-byte FileStream buffers publish the initial holding row immediately.
    $child = New-Object Diagnostics.Process
    $child.StartInfo = New-Object Diagnostics.ProcessStartInfo
    $child.StartInfo.FileName = Join-Path $env:WINDIR 'System32\wsl.exe'
    $child.StartInfo.Arguments = $command
    $child.StartInfo.WorkingDirectory = $manifest.working_directory_windows
    $child.StartInfo.UseShellExecute = $false
    $child.StartInfo.CreateNoWindow = $true
    $child.StartInfo.RedirectStandardOutput = $true
    $child.StartInfo.RedirectStandardError = $true
    $stdoutStream = $null; $stderrStream = $null
    try {
        $stdoutStream = New-Object IO.FileStream($stdout, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
        $stderrStream = New-Object IO.FileStream($stderr, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
        if (-not $child.Start()) { throw 'WSL process did not start.' }
        Log-Event 'child_running' @{ role = $Role; pid = $child.Id; start_utc = $child.StartTime.ToUniversalTime().ToString('o'); create_no_window = $true; use_shell_execute = $false; stdout = $stdout; stderr = $stderr }
        $stdoutPump = $child.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
        $stderrPump = $child.StandardError.BaseStream.CopyToAsync($stderrStream)
        $child.WaitForExit()
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]] @($stdoutPump, $stderrPump))
        $exitCode = $child.ExitCode
        $stdoutStream.Flush($true); $stderrStream.Flush($true)
    } finally {
        if ($null -ne $stdoutStream) { $stdoutStream.Dispose() }
        if ($null -ne $stderrStream) { $stderrStream.Dispose() }
        $child.Dispose()
    }
    $protocol = $null
    if ($Role -eq 'sentinel') {
        foreach ($line in @(Get-Content -LiteralPath $stdout -Encoding UTF8 -Tail 20)) {
            try { $candidate = $line | ConvertFrom-Json; if ($candidate.protocol -ceq 'shards-watchdog-keepalive-v1') { $protocol = $candidate } } catch { }
        }
    }
    $result = @{ role = $Role; exit_code = $exitCode; protocol = $protocol; stdout = $stdout; stderr = $stderr }
    Log-Event 'child_exit' $result
    return $result
}

$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$ManifestSha256 = $ManifestSha256.ToLowerInvariant()
Assert-Hash $ManifestPath $ManifestSha256
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -cne 'shards-windows-watchdog-v1' -or $manifest.campaign_id -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$') { throw 'Invalid manifest identity.' }
$windowsSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($manifest.windows_user_sid -cne $windowsSid -or $windowsSid -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) { throw 'Run under the manifest-owning Windows user.' }
if ($manifest.linux_user -cne 'lva' -or $manifest.distribution -cne 'Ubuntu-24.04' -or $manifest.max_runner_launch_attempts -ne 3 -or $manifest.max_transport_failures -ne 3 -or $manifest.retry_seconds -lt 1 -or $manifest.retry_seconds -gt 30) { throw 'Invalid watchdog limits/account.' }
foreach ($linuxPath in @($manifest.python_linux_path, $manifest.runner_linux_path, $manifest.sentinel_linux_path, $manifest.config_linux_path)) {
    if (-not $linuxPath.StartsWith('/') -or $linuxPath -match '[\x00\r\n]') { throw 'Invalid Linux path.' }
}
foreach ($windowsPath in @($manifest.parent_windows_path, $manifest.runner_windows_path, $manifest.sentinel_windows_path, $manifest.config_snapshot_windows_path, $manifest.bootstrap_windows_path, $manifest.launcher_windows_path, $manifest.state_windows_path, $manifest.log_windows_path, $manifest.working_directory_windows)) {
    if (-not [IO.Path]::IsPathRooted($windowsPath) -or $windowsPath -match '[\x00\r\n]') { throw 'Invalid Windows path.' }
}
if (-not [string]::Equals([IO.Path]::GetFullPath($manifest.parent_windows_path), [IO.Path]::GetFullPath($PSCommandPath), [StringComparison]::OrdinalIgnoreCase)) { throw 'Parent source path mismatch.' }
if (-not [string]::Equals([IO.Path]::GetFullPath($manifest.launcher_windows_path), [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($manifest.bootstrap_windows_path)) 'launch_hidden.ps1')), [StringComparison]::OrdinalIgnoreCase)) { throw 'Bootstrap bridge path mismatch.' }
if ([double]::IsNaN($manifest.hard_deadline_wall) -or [double]::IsInfinity($manifest.hard_deadline_wall)) { throw 'Invalid fixed deadline.' }
$fixedUtc = [DateTimeOffset]::Parse($manifest.hard_deadline_utc, [Globalization.CultureInfo]::InvariantCulture).ToUnixTimeMilliseconds() / 1000.0
if ([Math]::Abs($fixedUtc - [double] $manifest.hard_deadline_wall) -gt 0.001) { throw 'Deadline representations disagree.' }
Assert-Pins
$configSnapshotText = Get-Content -LiteralPath $manifest.config_snapshot_windows_path -Raw -Encoding UTF8
$configSnapshot = $configSnapshotText | ConvertFrom-Json
# Windows PowerShell JSON conversion can round a fractional epoch by one ULP.
# Forward the exact numeric token from the hash-pinned Linux configuration;
# the Python sentinel still requires exact equality with its own config parse.
$deadlineMatches = [regex]::Matches($configSnapshotText, '"hard_deadline_wall"\s*:\s*(?<value>[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)')
if ($deadlineMatches.Count -ne 1) { throw 'Missing or ambiguous fixed deadline token.' }
$deadlineArgument = $deadlineMatches[0].Groups['value'].Value
if ([double] $configSnapshot.hard_deadline_wall -ne [double] $manifest.hard_deadline_wall) { throw 'Config and manifest caps disagree.' }

$mutexName = 'Global\ShardsTrainingRecovery-' + $windowsSid + '-' + $manifest.campaign_id
$mutex = New-Object Threading.Mutex($false, $mutexName)
$held = $false
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { return } # The already-running parent owns this campaign.
    # Root creates the initial file exactly once before launch. Loss/corruption
    # of persistent state cannot reset a retry budget.
    $state = Get-Content -LiteralPath $manifest.state_windows_path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($state.schema -cne 'shards-windows-watchdog-state-v1' -or $state.manifest_sha256 -cne $ManifestSha256 -or $state.runner_launch_attempts -notin @(0,1,2,3) -or $state.transport_failures -notin @(0,1,2,3) -or $state.terminal_latched -isnot [bool] -or $state.unsafe -isnot [bool]) { throw 'Invalid persistent parent state.' }
    if ($state.terminal_latched) { Remove-OwnedStartup; return }
    Log-Event 'parent_start' @{ mutex = $mutexName; fixed_deadline = $manifest.hard_deadline_utc; adopted_only_initially = $true }
    while (-not $state.terminal_latched) {
        $result = Run-Wsl 'sentinel'
        $protocol = $result.protocol
        if ($result.exit_code -eq 20) { Latch-Terminal 'Sentinel declared unsafe/expired/unrecoverable.' $true; break }
        $validProtocol = ($null -ne $protocol -and $protocol.config_sha256 -ceq $manifest.config_sha256 -and [double] $protocol.hard_deadline_wall -eq [double] $manifest.hard_deadline_wall)
        if ($result.exit_code -eq 0 -and $validProtocol -and $protocol.disposition -ceq 'terminal') {
            Latch-Terminal ('sentinel: ' + $protocol.reason) $false; break
        }
        if ($result.exit_code -eq 10 -and $validProtocol -and $protocol.disposition -ceq 'retry') {
            if ((Utc-Now) -ge [double] $manifest.hard_deadline_wall) { Latch-Terminal 'Fixed training deadline reached; no fresh runner.' $true; break }
            if ($state.runner_launch_attempts -ge $manifest.max_runner_launch_attempts) { Latch-Terminal 'Windows runner launch budget exhausted.' $true; break }
            $state.runner_launch_attempts += 1
            Save-State # Charge before spawn; a parent crash never restores it.
            $null = Run-Wsl 'runner'
            # Re-enter through the sentinel, which validates STOP, terminal,
            # ownership and evaluation. Runner exit alone cannot rearm a run.
        } else {
            if ($state.transport_failures -ge $manifest.max_transport_failures) { Latch-Terminal 'Windows/WSL transport checks exhausted.' $true; break }
            $state.transport_failures += 1; Save-State
            if ($state.transport_failures -ge $manifest.max_transport_failures) { Latch-Terminal 'Windows/WSL transport checks exhausted.' $true; break }
            Log-Event 'transport_retry_readonly' @{ failures = $state.transport_failures }
        }
        Start-Sleep -Seconds $manifest.retry_seconds
    }
} catch {
    if ($held -and $null -ne (Get-Variable state -ErrorAction SilentlyContinue)) {
        try { Latch-Terminal ('Parent guard failed: ' + $_.Exception.Message) $true } catch { }
    }
    throw
} finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
