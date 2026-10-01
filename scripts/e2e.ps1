#Requires -Version 7
<#
.SYNOPSIS
    End-to-end verification: runs the real rockets test program against the service, then checks the result
    with the oracle against the expected state of a seed-444 run (tests/e2e/expected-seed444-*.json).

.DESCRIPTION
    Scenarios:
      quick    10,000 messages with the test program's defaults (a few seconds)
      default  the grading run: 100,000 messages with the test program's defaults
      crash    10,000 messages at 2 ms intervals; the service is hard-killed mid-run, then restarted on the same database
      stress   100,000 messages at concurrency 20
      all      all of the above

    The oracle (tools/Rockets.Capture verify) folds every rocket independently of the service's code and compares
    it with GET /rockets. It also checks that the service's message log holds every expected message with identical content.

.EXAMPLE
    ./scripts/e2e.ps1                      # quick
    ./scripts/e2e.ps1 -Scenario all
    ./scripts/e2e.ps1 -RegenerateExpected  # recapture the expected states first
#>
param(
    [ValidateSet('quick', 'default', 'crash', 'stress', 'all')]
    [string[]] $Scenario = @('quick'),
    [switch] $RegenerateExpected,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'artifacts/e2e'
$expectedDirectory = Join-Path $root 'tests/e2e'
$serviceDll = Join-Path $root 'src/Rockets.Api/bin/Release/net10.0/Rockets.Api.dll'
$captureDll = Join-Path $root 'tools/Rockets.Capture/bin/Release/net10.0/Rockets.Capture.dll'
$url = 'http://localhost:8088'

function Get-RocketsBinary {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $platform =
        if ($IsWindows) { if ($arch -eq 'X86') { 'windows_386' } else { 'windows_amd64' } }
        elseif ($IsMacOS) { if ($arch -eq 'Arm64') { 'darwin_arm64' } else { 'darwin_amd64' } }
        else { if ($arch -eq 'Arm64') { 'linux_arm64' } else { 'linux_amd64' } }
    $name = if ($IsWindows) { 'rockets.exe' } else { 'rockets' }
    Join-Path $root "vendor/rockets/$platform/$name"
}

$rockets = Get-RocketsBinary

function Test-PortInUse {
    try { Invoke-WebRequest "$url/health" -TimeoutSec 2 | Out-Null; return $true } catch { }
    try { $client = [Net.Sockets.TcpClient]::new('localhost', 8088); $client.Dispose(); return $true } catch { return $false }
}

# The test program opens many short-lived connections. On Windows, back-to-back large runs can use up the
# client ports (TIME_WAIT), so wait for them to be released before each scenario.
function Wait-ForClientPorts {
    if (-not $IsWindows) { return }
    $deadline = (Get-Date).AddMinutes(5)
    while (@(Get-NetTCPConnection -State TimeWait -RemotePort 8088 -ErrorAction SilentlyContinue).Count -gt 1000) {
        if ((Get-Date) -gt $deadline) { throw 'Client ports are still in TIME_WAIT after 5 minutes.' }
        Write-Host '    waiting for client ports in TIME_WAIT to be released...'
        Start-Sleep -Seconds 10
    }
}

function Start-Server([string[]] $Arguments, [string] $Log) {
    $process = Start-Process dotnet -ArgumentList $Arguments -RedirectStandardOutput $Log -RedirectStandardError "$Log.err" -PassThru -NoNewWindow
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($process.HasExited) { throw "The server exited during startup; see $Log." }
        try { Invoke-WebRequest "$url/" -TimeoutSec 2 -SkipHttpErrorCheck | Out-Null; return $process } catch { }
        if ((Get-Date) -gt $deadline) { Stop-Process $process -Force; throw "Nothing answered on $url within 60 s; see $Log." }
        Start-Sleep -Milliseconds 250
    }
}

function Stop-Server($Process) {
    if ($Process -and -not $Process.HasExited) {
        Stop-Process $Process -Force
        $Process.WaitForExit()
    }
}

function Start-Rockets([string[]] $Arguments, [string] $Directory) {
    $process = Start-Process $rockets -ArgumentList (@('launch', "$url/messages") + $Arguments) `
        -RedirectStandardOutput (Join-Path $Directory 'rockets.out.log') -RedirectStandardError (Join-Path $Directory 'rockets.log') `
        -PassThru -NoNewWindow
    [pscustomobject]@{ Process = $process; Directory = $Directory; Watch = [Diagnostics.Stopwatch]::StartNew() }
}

