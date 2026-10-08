param([Parameter(Mandatory=$true)][string]$ProjectDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Version="1.0.14")
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Build output must be new'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# Windows-local copy avoids slow per-file WSL filesystem round trips.
$copy=New-Object Diagnostics.ProcessStartInfo
$copy.FileName=Join-Path $env:WINDIR 'System32\robocopy.exe'
$copy.UseShellExecute=$false
$copy.CreateNoWindow=$true
$copy.Arguments='"F:\Unity\projects\pascension\Library\PackageCache" "'+(Join-Path $ProjectDirectory 'Library\PackageCache')+'" /E /MT:6 /R:1 /W:1 /NFL /NDL /NP /LOG:"'+(Join-Path $OutputDirectory 'package-copy.log')+'"'
$copyProcess=[Diagnostics.Process]::Start($copy)
$copyProcess.WaitForExit()
if($copyProcess.ExitCode -ge 8){throw 'Package cache copy failed'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName='F:\Unity\editors\6000.3.7f1\Editor\Unity.exe'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.Arguments='-batchmode -nographics -quit -projectPath "'+$ProjectDirectory+'" -buildTarget StandaloneWindows64 -executeMethod Pascension.Editor.CiBuild.Build -customBuildPath "'+(Join-Path $OutputDirectory 'pascension.exe')+'" -buildVersion '+$Version+' -job-worker-count 6 -logFile "'+(Join-Path $OutputDirectory 'unity-build.log')+'"'
$p=[Diagnostics.Process]::Start($start)
$p.ProcessorAffinity=[IntPtr]0x555
$p.WaitForExit()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-status.json'),(@{exit_code=$p.ExitCode;hidden=$true;project=$ProjectDirectory;output=$OutputDirectory}|ConvertTo-Json))
exit $p.ExitCode
