[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$BuildDir = '',
    [string]$RunRoot = '',
    [ValidateRange(1, 86400)]
    [int]$TimeoutSeconds = 180,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..\..'))
if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    $BuildDir = Join-Path $repoRoot 'UndoReplayLab\bin\Release\net9.0'
}
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $RunRoot = Join-Path $localAppData 'Temp\Sts2UndoReplayLab'
}

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required $Description was not found: $Path"
    }
}

function Write-Metadata([hashtable]$Metadata, [string]$Path) {
    $Metadata.updatedUtc = [DateTime]::UtcNow.ToString('o')
    $Metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Copy-ImmutableFile([string]$Source, [string]$Destination) {
    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    try {
        New-Item -ItemType HardLink -Path $Destination -Target $Source -ErrorAction Stop | Out-Null
        return 'hardlink'
    } catch {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        return 'copy'
    }
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    # Start-Process receives one command-line string when ArgumentList is joined.
    # Apply the Windows argv quoting rule: double backslashes before a quote and
    # at the end of a quoted argument.
    $quoted = $Value -replace '(\\*)"', '$1$1\"'
    $quoted = $quoted -replace '(\\+)$', '$1$1'
    return '"' + $quoted + '"'
}

function Get-RegularFilesNoReparse([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Get-FullPath $Root))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { Write-Output $item }
        }
    }
}

function Assert-NoReparseAncestor([string]$Path) {
    $current = Get-FullPath $Path
    while ($null -ne $current -and $current.Length -gt 0) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "RunRoot or an existing ancestor is a reparse point: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-JsonProperty([object]$Object, [string[]]$Names) {
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($null -ne $property) { return $property.Value }
    }
    return $null
}

function Test-ReportContract([object]$Report, [int]$ExpectedProcessId, [string]$ExpectedUserDir) {
    $hasFailure = Get-JsonProperty $Report @('HasFailure', 'hasFailure')
    $reportPid = Get-JsonProperty $Report @('pid', 'processId', 'process_id')
    $reportUserDir = Get-JsonProperty $Report @('userDataDir', 'user_data_dir')
    if ($null -eq $hasFailure -or [bool]$hasFailure -or $null -eq $reportPid -or [int]$reportPid -ne $ExpectedProcessId -or $null -eq $reportUserDir -or (Get-FullPath ([string]$reportUserDir)) -ne (Get-FullPath $ExpectedUserDir)) { return $false }

    $expectedPhaseNames = @('bootstrap', 'native-save-load', 'native-card-generation-rng-probe', 'record', 'potion-choice-replay', 'genetic-algorithm', 'model-state', 'replay-dispatch', 'normal-card-action-replay')
    $phaseDictionary = Get-JsonProperty $Report @('Phases', 'phases')
    if ($null -eq $phaseDictionary) { return $false }
    foreach ($name in $expectedPhaseNames) {
        $phaseProperty = $phaseDictionary.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($null -eq $phaseProperty) { return $false }
        $phase = $phaseProperty.Value
        $status = Get-JsonProperty $phase @('Status', 'status')
        if ($null -eq $status -or ([string]$status).ToUpperInvariant() -ne 'PASS') { return $false }
    }
    $recordedEvents = [int](Get-JsonProperty $Report @('RecordedEvents', 'recordedEvents'))
    $recordedActions = [int](Get-JsonProperty $Report @('RecordedActions', 'recordedActions'))
    $recordedChoices = [int](Get-JsonProperty $Report @('RecordedChoices', 'recordedChoices'))
    $replayedEvents = [int](Get-JsonProperty $Report @('ReplayedEvents', 'replayedEvents'))
    $replayedActions = [int](Get-JsonProperty $Report @('ReplayedActions', 'replayedActions'))
    $replayedChoices = [int](Get-JsonProperty $Report @('ReplayedChoices', 'replayedChoices'))
    if ($recordedEvents -le 0 -or $recordedActions -le 0 -or $recordedChoices -lt 2 -or $replayedEvents -ne $recordedEvents -or $replayedActions -ne $recordedActions -or $replayedChoices -ne $recordedChoices) { return $false }
    return $true
}

