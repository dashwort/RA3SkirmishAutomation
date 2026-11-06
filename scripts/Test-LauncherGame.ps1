[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Profile,
    [string]$Preset,
    [ValidateRange(1,300)][int]$TestSeconds=10,
    [ValidateRange(10,300)][int]$SetupTimeout=120,
    [ValidateRange(10,300)][int]$LoadTimeout=120,
    [switch]$IsolateIncompatibleMaps,
    [switch]$DryRun,
    [string]$OutputRoot,
    [string]$Launcher,
    [string]$GameRoot,
    [string]$BaseSkuDef,
    [string]$ProfileRoot,
    [string]$MapRoot,
    [string]$RuntimeRoot,
    [ValidateRange(800,7680)][int]$Width=1280,
    [ValidateRange(600,4320)][int]$Height=720
)
$ErrorActionPreference='Stop'
# Native stderr contains progress JSON, including for a passing test.
$PSNativeCommandUseErrorActionPreference=$false
$repo=Split-Path -Parent $PSScriptRoot
if(-not $Launcher){$Launcher=Join-Path $repo 'artifacts\releases\RA3SkirmishAutomation\RA3SkirmishAutomation.exe'}
$Launcher=[IO.Path]::GetFullPath($Launcher)
if(-not (Test-Path -LiteralPath $Launcher -PathType Leaf)){throw "Published launcher not found: $Launcher. See docs/README-Launcher.md for the publish command."}
if(-not $OutputRoot){$OutputRoot=Join-Path $repo '.build\launcher-tests'}
$run=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('run-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
$launchArgs=@('test-game','--profile',$Profile,'--test-seconds',"$TestSeconds",'--setup-timeout',"$SetupTimeout",'--load-timeout',"$LoadTimeout",'--width',"$Width",'--height',"$Height",'--evidence-root',(Join-Path $run 'session'))
foreach($pair in @(@('preset',$Preset),@('game-root',$GameRoot),@('base-skudef',$BaseSkuDef),@('profile-root',$ProfileRoot),@('map-root',$MapRoot),@('runtime-root',$RuntimeRoot))){
    if($pair[1]){$launchArgs+=('--'+$pair[0]);$launchArgs+=[IO.Path]::GetFullPath($pair[1])}
}
if($BaseSkuDef){$launchArgs+=@('--mod-directory',(Split-Path -Parent ([IO.Path]::GetFullPath($BaseSkuDef))))}
if($IsolateIncompatibleMaps){$launchArgs+='--isolate-incompatible-maps'}
if($DryRun){$launchArgs+='--dry-run'}
@{executable=$Launcher;arguments=$launchArgs;startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'command.json') -Encoding utf8
$stdout=Join-Path $run 'stdout.json';$stderr=Join-Path $run 'progress.jsonl'
& $Launcher @launchArgs 1> $stdout 2> $stderr
$nativeExit=$LASTEXITCODE
try {
    $result=Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
    if($null -eq $result.exitCode -or [int]$result.exitCode -ne $nativeExit){throw 'CLI exit code and JSON result disagree.'}
    $result | Add-Member -NotePropertyName codexRunDirectory -NotePropertyValue $run
    $result | ConvertTo-Json -Depth 32
} catch {
    @{schemaVersion=1;status='test-runner-failed';exitCode=1;message=$_.Exception.Message;nativeExitCode=$nativeExit;codexRunDirectory=$run} | ConvertTo-Json -Compress
    exit 1
}
exit $nativeExit
