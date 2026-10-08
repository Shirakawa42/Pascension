param([int]$Seconds=45, [string]$OutputPath='F:\Unity\projects\pascension\Tools\Diagnostics\window-trace.json')
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowProbe {
 [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
 [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
}
'@
function Describe-Process([uint32]$ProcessIdValue) {
 $p=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessIdValue" -ErrorAction SilentlyContinue
 if(!$p){ return @{pid=$ProcessIdValue; exited=$true} }
 # Only executable and script paths, never raw command-line arguments or environment.
 $scripts=@([regex]::Matches([string]$p.CommandLine,'(?i)[A-Za-z]:\\[^"\r\n]*?\.(?:py|ps1|cmd|bat)(?=["\s]|$)') | ForEach-Object {$_.Value})
 return @{pid=[int]$p.ProcessId;parent=[int]$p.ParentProcessId;name=$p.Name;executable=$p.ExecutablePath;scripts=$scripts}
}
$initial=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -match 'pascension|python|powershell|conhost|cmd.exe|nvidia-smi|ollama|wsl.exe|Unity.exe'} | ForEach-Object {Describe-Process $_.ProcessId})
$events=New-Object System.Collections.Generic.List[object]
$samples=New-Object System.Collections.Generic.List[object]
$seen=@{}; foreach($p in @(Get-Process)){ $seen[$p.Id]=$true }
$timer=[Diagnostics.Stopwatch]::StartNew()
while($timer.Elapsed.TotalSeconds -lt $Seconds){
  foreach($p in @(Get-Process)){
   if($seen.ContainsKey($p.Id)){continue}; $seen[$p.Id]=$true
   if($p.ProcessName -match 'pascension|python|powershell|conhost|cmd|nvidia-smi|ollama|wsl|terminal'){
    $e=Describe-Process $p.Id
    $chain=New-Object System.Collections.Generic.List[object];$parent=[uint32]$e.parent
    for($i=0;$i -lt 4 -and $parent -gt 0;$i++){ $item=Describe-Process $parent;$chain.Add($item);if(!$item.parent){break};$parent=[uint32]$item.parent }
    $events.Add(@{seconds=$timer.Elapsed.TotalSeconds;process=$e;ancestry=$chain.ToArray()})
   }
  }
  $foreground=[WindowProbe]::GetForegroundWindow();[uint32]$foregroundPid=0;[void][WindowProbe]::GetWindowThreadProcessId($foreground,[ref]$foregroundPid)
  foreach($p in @(Get-Process -Name pascension -ErrorAction SilentlyContinue)){
   $handle=$p.MainWindowHandle;[IntPtr]$reply=[IntPtr]::Zero
   $responding=$true
   if($handle -ne [IntPtr]::Zero){$responding=[WindowProbe]::SendMessageTimeout($handle,0,[IntPtr]::Zero,[IntPtr]::Zero,2,250,[ref]$reply) -ne [IntPtr]::Zero}
   $samples.Add(@{seconds=$timer.Elapsed.TotalSeconds;pid=$p.Id;window=[long]$handle;hung=[WindowProbe]::IsHungAppWindow($handle);wm_null_responding=$responding;foreground_pid=$foregroundPid;foreground=($foregroundPid -eq $p.Id);cpu_seconds=$p.CPU})
  }
  Start-Sleep -Milliseconds 100
 }

$result=@{utc=[DateTime]::UtcNow.ToString('o');duration_seconds=$timer.Elapsed.TotalSeconds;initial=$initial;starts=$events.ToArray();samples=$samples.ToArray()}
$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $OutputPath
@{output=$OutputPath;initial=$initial;starts=$events.ToArray();sample_count=$samples.Count;unresponsive_samples=@($samples | Where-Object {!$_.wm_null_responding -or $_.hung}).Count} | ConvertTo-Json -Depth 8
