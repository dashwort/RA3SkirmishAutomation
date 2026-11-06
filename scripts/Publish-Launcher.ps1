param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build\cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\releases\RA3SkirmishLauncher'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publishRoot = Join-Path $projectRoot ('.build\launcher-publish\' + [Guid]::NewGuid().ToString('N'))
$uiOutput = Join-Path $publishRoot 'ui'
$cliOutput = Join-Path $publishRoot 'backend'

& dotnet publish (Join-Path $projectRoot 'tools\RA3SkirmishLauncher') -c $Configuration -r win-x64 --self-contained true -o $uiOutput
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
& dotnet publish (Join-Path $projectRoot 'tools\EpicWar.Automation') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $cliOutput
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }

foreach ($source in @($uiOutput, $cliOutput)) {
    Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($source, $_.FullName)
        $destination = Join-Path $OutputDirectory $relative
        # Named presets and default.ini belong to the user after first delivery.
        if ($relative.StartsWith('Presets\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $destination)) {
            return
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

$sageSourceRoot = Join-Path $projectRoot 'third_party\SAGE-Unlocked\v1.9.5'
$sageChecksumFile = Join-Path $sageSourceRoot 'SHA256SUMS.txt'
if (!(Test-Path -LiteralPath $sageChecksumFile)) { throw 'Pinned SAGE checksum file is missing.' }
foreach ($line in Get-Content -LiteralPath $sageChecksumFile) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw "Invalid SAGE checksum entry: $line" }
    $expectedHash = $Matches[1]
    $relative = $Matches[2].Replace('/', '\')
    $sourceFile = Join-Path $sageSourceRoot $relative
    if (!(Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Pinned SAGE file is missing: $relative" }
    $actualHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "Pinned SAGE file checksum changed: $relative" }
}

$sageRuntimeOutput = Join-Path $OutputDirectory 'dependencies\SAGE-Unlocked\v1.9.5\RA3'
$sageNoticeOutput = Join-Path $OutputDirectory 'licenses\SAGE-Unlocked\v1.9.5'
[IO.Directory]::CreateDirectory($sageRuntimeOutput) | Out-Null
[IO.Directory]::CreateDirectory($sageNoticeOutput) | Out-Null
foreach ($name in @('d3d9.dll', 'CnCFpsUnlocker.dll', 'SAGEUnlocked.ini')) {
    Copy-Item -LiteralPath (Join-Path $sageSourceRoot "RA3\$name") -Destination (Join-Path $sageRuntimeOutput $name) -Force
}
foreach ($name in @('LICENSE.txt', 'README.txt', 'UPSTREAM.md', 'SHA256SUMS.txt', 'SAGE-Unlocked-source-v1.9.5.zip')) {
    Copy-Item -LiteralPath (Join-Path $sageSourceRoot $name) -Destination (Join-Path $sageNoticeOutput $name) -Force
}

foreach ($required in @('RA3SkirmishLauncher.exe', 'RA3SkirmishAutomation.exe', 'RA3SkirmishLauncher.pri', 'App.xbf', 'MainWindow.xbf',
        'Microsoft.UI.Xaml.dll', 'Presets\Allies.ini', 'Presets\Japan.ini', 'Presets\Soviet.ini', 'dependencies\x86\d3dx9_35.dll',
        'dependencies\SAGE-Unlocked\v1.9.5\RA3\d3d9.dll', 'dependencies\SAGE-Unlocked\v1.9.5\RA3\CnCFpsUnlocker.dll',
        'dependencies\SAGE-Unlocked\v1.9.5\RA3\SAGEUnlocked.ini', 'licenses\SAGE-Unlocked\v1.9.5\LICENSE.txt',
        'licenses\SAGE-Unlocked\v1.9.5\SAGE-Unlocked-source-v1.9.5.zip', 'licenses\SAGE-Unlocked\v1.9.5\SHA256SUMS.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Published dependency is missing: $required" }
}

$files = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object Name -NE 'release.json' | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName)
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'RA3 Skirmish Launcher'
    configuration = $Configuration
    architecture = 'win-x64'
    runtimeVerified = $false
    acceptance = 'awaiting-user-verification'
    builtAtUtc = [DateTime]::UtcNow.ToString('O')
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release.json') -Encoding utf8
Write-Output (Join-Path $OutputDirectory 'RA3SkirmishLauncher.exe')
