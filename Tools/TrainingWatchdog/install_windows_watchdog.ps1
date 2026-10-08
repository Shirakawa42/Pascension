# Windows outer watchdog for a single, finite Shards training campaign.
# Prepare validates/exports the task without registering or starting anything.
# Install is create-only. Status and Unregister require the same exact inputs.
# The Linux runner owns checkpoints, duplicate-process locks and training budgets.
# InteractiveToken covers an existing login and the next login, not pre-login boot.
# Official behavior: https://learn.microsoft.com/en-us/windows/wsl/systemd
# https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertask
# https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-deleteexpiredtaskafter
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Install', 'Status', 'Unregister')]
    [string] $Mode = 'Prepare',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$')]
    [string] $CampaignId,
    [Parameter(Mandatory = $true)] [string] $ConfigLinuxPath,
    [Parameter(Mandatory = $true)] [string] $DeadlineUtc,
    [string] $Distribution = 'Ubuntu-24.04',
    [ValidatePattern('^[a-z_][a-z0-9_-]{0,31}$')]
    [string] $LinuxUser = 'lva',
    [string] $PythonLinuxPath = '/home/lva/.venvs/shards-preflight/bin/python',
    [string] $RunnerLinuxPath = '/mnt/f/Unity/projects/pascension/Tools/TrainingWatchdog/runner.py',
    [string] $StartUtc = '',
    [string] $ReportPath = '',
    [switch] $Start,
    [switch] $AllowDisabled,
    [switch] $VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this installer with Windows PowerShell.' }
if ($Start -and $Mode -ne 'Install') { throw '-Start is only supported with -Mode Install.' }
if ($AllowDisabled -and $Mode -notin @('Status', 'Unregister')) { throw '-AllowDisabled is only supported with Status or Unregister.' }
if ($VerifyOnly -and $Mode -ne 'Unregister') { throw '-VerifyOnly is only supported with Unregister.' }
foreach ($path in @($ConfigLinuxPath, $PythonLinuxPath, $RunnerLinuxPath)) {
    if (-not $path.StartsWith('/') -or $path -match '[\x00\r\n]') {
        throw 'Linux paths must be absolute and cannot contain NUL or line breaks.'
    }
}
if ([string]::IsNullOrWhiteSpace($Distribution) -or $Distribution -match '[\x00\r\n]') {
    throw 'Distribution must be a nonempty single-line WSL distribution name.'
}
if ($LinuxUser -eq 'root') { throw 'Use the Linux account that owns the training campaign, not root.' }

