param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Target,[Parameter(Mandatory=$true)][string]$Backup)
$ErrorActionPreference='Stop'
$report=Get-Content -LiteralPath (Join-Path $Source 'package-verification.json') -Raw | ConvertFrom-Json
$build=Get-Content -LiteralPath (Join-Path $Source 'build-status.json') -Raw | ConvertFrom-Json
if(!$report.passed -or $report.checks.Count -ne 7 -or $build.exit_code -ne 0){throw 'A verified dual-AI build is required'}
if(@(Get-Process pascension -ErrorAction SilentlyContinue).Count){throw 'Game is running; leaving installation unchanged'}
if(!(Test-Path -LiteralPath $Target) -or (Test-Path -LiteralPath $Backup)){throw 'Missing target or backup already exists'}
$stage=$Target+'-nyou-staging'
if(Test-Path -LiteralPath $stage){throw 'Staging directory already exists'}
New-Item -ItemType Directory -Path $stage | Out-Null
# Stage the complete player while the existing installation remains playable.
$roots=@('pascension.exe','UnityPlayer.dll','UnityCrashHandler64.exe','pascension_Data','MonoBleedingEdge','D3D12')
foreach($name in $roots){$path=Join-Path $Source $name;if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination $stage -Recurse}}
$count=0
foreach($name in $roots){$path=Join-Path $Source $name;if(!(Test-Path -LiteralPath $path)){continue}
 foreach($f in @(Get-ChildItem -LiteralPath $path -File -Recurse)){
  $relative=$f.FullName.Substring($Source.TrimEnd('\').Length+1)
  if((Get-FileHash -LiteralPath $f.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stage $relative)).Hash){throw ('Staged file differs: '+$relative)}
  $count++
 }
}
if(!(Test-Path -LiteralPath (Join-Path $stage 'pascension.exe'))){throw 'Missing player'}
if(@(Get-Process pascension -ErrorAction SilentlyContinue).Count){throw 'Game started during staging; leaving installation unchanged'}
[IO.Directory]::Move($Target,$Backup)
try{[IO.Directory]::Move($stage,$Target)}catch{[IO.Directory]::Move($Backup,$Target);throw}
[IO.File]::WriteAllText((Join-Path $Source 'install-status.json'),(@{installed=$true;target=$Target;backup=$Backup;verified_files=$count;started_game=$false;both_ai_available=$true}|ConvertTo-Json))
