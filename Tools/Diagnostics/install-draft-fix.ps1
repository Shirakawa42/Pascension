param([int]$WaitSeconds=900,[string]$SourcePath='F:\Unity\projects\pascension\Builds\WindowsDraftFix',[string]$Revision='draft2')
$ErrorActionPreference='Stop'
$source=$SourcePath
if($Revision -notmatch '^[a-z0-9-]+$'){throw 'Invalid local build revision.'}
$target='E:\Bureau\pascension-windows-v1.0.4'
$staged="E:\Bureau\pascension-windows-v1.0.4-$Revision-staged"
$backup="E:\Bureau\pascension-windows-v1.0.4-before-$Revision"
$status=if($Revision -eq "draft2"){ "F:\Unity\projects\pascension\Tools\Diagnostics\draft-install-status.json" }else{ "F:\Unity\projects\pascension\Tools\Diagnostics\$Revision-install-status.json" }
function Status([string]$phase,[string]$detail){@{phase=$phase;detail=$detail;utc=[DateTime]::UtcNow.ToString('o');target=$target;backup=$backup}|ConvertTo-Json|Set-Content -Encoding UTF8 $status}
try {
 if(Test-Path $backup){throw 'Backup already exists; refusing to overwrite it.'}
 if(Test-Path $staged){throw 'Staging directory already exists; refusing to overwrite it.'}
 if(!(Test-Path "$source\pascension_Data\Managed\Shards.AI.dll")){throw 'Verified build is missing.'}
 Status 'staging' 'Copying verified build to the install volume.'
 Copy-Item -LiteralPath $source -Destination $staged -Recurse
 foreach($relative in @('pascension.exe','UnityPlayer.dll','pascension_Data\Managed\Shards.AI.dll','pascension_Data\Managed\Pascension.Game.dll')){
  if((Get-FileHash "$source\$relative").Hash -ne (Get-FileHash "$staged\$relative").Hash){throw "Staging hash mismatch: $relative"}
 }
 Status 'waiting_for_game_exit' 'Close the running installed game normally to apply the prepared update. No process will be terminated.'
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while(@(Get-Process -Name pascension -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq "$target\pascension.exe"}).Count -gt 0){
  if($timer.Elapsed.TotalSeconds -ge $WaitSeconds){Status 'ready_not_installed' 'Game remains open. Verified staging retained; installed game untouched.';exit 0}
  Start-Sleep -Seconds 1
 }
 # Move complete directories, retaining the entire old install for rollback.
 Move-Item -LiteralPath $target -Destination $backup
 try { Move-Item -LiteralPath $staged -Destination $target }
 catch { Move-Item -LiteralPath $backup -Destination $target;throw }
 Status 'installed' "Verified $Revision build installed. Previous complete install retained in backup."
}catch{Status 'failed' $_.Exception.Message;throw}