function Wait-Rockets($Run) {
    $Run.Process.WaitForExit()
    $seconds = [math]::Round($Run.Watch.Elapsed.TotalSeconds, 1)
    $logs = Get-ChildItem $Run.Directory -Filter 'rockets*.log'
    [pscustomobject]@{
        ExitCode = $Run.Process.ExitCode
        Seconds  = $seconds
        Retries  = @($logs | Select-String -Pattern 'Retrying sending message').Count
        # Retries still waiting when the test program finishes are dropped: those messages are never delivered.
        Dropped  = @($logs | Select-String -Pattern 'Redelivering message failed').Count
    }
}

function Invoke-Scenario([string] $Name, [int] $Messages, [string[]] $RocketsArguments, [switch] $Crash) {
    Write-Host "== $Name ($Messages messages; rockets $($RocketsArguments -join ' '))"
    Wait-ForClientPorts
    $directory = Join-Path $work $Name
    Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $directory -ItemType Directory | Out-Null
    $database = Join-Path $directory 'rockets.db'
    $serverArguments = @($serviceDll, "--Storage:DatabasePath=$database")

    $service = Start-Server $serverArguments (Join-Path $directory 'service.log')
    try {
        $run = Start-Rockets $RocketsArguments $directory
        if ($Crash) {
            Start-Sleep -Seconds 4
            Stop-Server $service
            Write-Host '    hard-killed the service'
            Start-Sleep -Seconds 3
            $service = Start-Server $serverArguments (Join-Path $directory 'service-restarted.log')
            Write-Host '    restarted the service on the same database'
        }
        $result = Wait-Rockets $run
        $health = Invoke-RestMethod "$url/health"
        & dotnet $captureDll verify (Join-Path $expectedDirectory "expected-seed444-$Messages.json") $url $database | Write-Host
        $oracle = if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }
    }
    finally {
        Stop-Server $service
    }

    [pscustomobject]@{
        Scenario      = $Name
        Oracle        = $oracle
        Messages      = $Messages
        Seconds       = $result.Seconds
        RocketsExit   = $result.ExitCode
        Retries       = $result.Retries
        Dropped       = $result.Dropped
        # Counters since the (last) start of the service.
        Stored        = $health.messages.stored
        Duplicates    = $health.messages.duplicates
        Rejected      = $health.messages.rejected
        StoreFailures = $health.messages.storeFailures
        AvgBatch      = if ($health.messages.commits) { [math]::Round($health.messages.stored / $health.messages.commits, 2) } else { 0 }
    }
}

function Update-Expected([int] $Messages) {
    Write-Host "== recapturing the expected state for $Messages messages"
    Wait-ForClientPorts
    $directory = Join-Path $work "capture-$Messages"
    Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $directory -ItemType Directory | Out-Null
    $capture = Join-Path $directory 'capture.ndjson'
    $server = Start-Server @($captureDll, 'serve', "--Capture:Path=$capture") (Join-Path $directory 'capture.log')
    try {
        $result = Wait-Rockets (Start-Rockets @("--max-messages=$Messages") $directory)
        if ($result.ExitCode -ne 0) { throw "rockets exited with $($result.ExitCode) while capturing." }
    }
    finally {
        Stop-Server $server
    }
    & dotnet $captureDll expect $capture (Join-Path $expectedDirectory "expected-seed444-$Messages.json")
    if ($LASTEXITCODE -ne 0) { throw 'Writing the expected state failed.' }
}

if (Test-PortInUse) { throw "Something is already listening on $url. Stop it first." }
if (-not $NoBuild) {
    Write-Host '== building (Release)'
    & dotnet build (Join-Path $root 'src/Rockets.Api') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the service failed.' }
    & dotnet build (Join-Path $root 'tools/Rockets.Capture') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the capture tool failed.' }
}

if ($RegenerateExpected) {
    Update-Expected 10000
    Update-Expected 100000
}

$scenarios = if ($Scenario -contains 'all') { @('quick', 'default', 'crash', 'stress') } else { $Scenario }
$results = foreach ($name in $scenarios) {
    switch ($name) {
        'quick'   { Invoke-Scenario 'quick' 10000 @('--max-messages=10000') }
        'default' { Invoke-Scenario 'default' 100000 @() }
        'crash'   { Invoke-Scenario 'crash' 10000 @('--max-messages=10000', '--message-delay=2ms') -Crash }
        'stress'  { Invoke-Scenario 'stress' 100000 @('--concurrency-level=20') }
    }
}

$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
$results | ConvertTo-Json | Set-Content (Join-Path $work 'results.json')
Write-Host "Results written to $(Join-Path $work 'results.json')"
$failed = @($results | Where-Object { $_.Oracle -ne 'PASS' -or $_.RocketsExit -ne 0 -or $_.Dropped -gt 0 })
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed.Scenario -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'All scenarios passed.' -ForegroundColor Green