function Parse-Utc([string] $Text) {
    if ($Text -notmatch '(Z|[+-]00:00)$') { throw 'UTC timestamps must have an explicit Z or +00:00 suffix.' }
    return [DateTimeOffset]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Format-Utc([DateTimeOffset] $Value) {
    return $Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function Parse-SchedulerTime([string] $Text) {
    # Registered XML is normalized by Windows into the local UTC offset.
    if ($Text -notmatch '(Z|[+-][0-9]{2}:[0-9]{2})$') { throw 'Scheduler timestamp must have an explicit time-zone offset.' }
    return [DateTimeOffset]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Bool-Text([bool] $Value) { return $Value.ToString().ToLowerInvariant() }

function Quote-WindowsArgument([string] $Value) {
    # WSL's own option parser requires bare flags: quoted "-d" is treated as a
    # Linux command, even though CommandLineToArgvW decodes it as an option.
    # Quote only tokens that require grouping/escaping, then use CRT escaping.
    # There is no cmd.exe, PowerShell expression, bash, or shell evaluation here.
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Xml-Escape([string] $Value) { return [Security.SecurityElement]::Escape($Value) }

function New-NamespaceManager([xml] $Document) {
    $manager = New-Object Xml.XmlNamespaceManager($Document.NameTable)
    $manager.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    return ,$manager
}

function Node-Text([xml] $Document, [string] $Path) {
    $node = $Document.SelectSingleNode($Path, (New-NamespaceManager $Document))
    if ($null -eq $node) { return $null }
    return [string] $node.InnerText
}

function Get-Task($Folder, [string] $Name) {
    foreach ($candidate in $Folder.GetTasks(1)) {
        if ([string]::Equals($candidate.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) { return $candidate }
    }
    return $null
}

function Verify-Task([xml] $Document, [string] $ExpectedStart) {
    $checks = [ordered] @{}
    # Use the native definition to obtain effective schema defaults. Exported
    # tasks omit many default values; missing XML is not a missing constraint.
    $definition = $service.NewTask(0)
    $definition.XmlText = $Document.OuterXml
    $settings = $definition.Settings
    $effective = @{
        '/t:Task/t:Principals/t:Principal/t:RunLevel' = $(if ($definition.Principal.RunLevel -eq 0) { 'LeastPrivilege' } else { 'HighestAvailable' })
        '/t:Task/t:Settings/t:DisallowStartIfOnBatteries' = (Bool-Text $settings.DisallowStartIfOnBatteries)
        '/t:Task/t:Settings/t:StopIfGoingOnBatteries' = (Bool-Text $settings.StopIfGoingOnBatteries)
        '/t:Task/t:Settings/t:AllowHardTerminate' = (Bool-Text $settings.AllowHardTerminate)
        '/t:Task/t:Settings/t:StartWhenAvailable' = (Bool-Text $settings.StartWhenAvailable)
        '/t:Task/t:Settings/t:RunOnlyIfNetworkAvailable' = (Bool-Text $settings.RunOnlyIfNetworkAvailable)
        '/t:Task/t:Settings/t:IdleSettings/t:StopOnIdleEnd' = (Bool-Text $settings.IdleSettings.StopOnIdleEnd)
        '/t:Task/t:Settings/t:IdleSettings/t:RestartOnIdle' = (Bool-Text $settings.IdleSettings.RestartOnIdle)
        '/t:Task/t:Settings/t:RunOnlyIfIdle' = (Bool-Text $settings.RunOnlyIfIdle)
        '/t:Task/t:Settings/t:AllowStartOnDemand' = (Bool-Text $settings.AllowDemandStart)
        '/t:Task/t:Settings/t:Enabled' = (Bool-Text $settings.Enabled)
        '/t:Task/t:Settings/t:Hidden' = (Bool-Text $settings.Hidden)
        '/t:Task/t:Settings/t:WakeToRun' = (Bool-Text $settings.WakeToRun)
    }
    foreach ($trigger in $definition.Triggers) {
        if ($trigger.Type -eq 9) { $effective['/t:Task/t:Triggers/t:LogonTrigger/t:Enabled'] = Bool-Text $trigger.Enabled }
        if ($trigger.Type -eq 1) {
            $effective['/t:Task/t:Triggers/t:TimeTrigger/t:Enabled'] = Bool-Text $trigger.Enabled
            $effective['/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:StopAtDurationEnd'] = Bool-Text $trigger.Repetition.StopAtDurationEnd
        }
    }
    $expect = [ordered] @{
        '/t:Task/t:RegistrationInfo/t:Description' = $description
        '/t:Task/t:Principals/t:Principal/t:UserId' = $userSid
        '/t:Task/t:Principals/t:Principal/t:LogonType' = 'InteractiveToken'
        '/t:Task/t:Principals/t:Principal/t:RunLevel' = 'LeastPrivilege'
        '/t:Task/t:Triggers/t:LogonTrigger/t:UserId' = $userSid
        '/t:Task/t:Triggers/t:LogonTrigger/t:Enabled' = 'true'
        '/t:Task/t:Triggers/t:TimeTrigger/t:Enabled' = 'true'
        '/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Interval' = 'PT1M'
        '/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:StopAtDurationEnd' = 'false'
        '/t:Task/t:Settings/t:MultipleInstancesPolicy' = 'IgnoreNew'
        '/t:Task/t:Settings/t:DisallowStartIfOnBatteries' = 'false'
        '/t:Task/t:Settings/t:StopIfGoingOnBatteries' = 'false'
        '/t:Task/t:Settings/t:AllowHardTerminate' = 'true'
        '/t:Task/t:Settings/t:StartWhenAvailable' = 'true'
        '/t:Task/t:Settings/t:RunOnlyIfNetworkAvailable' = 'false'
        '/t:Task/t:Settings/t:IdleSettings/t:StopOnIdleEnd' = 'false'
        '/t:Task/t:Settings/t:IdleSettings/t:RestartOnIdle' = 'false'
        '/t:Task/t:Settings/t:RunOnlyIfIdle' = 'false'
        '/t:Task/t:Settings/t:AllowStartOnDemand' = 'true'
        '/t:Task/t:Settings/t:Enabled' = 'true'
        '/t:Task/t:Settings/t:Hidden' = 'false'
        '/t:Task/t:Settings/t:WakeToRun' = 'false'
        '/t:Task/t:Settings/t:ExecutionTimeLimit' = 'PT24H'
        '/t:Task/t:Settings/t:DeleteExpiredTaskAfter' = 'PT12H'
        '/t:Task/t:Settings/t:RestartOnFailure/t:Interval' = 'PT1M'
        '/t:Task/t:Settings/t:RestartOnFailure/t:Count' = '3'
        '/t:Task/t:Actions/t:Exec/t:Command' = $windowsExecutable
        '/t:Task/t:Actions/t:Exec/t:Arguments' = $actionArguments
    }
    if ($AllowDisabled -and -not $settings.Enabled) { $expect['/t:Task/t:Settings/t:Enabled'] = 'false' }
    foreach ($entry in $expect.GetEnumerator()) {
        $raw = Node-Text $Document $entry.Key
        $actual = $raw
        if ($effective.ContainsKey($entry.Key)) { $actual = $effective[$entry.Key] }
        if ($entry.Key -in @('/t:Task/t:Principals/t:Principal/t:UserId', '/t:Task/t:Triggers/t:LogonTrigger/t:UserId')) {
            if ([string]::Equals($actual, $userName, [StringComparison]::OrdinalIgnoreCase)) { $actual = $userSid }
        }
        $passed = ($actual -ceq $entry.Value)
        if ($entry.Key -in @('/t:Task/t:Settings/t:ExecutionTimeLimit', '/t:Task/t:Settings/t:DeleteExpiredTaskAfter', '/t:Task/t:Settings/t:RestartOnFailure/t:Interval', '/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Interval')) {
            try { $passed = ([Xml.XmlConvert]::ToTimeSpan($actual) -eq [Xml.XmlConvert]::ToTimeSpan($entry.Value)) } catch { $passed = $false }
        }
        $checks[$entry.Key] = [ordered] @{ expected = $entry.Value; actual = $actual; raw_xml_value = $raw; passed = $passed }
    }
    $manager = New-NamespaceManager $Document
    foreach ($entry in @(
        @('/t:Task/t:Triggers/*', 2), @('/t:Task/t:Triggers/t:LogonTrigger', 1),
        @('/t:Task/t:Triggers/t:TimeTrigger', 1), @('/t:Task/t:Principals/*', 1),
        @('/t:Task/t:Actions/*', 1), @('/t:Task/t:Actions/t:Exec', 1),
        @('/t:Task/t:Triggers/t:LogonTrigger/t:Repetition', 0),
        @('/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Duration', 0),
        @('/t:Task/t:Actions/t:Exec/t:WorkingDirectory', 0)
    )) {
        $count = $Document.SelectNodes($entry[0], $manager).Count
        $checks['count:' + $entry[0]] = [ordered] @{ expected = $entry[1]; actual = $count; passed = ($count -eq $entry[1]) }
    }
    foreach ($kind in @('LogonTrigger', 'TimeTrigger')) {
        foreach ($pair in @(@('EndBoundary', $deadline), @('StartBoundary', (Parse-Utc $ExpectedStart)))) {
            $key = '/t:Task/t:Triggers/t:' + $kind + '/t:' + $pair[0]
            $actual = Node-Text $Document $key
            $passed = $false
            # Task Scheduler drops fractional seconds. Accept exactly the
            # requested instant or its whole-second floor, never a later time.
            $floor = $pair[1].AddTicks(-($pair[1].Ticks % [TimeSpan]::TicksPerSecond))
            try { $parsed = Parse-SchedulerTime $actual; $passed = ($parsed -eq $pair[1] -or $parsed -eq $floor) } catch { $passed = $false }
            $checks[$key] = [ordered] @{ expected = (Format-Utc $pair[1]); floor_expected = (Format-Utc $floor); actual = $actual; passed = $passed }
        }
    }
    return $checks
}

function Assert-Verified($Checks) {
    $failed = @($Checks.GetEnumerator() | Where-Object { -not $_.Value.passed })
    if ($failed.Count -ne 0) { throw ('Task readback differs from the requested definition: ' + (($failed | ForEach-Object { $_.Key }) -join ', ')) }
}

function Write-Report($Value) {
    $json = $Value | ConvertTo-Json -Depth 12
    $target = [IO.Path]::GetFullPath($ReportPath)
    $parent = [IO.Path]::GetDirectoryName($target)
    if (-not [IO.Directory]::Exists($parent)) { throw 'Report parent directory must already exist.' }
    $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $target -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    Write-Output $json
}

$deadline = Parse-Utc $DeadlineUtc
$now = [DateTimeOffset]::UtcNow
if ($Mode -in @('Prepare', 'Install')) {
    if ($deadline -le $now -or ($deadline - $now).TotalHours -gt 12) {
        throw 'The fixed deadline must be in the future and at most 12 hours away.'
    }
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$userName = $identity.Name
$userSid = $identity.User.Value
if ($userSid -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) {
    throw 'Run under the interactive Windows account that owns the WSL distribution, not a service account.'
}
$taskName = 'ShardsTrainingWatchdog-' + $CampaignId
if (-not $ReportPath) { $ReportPath = Join-Path $PSScriptRoot ($taskName + '-' + $Mode.ToLowerInvariant() + '.json') }
$description = 'Shards zero-depth campaign watchdog; campaign_id=' + $CampaignId + '; config=' + $ConfigLinuxPath + '; deadline_utc=' + (Format-Utc $deadline)
$windowsExecutable = Join-Path $env:WINDIR 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $windowsExecutable -PathType Leaf)) { throw 'wsl.exe was not found.' }
$argumentsVector = @('-d', $Distribution, '--user', $LinuxUser, '--exec', $PythonLinuxPath, $RunnerLinuxPath, '--config', $ConfigLinuxPath)
$actionArguments = ($argumentsVector | ForEach-Object { Quote-WindowsArgument $_ }) -join ' '
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$folder = $service.GetFolder('\')
$task = Get-Task $folder $taskName
if ($Mode -eq 'Install' -and $null -ne $task) { throw 'Refusing to overwrite an existing task. Use Status or unregister the exactly verified campaign task first.' }
if ($StartUtc) { $startBoundary = Format-Utc (Parse-Utc $StartUtc) }
elseif ($Mode -in @('Status', 'Unregister') -and $null -ne $task) {
    $startBoundary = Format-Utc (Parse-SchedulerTime (Node-Text ([xml] $task.Xml) '/t:Task/t:Triggers/t:TimeTrigger/t:StartBoundary'))
} elseif ($Mode -in @('Status', 'Unregister')) {
    # Expired/missing tasks remain inspectable after automatic deletion.
    $startBoundary = Format-Utc ($deadline.AddMinutes(-1))
} else { $startBoundary = Format-Utc ($now.AddMinutes(1)) }
if ((Parse-Utc $startBoundary) -ge $deadline) { throw 'Task start boundary must precede the fixed deadline.' }
$endBoundary = Format-Utc $deadline
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>$(Xml-Escape $userName)</Author><Description>$(Xml-Escape $description)</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger id="Logon"><StartBoundary>$startBoundary</StartBoundary><EndBoundary>$endBoundary</EndBoundary><Enabled>true</Enabled><UserId>$userSid</UserId></LogonTrigger>
    <TimeTrigger id="Health"><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>$startBoundary</StartBoundary><EndBoundary>$endBoundary</EndBoundary><Enabled>true</Enabled></TimeTrigger>
  </Triggers>
  <Principals><Principal id="Owner"><UserId>$userSid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT24H</ExecutionTimeLimit><DeleteExpiredTaskAfter>PT12H</DeleteExpiredTaskAfter>
    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Owner"><Exec><Command>$(Xml-Escape $windowsExecutable)</Command><Arguments>$(Xml-Escape $actionArguments)</Arguments></Exec></Actions>
</Task>
"@

# Read-only Status/Unregister verification never calls RegisterTask. Prepare and
# Install use an absent random name for native validation, protecting every
# existing task from the registration API even when only validation is intended.
$validationMethod = 'native TaskDefinition.XmlText and semantic readback'
if ($Mode -in @('Prepare', 'Install')) {
    $validationName = 'ShardsWatchdogValidation-' + [Guid]::NewGuid().ToString('N')
    if ($null -ne (Get-Task $folder $validationName)) { throw 'Validation name collision.' }
    # TASK_VALIDATE_ONLY = 1. This does not register or execute an action.
    $null = $folder.RegisterTask($validationName, $xml, 1, $userSid, $null, 3, $null)
    if ($null -ne (Get-Task $folder $validationName)) { throw 'Native validation unexpectedly registered a task.' }
    $validationMethod = 'TASK_VALIDATE_ONLY under an absent random name'
}
$checks = Verify-Task ([xml] $xml) $startBoundary
Assert-Verified $checks
$report = [ordered] @{
    schema_version = 1; mode = $Mode; recorded_utc = (Format-Utc ([DateTimeOffset]::UtcNow))
    task_name = $taskName; task_path = '\'; windows_user = $userName; windows_user_sid = $userSid
    distribution = $Distribution; linux_user = $LinuxUser; executable = $windowsExecutable; arguments = $actionArguments; argument_vector = $argumentsVector
    deadline_utc = $endBoundary; start_utc = $startBoundary; native_xml_validation = $true; validation_method = $validationMethod
    registered = ($null -ne $task); verified = $true; verification = $checks; definition_xml = $xml
    startup_coverage = 'While this Windows account is logged in, and after its next login; no pre-login boot coverage.'
    expiry = 'Both triggers end at the fixed training deadline; automatic task deletion twelve hours later.'
    runtime_limit = 'The outer task may run up to 24 hours to finish evaluation. The runner separately enforces the fixed training budget and deadline.'
    timestamp_precision = 'Registered trigger times may be rounded down to whole seconds; readback never accepts a later deadline.'
    allow_disabled = [bool] $AllowDisabled; verify_only = [bool] $VerifyOnly
}

if ($Mode -eq 'Install') {
    # TASK_CREATE = 2 (never UPDATE or CREATE_OR_UPDATE); no credentials stored.
    $task = $folder.RegisterTask($taskName, $xml, 2, $userSid, $null, 3, $null)
    $checks = Verify-Task ([xml] $task.Xml) $startBoundary
    $report.verification = $checks
    $report.definition_xml = [string] $task.Xml
    $report.registered = $true
    try { Assert-Verified $checks } catch {
        # Only the newly created campaign task is disabled on failed readback.
        # Prevent its time trigger from launching an unverified definition.
        $task.Enabled = $false
        $report.verified = $false
        $report['disabled_after_failed_readback'] = $true
        Write-Report $report
        throw
    }
    if ($Start) { $null = $task.Run($null) }
} elseif ($Mode -in @('Status', 'Unregister')) {
    if ($null -eq $task) {
        $report.verified = $false
        $report['missing'] = $true
    } else {
        $checks = Verify-Task ([xml] $task.Xml) $startBoundary
        $report.verification = $checks
        $report.definition_xml = [string] $task.Xml
        if ($Mode -eq 'Status') {
            $report.verified = (@($checks.GetEnumerator() | Where-Object { -not $_.Value.passed }).Count -eq 0)
        } else { Assert-Verified $checks }
        if ($Mode -eq 'Unregister') {
            if ($task.GetInstances(0).Count -ne 0) { throw 'Refusing to unregister a task with running instances.' }
            # Delete only this exactly verified task. Do not stop any training process.
            if (-not $VerifyOnly) {
                $folder.DeleteTask($taskName, 0)
                if ($null -ne (Get-Task $folder $taskName)) { throw 'Task remained registered after deletion.' }
                $report.registered = $false
                $report['unregistered'] = $true
                $task = $null
            }
        }
    }
}
if ($null -ne $task) {
    $report['task_state'] = [int] $task.State
    $report['last_task_result'] = [int64] $task.LastTaskResult
    $report['last_run_time'] = $task.LastRunTime.ToString('o')
    $report['next_run_time'] = $task.NextRunTime.ToString('o')
    $report['running_instances'] = $task.GetInstances(0).Count
}
Write-Report $report
