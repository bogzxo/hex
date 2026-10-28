param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CurrentDirectory,
    [ValidateRange(1, 100)][int]$Samples = 30,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('hex-ux-startup-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$builds = @{
    baseline = [IO.Path]::GetFullPath($BaselineDirectory)
    current = [IO.Path]::GetFullPath($CurrentDirectory)
}
foreach ($directory in $builds.Values) {
    if (!(Test-Path -LiteralPath (Join-Path $directory 'Hex.exe'))) { throw "Hex.exe missing in $directory" }
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$savedStartup = $env:HORIZON_LOG_STARTUP
$savedProfile = $env:HEX_PROFILE
$results = [Collections.Generic.List[object]]::new()
try {
    $env:HORIZON_LOG_STARTUP = 'memory'
    $env:HEX_PROFILE = '0'
    # One unrecorded warmup per build. Alternate order to reduce systematic first/second launch bias.
    for ($sample = 0; $sample -le $Samples; $sample++) {
        $order = if ($sample % 2 -eq 0) { @('baseline', 'current') } else { @('current', 'baseline') }
        foreach ($build in $order) {
            $directory = $builds[$build]
            $stdout = Join-Path $OutputDirectory "$build-$sample.out"
            $stderr = Join-Path $OutputDirectory "$build-$sample.err"
            $process = Start-Process -FilePath (Join-Path $directory 'Hex.exe') -WorkingDirectory $directory `
                -ArgumentList '--selftest', 'shell', '--exit', '--size', '1600x900' -WindowStyle Hidden `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            $null = $process.Handle
            if (!$process.WaitForExit(60000)) {
                # Only the benchmark's own process is stopped on timeout.
                $process.Kill()
                throw "$build sample $sample timed out; see $stdout"
            }
            $process.WaitForExit()
            # A short-lived child can retain the redirected output handle after Hex exits.
            $stream = [IO.File]::Open($stdout, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = [IO.StreamReader]::new($stream)
            try { $log = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if (($null -ne $process.ExitCode -and $process.ExitCode -ne 0) -or $log -match '\[Hex self-test\] FAIL:' -or $log -notmatch 'checks passed') {
                throw "$build sample $sample failed its shell checks; see $stdout and $stderr"
            }
            $startup = [regex]::Match($log, 'first frame of the scene shown at (\d+) ms, working set (\d+) MB, private (\d+) MB, heap (\d+) MB')
            if (!$startup.Success) { throw "Startup trace missing in $stdout" }
            if ($sample -gt 0) {
                $results.Add([pscustomobject]@{
                    Build = $build; Sample = $sample
                    FirstFrameMs = [int]$startup.Groups[1].Value
                    FirstFrameWorkingSetMB = [int]$startup.Groups[2].Value
                    FirstFramePrivateMB = [int]$startup.Groups[3].Value
                    FirstFrameHeapMB = [int]$startup.Groups[4].Value
                })
            }
        }
        Write-Output "Completed pair $sample / $Samples"
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'samples.json'), ($results | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    foreach ($build in @('baseline', 'current')) {
        $ordered = @($results | Where-Object Build -eq $build | Sort-Object FirstFrameMs)
        $middle = [int][Math]::Floor($ordered.Count / 2)
        $median = if ($ordered.Count % 2) { $ordered[$middle].FirstFrameMs } else { ($ordered[$middle - 1].FirstFrameMs + $ordered[$middle].FirstFrameMs) / 2.0 }
        $p95 = $ordered[[int][Math]::Ceiling($ordered.Count * 0.95) - 1].FirstFrameMs
        Write-Output "$build : median $median ms, p95 $p95 ms"
    }
    Write-Output "Samples and logs: $OutputDirectory"
}
finally {
    $env:HORIZON_LOG_STARTUP = $savedStartup
    $env:HEX_PROFILE = $savedProfile
}
