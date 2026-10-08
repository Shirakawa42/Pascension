# Create-only, current-user Startup shortcut for the pinned Windows parent.
# No Task Scheduler, registry, services, credentials, or training changes.
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Install','Status','Uninstall')] [string] $Mode = 'Prepare',
    [Parameter(Mandatory = $true)] [string] $ManifestPath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[a-fA-F0-9]{64}$')] [string] $ManifestSha256,
    [Parameter(Mandatory = $true)] [string] $ReportPath,
    [switch] $Start
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($env:OS -ne 'Windows_NT') { throw 'Run with Windows PowerShell.' }
if ($Start -and $Mode -ne 'Install') { throw '-Start requires Install.' }
function Sha([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Quote-Arg([string] $Value) {
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    return '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
}
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$ManifestSha256 = $ManifestSha256.ToLowerInvariant()
if ((Sha $ManifestPath) -cne $ManifestSha256) { throw 'Manifest SHA mismatch.' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -cne 'shards-windows-watchdog-v1' -or $manifest.campaign_id -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$' -or $manifest.windows_user_sid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Manifest campaign/user mismatch.' }
foreach ($pin in @(
    @($manifest.parent_windows_path, $manifest.parent_sha256),
    @($manifest.runner_windows_path, $manifest.runner_sha256),
    @($manifest.sentinel_windows_path, $manifest.sentinel_sha256),
    @($manifest.config_snapshot_windows_path, $manifest.config_sha256),
    @($manifest.bootstrap_windows_path, $manifest.bootstrap_sha256),
    @($manifest.launcher_windows_path, $manifest.launcher_sha256)
)) { if ((Sha $pin[0]) -cne $pin[1]) { throw ('Pinned source/config changed: ' + $pin[0]) } }
if (-not [string]::Equals([IO.Path]::GetFullPath($manifest.launcher_windows_path), [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($manifest.bootstrap_windows_path)) 'launch_hidden.ps1')), [StringComparison]::OrdinalIgnoreCase)) { throw 'Bootstrap bridge path mismatch.' }
$state = Get-Content -LiteralPath $manifest.state_windows_path -Raw -Encoding UTF8 | ConvertFrom-Json
if ($state.schema -cne 'shards-windows-watchdog-state-v1' -or $state.manifest_sha256 -cne $ManifestSha256) { throw 'Persistent state binding mismatch.' }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup ('ShardsTrainingRecovery-' + $manifest.campaign_id + '.lnk')
$target = Join-Path $env:WINDIR 'System32\wscript.exe'
$parentPayload = ConvertTo-Json -InputObject @('-ManifestPath', $ManifestPath, '-ManifestSha256', $ManifestSha256) -Compress
$parentEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($parentPayload))
$arguments = (@('//B', '//Nologo', $manifest.bootstrap_windows_path, $manifest.parent_windows_path, $parentEncoded, ($manifest.log_windows_path + '.bootstrap.json')) | ForEach-Object { Quote-Arg $_ }) -join ' '
$description = 'Shards training recovery ' + $manifest.campaign_id + '; manifest_sha256=' + $ManifestSha256
$shell = New-Object -ComObject WScript.Shell
function Check-Shortcut([string] $Path) {
    $link = $shell.CreateShortcut($Path)
    if (-not [string]::Equals($link.TargetPath, $target, [StringComparison]::OrdinalIgnoreCase) -or $link.Arguments -cne $arguments -or $link.Description -cne $description -or -not [string]::Equals($link.WorkingDirectory, $manifest.working_directory_windows, [StringComparison]::OrdinalIgnoreCase)) { throw 'Foreign or changed Startup shortcut; no modification allowed.' }
}
$exists = Test-Path -LiteralPath $shortcutPath
if ($Mode -eq 'Install') {
    if ($exists) { throw 'Refusing to overwrite an existing Startup shortcut.' }
    if ($state.terminal_latched) { throw 'Terminal/unsafe parent state cannot be rearmed.' }
    $temporary = Join-Path $startup ('ShardsTrainingRecovery-' + [Guid]::NewGuid().ToString('N') + '.lnk')
    try {
        $link = $shell.CreateShortcut($temporary)
        $link.TargetPath = $target; $link.Arguments = $arguments
        $link.Description = $description; $link.WorkingDirectory = $manifest.working_directory_windows
        # The GUI wscript target suppresses the bridge window (Run(...,0,...));
        # launch_hidden then creates the real parent with no console at all.
        $link.WindowStyle = 1; $link.Save()
        Check-Shortcut $temporary
        # File.Move is create-only, including races; no -Force replacement.
        [IO.File]::Move($temporary, $shortcutPath)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    $exists = $true
}
if ($exists -and $Mode -in @('Install','Status','Uninstall')) { Check-Shortcut $shortcutPath }
if ($exists -and $Mode -eq 'Uninstall') { Remove-Item -LiteralPath $shortcutPath; $exists = $false }
$report = [ordered] @{
    schema_version = 1; mode = $Mode; campaign_id = $manifest.campaign_id
    manifest_sha256 = $ManifestSha256; windows_user_sid = $manifest.windows_user_sid
    shortcut_path = $shortcutPath; installed = [bool] $exists; verified = ($exists -or $Mode -in @('Prepare','Uninstall'))
    target = $target; arguments = $arguments; description = $description
    fixed_training_deadline = $manifest.hard_deadline_utc
    startup_coverage = 'Current Windows login and future logins; no before-login startup.'
    task_scheduler_used = $false; registry_or_service_changed = $false
    desktop_windows_created = $false; bootstrap_target = 'GUI wscript.exe'; parent_create_no_window = $true
}
if ($Start) {
    $parent = New-Object Diagnostics.Process
    try {
        $parent.StartInfo = New-Object Diagnostics.ProcessStartInfo
        $parent.StartInfo.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $parent.StartInfo.Arguments = (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $manifest.launcher_windows_path, '-ScriptPath', $manifest.parent_windows_path, '-ArgumentsBase64', $parentEncoded, '-ReportPath', ($manifest.log_windows_path + '.bootstrap.json')) | ForEach-Object { Quote-Arg $_ }) -join ' '
        $parent.StartInfo.WorkingDirectory = $manifest.working_directory_windows
        $parent.StartInfo.UseShellExecute = $false
        $parent.StartInfo.CreateNoWindow = $true
        if (-not $parent.Start()) { throw 'Background parent bridge did not start.' }
        $report['bridge_pid'] = $parent.Id; $report['bridge_start_utc'] = $parent.StartTime.ToUniversalTime().ToString('o')
        $report['parent_process_report'] = $manifest.log_windows_path + '.bootstrap.json'
    } finally { $parent.Dispose() }
}
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), $json, (New-Object Text.UTF8Encoding($false)))
Write-Output $json
