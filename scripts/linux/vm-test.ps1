<#
.SYNOPSIS
    Syncs the Horizon + Hex workspace to the local VMware Workstation Ubuntu VM and runs Linux builds and tests.
.EXAMPLE
    .\vm-test.ps1 -Mode selftest
    .\vm-test.ps1 -Mode touchpad -VideoDriver wayland
    .\vm-test.ps1 -Mode all
#>
[CmdletBinding()]
param(
    [ValidateSet('sync', 'build', 'unit', 'selftest', 'dusk', 'smoke', 'touchpad', 'publish', 'all')]
    [string]$Mode = 'selftest',

    [ValidateSet('wayland', 'x11')]
    [string]$VideoDriver = 'wayland',

    [string]$SelfTestSuites = 'shell,menus,middle-scroll,scrollbars,minimap',
    [string]$GuestUser = 'bogdan',
    [string]$SshKey = "$env:USERPROFILE\.ssh\id_ed25519_linux",
    [string]$HostAddress = '',
    [string]$VmxPath = '',
    [string]$VmrunPath = '',
    [switch]$SkipSync,
    [switch]$StartVmIfStopped
)

$ErrorActionPreference = 'Stop'

$WorkspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$ScratchDir = Join-Path $WorkspaceRoot 'hex\Hex\bin\Debug\net11.0\data\agents\temp\scratch\linux-vm'
New-Item -ItemType Directory -Force -Path $ScratchDir | Out-Null

function Resolve-Vmrun {
    if ($VmrunPath -and (Test-Path $VmrunPath)) { return $VmrunPath }
    $regPaths = @(
        'HKLM:\SOFTWARE\VMware, Inc.\VMware Workstation',
        'HKLM:\SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation'
    )
    foreach ($rp in $regPaths) {
        $install = (Get-ItemProperty $rp -ErrorAction SilentlyContinue).InstallPath
        if ($install) {
            $candidate = Join-Path $install 'vmrun.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    foreach ($p in @('C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe', 'C:\Program Files\VMware\VMware Workstation\vmrun.exe')) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command vmrun.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Could not locate VMware Workstation vmrun.exe. Pass -VmrunPath explicitly.'
}

function Resolve-UbuntuVmx {
    if ($VmxPath -and (Test-Path $VmxPath)) { return $VmxPath }
    $inv = Join-Path $env:APPDATA 'VMware\inventory.vmls'
    if (Test-Path $inv) {
        $lines = Get-Content $inv
        foreach ($line in $lines) {
            if ($line -match 'config\s*=\s*"([^"]*Ubuntu[^"]*\.vmx)"') {
                if (Test-Path $Matches[1]) { return $Matches[1] }
            }
        }
        foreach ($line in $lines) {
            if ($line -match 'config\s*=\s*"([^"]+\.vmx)"') {
                if (Test-Path $Matches[1]) { return $Matches[1] }
            }
        }
    }
    throw 'Could not find Ubuntu .vmx in VMware inventory. Pass -VmxPath explicitly.'
}

$vmrun = Resolve-Vmrun
$vmx = Resolve-UbuntuVmx

if (-not $HostAddress) {
    $running = (& $vmrun -T ws list 2>&1) -join "`n"
    if ($running -notmatch [regex]::Escape($vmx)) {
        if ($StartVmIfStopped) {
            Write-Host "[vm-test] Starting Ubuntu VM: $vmx"
            & $vmrun -T ws start $vmx gui | Out-Host
        } else {
            throw "Ubuntu VM is not running ($vmx). Start it or pass -StartVmIfStopped."
        }
    }
    $ipOut = (& $vmrun -T ws getGuestIPAddress $vmx -wait 2>&1) | Out-String
    if ($ipOut -match '(\d+\.\d+\.\d+\.\d+)') {
        $HostAddress = $Matches[1]
    } else {
        throw "Could not resolve guest IP address: $ipOut"
    }
}

Write-Host "[vm-test] VM=$vmx IP=$HostAddress User=$GuestUser Mode=$Mode Driver=$VideoDriver"

$sshArgs = @('-i', $SshKey, '-o', 'StrictHostKeyChecking=no', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
$target = "$GuestUser@$HostAddress"

function Invoke-Guest([string]$Command) {
    & ssh.exe @sshArgs $target $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipSync) {
    Write-Host '[vm-test] Syncing horizon + hex sources to ~/src/horizon ...'
    $tarFile = Join-Path $ScratchDir 'workspace-sync.tar.gz'
    if (Test-Path $tarFile) { Remove-Item -LiteralPath $tarFile -Force }

    Push-Location $WorkspaceRoot
    try {
        & tar.exe -czf $tarFile `
            --exclude='.git' `
            --exclude='bin' `
            --exclude='obj' `
            --exclude='TestResults' `
            --exclude='publish' `
            --exclude='data/agents/temp' `
            --exclude='hex/site' `
            --exclude='hex/images' `
            --exclude='hex/docs/benchmarks' `
            --exclude='horizon/Horizon.Testing.Android' `
            --exclude='horizon/Horizon.Testing.iOS' `
            --exclude='horizon/Horizon.Android.Probe' `
            --exclude='horizon/Horizon.Platform.Android' `
            --exclude='horizon/Horizon.Platform.iOS' `
            Directory.Build.props global.json NuGet.Config horizon hex
        if ($LASTEXITCODE -ne 0) {
            throw "tar.exe failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    $mb = [Math]::Round((Get-Item $tarFile).Length / 1MB, 2)
    Write-Host "[vm-test] Uploading $mb MiB archive to VM..."
    & scp.exe @sshArgs $tarFile "${target}:/tmp/workspace-sync.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'scp upload failed.' }
    Remove-Item -LiteralPath $tarFile -Force

    Invoke-Guest @'
set -e
mkdir -p ~/src/horizon
tar -xzf /tmp/workspace-sync.tar.gz -C ~/src/horizon
rm -f /tmp/workspace-sync.tar.gz
find ~/src/horizon/hex/scripts/linux -type f \( -name "*.sh" -o -name "*.py" \) -exec sed -i 's/\r$//' {} + -exec chmod +x {} +
'@
    Write-Host '[vm-test] Sync complete.'
}

if ($Mode -eq 'sync') {
    exit 0
}

$remoteCmd = "HEX_VIDEO_DRIVER='$VideoDriver' HEX_SELFTEST_SUITES='$SelfTestSuites' ~/src/horizon/hex/scripts/linux/run-linux-tests.sh '$Mode'"
Write-Host "[vm-test] Running: $remoteCmd"
& ssh.exe @sshArgs $target $remoteCmd
$testExit = $LASTEXITCODE

# Always pull back logs & screenshots into scratch for inspection
Write-Host "[vm-test] Pulling logs/screenshots to $ScratchDir ..."
& scp.exe @sshArgs "${target}:/tmp/hex-linux-test/*" $ScratchDir 2>$null | Out-Null

if ($testExit -ne 0) {
    throw "Linux VM test mode '$Mode' failed with exit code $testExit"
}
Write-Host "[vm-test] Mode '$Mode' completed successfully."
