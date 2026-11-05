param(
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(3, 100)][int]$Samples = 10,
    [ValidateRange(2, 60)][int]$SettleSeconds = 5,
    [ValidateRange(3, 60)][int]$IdleSeconds = 5
)

# Windows benchmark of the published executable, in normal application mode, not --selftest.
# Each mode gets one discarded warmup. CPU is process CPU time / wall time: 100% is one logical core.
$ErrorActionPreference = 'Stop'
$PublishedDirectory = [IO.Path]::GetFullPath($PublishedDirectory)
$Repository = [IO.Path]::GetFullPath($Repository)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PublishedDirectory 'Hex.exe'))) { throw 'Publish Hex first.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory so runs cannot overwrite evidence.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$settings = Join-Path $OutputDirectory 'settings'
[IO.Directory]::CreateDirectory($settings) | Out-Null
$oldHome = $env:HEX_HOME
$oldStartup = $env:HORIZON_LOG_STARTUP
$oldProfile = $env:HEX_PROFILE
$samplesOut = [Collections.Generic.List[object]]::new()

function Read-Shared([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Measure-Launch([string]$mode, [int]$index) {
    $stdout = Join-Path $OutputDirectory "$mode-$index.stdout.log"
    $stderr = Join-Path $OutputDirectory "$mode-$index.stderr.log"
    $arguments = if ($mode -eq 'dawn') {
        @('--window', '--fresh', '--size', '1600x900', '--folder', $Repository,
          '--open', (Join-Path $Repository 'Hex.Dawn/Shell/DawnScene.cs'))
    } else { @('--dusk', '--size', '1600x900', '--cwd', $Repository) }
    # Start-Process joins ArgumentList with spaces on Windows PowerShell; quote every argument.
    $quoted = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $process = Start-Process -FilePath (Join-Path $PublishedDirectory 'Hex.exe') -WorkingDirectory $PublishedDirectory `
        -ArgumentList $quoted -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            Start-Sleep -Milliseconds 40
            $log = Read-Shared $stdout
            $frame = [regex]::Match($log, 'first frame of the scene shown at (\d+) ms')
            if ($process.HasExited) { throw "$mode exited before its first frame; see $stdout" }
        } until ($frame.Success -or [DateTime]::UtcNow -gt $deadline)
        if (!$frame.Success) { throw "No first-frame marker for $mode; see $stdout" }
        if ($index -eq 0) {
            Start-Sleep -Seconds $SettleSeconds
            Write-Output "$mode warmup discarded (shader/cache population)."
            return
        }
        Start-Sleep -Seconds $SettleSeconds
        $process.Refresh()
        $cpuStart = $process.TotalProcessorTime.TotalMilliseconds
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $memory = [Collections.Generic.List[long]]::new()
        $private = [Collections.Generic.List[long]]::new()
        while ($timer.Elapsed.TotalSeconds -lt $IdleSeconds) {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
            if ($process.HasExited) { throw "$mode exited during its idle sample." }
            $memory.Add($process.WorkingSet64)
            $private.Add($process.PrivateMemorySize64)
        }
        $cpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuStart
        $elapsed = $timer.Elapsed.TotalMilliseconds
        $main = [regex]::Match($log, '\[Startup\] main at (\d+) ms')
        $record = [pscustomobject]@{
            Mode = $mode; Sample = $index; FirstFrameMs = [int]$frame.Groups[1].Value
            MainMs = if ($main.Success) { [int]$main.Groups[1].Value } else { $null }
            IdleWorkingSetBytes = [long](($memory | Measure-Object -Average).Average)
            IdlePrivateBytes = [long](($private | Measure-Object -Average).Average)
            IdleCpuOneCorePercent = [Math]::Round(100 * $cpuMs / $elapsed, 3)
            IdleIntervalMs = [Math]::Round($elapsed, 1)
            ProcessId = $process.Id
        }
        $samplesOut.Add($record)
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'samples.json'), (ConvertTo-Json -InputObject @($samplesOut.ToArray()) -Depth 5))
        Write-Output "$mode $index/$Samples : $($record.FirstFrameMs) ms; $([Math]::Round($record.IdleWorkingSetBytes / 1MB, 1)) MiB; $($record.IdleCpuOneCorePercent)% of one core"
    }
    finally {
        if (!$process.HasExited) {
            $process.Refresh()
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
        }
        Copy-Item -LiteralPath (Join-Path $PublishedDirectory 'debug.log') -Destination (Join-Path $OutputDirectory "$mode-$index.debug.log") -ErrorAction SilentlyContinue
        $process.Dispose()
    }
}

try {
    $env:HEX_HOME = $settings
    $env:HORIZON_LOG_STARTUP = '1'
    $env:HEX_PROFILE = '0'
    $metadata = [ordered]@{
        DateUtc = [DateTime]::UtcNow.ToString('o'); SamplesPerMode = $Samples
        DiscardedWarmupsPerMode = 1; SettleSeconds = $SettleSeconds; IdleSeconds = $IdleSeconds
        Cpu = (Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        Os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber)
        RamBytes = [long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        Gpu = (Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
        BinarySha256 = (Get-FileHash -LiteralPath (Join-Path $PublishedDirectory 'Hex.exe') -Algorithm SHA256).Hash
        HexHead = (git -C $Repository rev-parse HEAD)
        LocalChangesIncluded = (@(git -C $Repository status --porcelain).Count -gt 0)
        Workspace = 'Hex repository; DawnScene.cs open in Dawn; one default shell in Dusk'
        Window = '1600x900 requested; default display scaling'; Profile = 'FolderProfile: Release, win-x64, NativeAOT, self-contained'
        Scope = 'Hex process only; language servers, shells and other child processes are excluded from CPU/memory'
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'machine.json'), ($metadata | ConvertTo-Json -Depth 6))
    Measure-Launch 'dawn' 0
    Measure-Launch 'dusk' 0
    for ($index = 1; $index -le $Samples; $index++) {
        $order = if ($index % 2) { @('dawn', 'dusk') } else { @('dusk', 'dawn') }
        foreach ($mode in $order) { Measure-Launch $mode $index }
    }
}
finally {
    $env:HEX_HOME = $oldHome
    $env:HORIZON_LOG_STARTUP = $oldStartup
    $env:HEX_PROFILE = $oldProfile
}