$GameDir = Get-FullPath $GameDir
$BuildDir = Get-FullPath $BuildDir
$requestedRunRoot = Get-FullPath $RunRoot
$manifestPath = Join-Path $repoRoot 'UndoReplayLab\UndoReplayLab.json'
$pluginDll = Join-Path $BuildDir 'UndoReplayLab.dll'
$gameManifest = Join-Path $GameDir 'release_info.json'
$isolatedGameDir = $null

Require-File $pluginDll 'UndoReplayLab build output'
Require-File $manifestPath 'UndoReplayLab manifest'
if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) { throw "Game directory was not found: $GameDir" }
if ($requestedRunRoot -eq $GameDir -or $requestedRunRoot.StartsWith($GameDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "RunRoot must be outside the actual game directory: $requestedRunRoot"
}
Assert-NoReparseAncestor $requestedRunRoot

$exe = Get-ChildItem -LiteralPath $GameDir -File -Filter '*.exe' |
    Where-Object { $_.Name -match '(?i)slay.*spire.*2|^SlayTheSpire2\.exe$' } |
    Select-Object -First 1
if ($null -eq $exe) { throw "Could not find the Slay the Spire 2 executable in $GameDir" }
$pck = Get-ChildItem -LiteralPath $GameDir -File -Filter '*.pck' | Select-Object -First 1
if ($null -eq $pck) { throw "Could not find the game .pck export in $GameDir" }
$dataDir = Join-Path $GameDir 'data_sts2_windows_x86_64'
if (-not (Test-Path -LiteralPath $dataDir -PathType Container)) { throw "Could not find the game data hierarchy: $dataDir" }

$existing = Get-CimInstance Win32_Process -Filter "Name = '$($exe.Name)'" -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    $pids = ($existing | ForEach-Object { $_.ProcessId }) -join ', '
    throw "A Slay the Spire 2 process is already running (PID $pids). Refusing to launch an isolated run."
}

$runId = [Guid]::NewGuid().ToString('N')
$RunRoot = Join-Path $requestedRunRoot $runId
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
$isolatedGameDir = Join-Path $RunRoot "game-$runId"
New-Item -ItemType Directory -Path $isolatedGameDir -Force | Out-Null
$modsDir = Join-Path $isolatedGameDir 'mods'
$pluginDir = Join-Path $modsDir 'UndoReplayLab'
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

$copyRecords = [Collections.Generic.List[object]]::new()
$rootFiles = @($exe.FullName, $pck.FullName) + @(Get-ChildItem -LiteralPath $GameDir -File | Where-Object {
    $_.Name -match '(?i)\.dll$' -or $_.Name -match '(?i)^release_info'
} | ForEach-Object { $_.FullName })
foreach ($source in ($rootFiles | Sort-Object -Unique)) {
    $relative = [IO.Path]::GetRelativePath($GameDir, $source)
    $destination = Join-Path $isolatedGameDir $relative
    $mode = Copy-ImmutableFile $source $destination
    $copyRecords.Add([pscustomobject]@{ source = $source; destination = $destination; mode = $mode })
}

Get-RegularFilesNoReparse $dataDir | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($GameDir, $_.FullName)
    $destination = Join-Path $isolatedGameDir $relative
    $mode = Copy-ImmutableFile $_.FullName $destination
    $copyRecords.Add([pscustomobject]@{ source = $_.FullName; destination = $destination; mode = $mode })
}

$pluginDestination = Join-Path $pluginDir 'UndoReplayLab.dll'
$manifestDestination = Join-Path $pluginDir 'UndoReplayLab.json'
Copy-Item -LiteralPath $pluginDll -Destination $pluginDestination -Force
Copy-Item -LiteralPath $manifestPath -Destination $manifestDestination -Force
$overridePath = Join-Path $isolatedGameDir 'override.cfg'
$override = @"
[application]
config/use_custom_user_dir=true
config/custom_user_dir_name="Sts2UndoReplayLab/$runId"
"@
Set-Content -LiteralPath $overridePath -Value $override -Encoding UTF8

# --force-steam=off selects the release NullPlatform. Its default local player
# ID is 1, so create fresh consent at user://default/1/settings.save.
$expectedUserDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Sts2UndoReplayLab\$runId"
$settingsDir = Join-Path $expectedUserDir 'default\1'
$settingsPath = Join-Path $settingsDir 'settings.save'
New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
$settings = '{"mod_settings":{"mods_enabled":true,"mod_list":[{"id":"UndoReplayLab","is_enabled":true,"source":"mods_directory"}]},"skip_intro_logo":true}'
Set-Content -LiteralPath $settingsPath -Value $settings -Encoding UTF8

$logPath = Join-Path $RunRoot 'godot.log'
$stdoutPath = Join-Path $RunRoot 'stdout.log'
$stderrPath = Join-Path $RunRoot 'stderr.log'
$resultPath = Join-Path $RunRoot 'result.json'
$metadataPath = Join-Path $RunRoot 'metadata.json'
$hashInputs = @($exe.FullName, $pck.FullName, $gameManifest, $pluginDll, $manifestPath) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
$versionHash = (Get-FileHash -LiteralPath $hashInputs -Algorithm SHA256 | ForEach-Object Hash) -join ''
$metadata = @{
    schema = 1
    status = 'prepared'
    runId = $runId
    createdUtc = [DateTime]::UtcNow.ToString('o')
    gameDir = $GameDir
    buildDir = $BuildDir
    sourcePlugin = $pluginDll
    sourceManifest = $manifestPath
    isolatedGameDir = $isolatedGameDir
    executable = (Join-Path $isolatedGameDir $exe.Name)
    overrideConfig = $overridePath
    expectedUserDataDir = $expectedUserDir
    runRoot = $RunRoot
    requestedRunRoot = $requestedRunRoot
    logFile = $logPath
    stdoutFile = $stdoutPath
    stderrFile = $stderrPath
    resultFile = $resultPath
    settingsFile = $settingsPath
    timeoutSeconds = $TimeoutSeconds
    versionHash = $versionHash
    copiedFiles = @($copyRecords)
    pid = $null
    exitCode = $null
}
Write-Metadata $metadata $metadataPath

if ($PrepareOnly) {
    Write-Output "Prepared isolated ReplayLab run: $RunRoot"
    Write-Output "Metadata: $metadataPath"
    exit 0
}

$arguments = @('--headless', '--audio-driver', 'Dummy', '--force-steam=off', '--log-file', $logPath, '--undo-replay-lab', "--undo-replay-lab-output=$resultPath", "--undo-replay-lab-user-dir=$expectedUserDir")
$argumentString = ($arguments | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' '
$process = Start-Process -FilePath (Join-Path $isolatedGameDir $exe.Name) -ArgumentList $argumentString -WorkingDirectory $isolatedGameDir -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
$metadata.status = 'running'
$metadata.pid = $process.Id
Write-Metadata $metadata $metadataPath
Write-Output "ReplayLab PID: $($process.Id)"
Write-Output "Run root: $RunRoot"

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
}
if (-not $process.HasExited) {
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
        Stop-Process -Id $process.Id -Force
    }
    $metadata.status = 'timeout'
    Write-Metadata $metadata $metadataPath
    throw "ReplayLab timed out after $TimeoutSeconds seconds; PID $($process.Id) was stopped. Evidence retained at $RunRoot"
}
$metadata.exitCode = $process.ExitCode
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    $metadata.status = 'failed-no-result'
    Write-Metadata $metadata $metadataPath
    throw "ReplayLab exited with code $($process.ExitCode) without result.json. Evidence retained at $RunRoot"
}
$result = $null
try { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } catch {
    $metadata.status = 'failed-invalid-result'
    Write-Metadata $metadata $metadataPath
    throw "ReplayLab produced invalid result.json. Evidence retained at $RunRoot"
}
if (-not (Test-ReportContract $result $process.Id $expectedUserDir)) {
    $metadata.status = 'failed-result-contract'
    Write-Metadata $metadata $metadataPath
    throw "ReplayLab result.json did not identify PID $($process.Id) and user data directory $expectedUserDir. Evidence retained at $RunRoot"
}
if ($process.ExitCode -ne 0) {
    $metadata.status = 'failed-exit-code'
    Write-Metadata $metadata $metadataPath
    throw "ReplayLab exited with code $($process.ExitCode). Evidence retained at $RunRoot"
}
$metadata.status = 'completed'
Write-Metadata $metadata $metadataPath
Write-Output "ReplayLab completed successfully. Result: $resultPath"
