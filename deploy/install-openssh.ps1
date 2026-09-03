<#
.SYNOPSIS
    Installs and starts the built-in Windows OpenSSH server on a mining node, reachable
    from the tailnet only.

.DESCRIPTION
    Nodes are diagnosed by hand far more often than the fleet API allows: the agent
    exposes miner control and telemetry, not a shell, so a node with no SSH server can
    only be inspected by sitting at it. This script turns on the OpenSSH server that
    ships with Windows 10/11 as an optional feature, registers it to start with the
    machine, and opens port 22 to 100.64.0.0/10 the way install-agent.ps1 opens 47800 -
    the tailnet is the security boundary, so the port must never face the LAN.

    That component is a Feature on Demand, fetched from Windows Update rather than from the
    installation media, and on a node whose updates are pointed at a WSUS server that does
    not carry it the install fails with REGDB_E_CLASSNOTREG after a long silent wait. Seen
    on a live node. When that happens the script falls back to the upstream MSI, which needs
    nothing from Windows Update - see Install-FromRelease for why that is a fallback and not
    the default.

    Authentication is by public key. A password-authenticated SSH server on a mining rig
    is an invitation, and the operator console already reaches these nodes without one.
    Pass -PublicKey (or set $env:XMRIG_FLEET_SSH_KEY) and the key is installed for
    -UserName. Which file that means is decided by whether the account is an
    administrator: Windows' sshd_config sends every admin login to one shared
    administrators_authorized_keys and ignores that account's own ~\.ssh, so a key
    written to the wrong one of the two is silently never read.

    Run this in an elevated PowerShell on the node. It can also be run straight from the
    web, which avoids the execution policy that blocks a downloaded .ps1 file:

        $env:XMRIG_FLEET_SSH_KEY  = 'ssh-ed25519 AAAA... operator'
        $env:XMRIG_FLEET_SSH_USER = 'local'
        irm https://raw.githubusercontent.com/XYphrodite/xmrig-fleet/master/deploy/install-openssh.ps1 | iex

    Nothing here touches xmrig or the fleet agent: installing SSH does not interrupt
    mining.

.EXAMPLE
    .\install-openssh.ps1 -PublicKey (Get-Content ~\.ssh\id_ed25519.pub -Raw)
#>
[CmdletBinding()]
param(
    # Operator's SSH public key, authorised for administrator logins. Falls back to
    # $env:XMRIG_FLEET_SSH_KEY so the script also works when piped to iex, which cannot
    # pass arguments.
    [string]$PublicKey = $env:XMRIG_FLEET_SSH_KEY,

    # Account the key logs in as. An elevated shell may be running as a different
    # administrator than the one the operator logs in as, so this is worth naming.
    [string]$UserName = $(if ($env:XMRIG_FLEET_SSH_USER) { $env:XMRIG_FLEET_SSH_USER } else { $env:USERNAME }),

    # Leave password authentication on. Off by default: see the description.
    [switch]$AllowPasswordAuth,

    [int]$Port = 22,

    # Remote range allowed through the firewall. The tailnet CGNAT range by default.
    [string]$RemoteAddress = '100.64.0.0/10'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell.'
}

if ([string]::IsNullOrWhiteSpace($PublicKey) -and -not $AllowPasswordAuth) {
    throw 'No public key. Pass -PublicKey, set $env:XMRIG_FLEET_SSH_KEY, or accept the risk with -AllowPasswordAuth.'
}

# ---------------------------------------------------------------- install the feature

<#
.SYNOPSIS
    Installs OpenSSH from the upstream release when the Windows component will not.

.DESCRIPTION
    Deliberately a fallback rather than the default. The component that ships with Windows
    is serviced by Windows Update; a copy installed from a release is not, and somebody has
    to remember it exists. But a node that cannot install the component is a node that can
    only ever be diagnosed by sitting at it, which is the very thing this script exists to
    avoid, so the fallback wins over having no shell at all.

    Upstream tags every build "Preview" - it is their long-standing habit, not a warning
    about this particular one - so a release is chosen by date and that label is ignored.
#>
function Install-FromRelease {
    Write-Host '==> Installing OpenSSH from the upstream release instead'

    $api = 'https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases?per_page=10'
    $releases = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'xmrig-fleet' }

    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'Win64' }
    } else { 'Win32' }

    $asset = $releases |
        Sort-Object { [datetime]$_.published_at } -Descending |
        ForEach-Object { $_.assets } |
        Where-Object { $_.name -like "OpenSSH-$arch-*.msi" } |
        Select-Object -First 1

    if (-not $asset) {
        throw "No OpenSSH-$arch MSI in the last releases of PowerShell/Win32-OpenSSH. Install it by hand."
    }

    $msi = Join-Path $env:TEMP $asset.name
    Write-Host "    downloading $($asset.name)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $msi -UseBasicParsing

    # ADDLOCAL=Server: the client half is not wanted on a rig, and installing it would put
    # a second ssh.exe ahead of the one Windows ships on PATH.
    $run = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" ADDLOCAL=Server /qn /norestart" -Wait -PassThru
    if ($run.ExitCode -ne 0) {
        throw "msiexec failed with exit code $($run.ExitCode) installing $($asset.name)."
    }

    Remove-Item $msi -ErrorAction SilentlyContinue
    Write-Host '    installed'
}

# The MSI below registers sshd too, so an sshd that already exists is the end of this
# step whichever way it got there. Checked before the capability, because a node
# installed from the MSI reports the capability as absent and would be sent round the
# loop that failed in the first place.
if (Get-Service -Name sshd -ErrorAction SilentlyContinue) {
    Write-Host '==> sshd is already installed on this node'
}
else {
    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' |
        Sort-Object Name | Select-Object -First 1

    if ($capability -and $capability.State -eq 'Installed') {
        Write-Host "==> $($capability.Name) already installed"
    }
    elseif ($capability) {
        # A Feature on Demand, so it is fetched from Windows Update rather than from the
        # installation media. That makes this the one step on a mining node that depends on
        # Windows Update working, and on some nodes it does not: a WSUS policy points the
        # search at a server that does not carry the package, and the call fails with
        # REGDB_E_CLASSNOTREG (0x80040154) after a long silent wait. Observed on a live node.
        Write-Host "==> Installing $($capability.Name)"
        try {
            Add-WindowsCapability -Online -Name $capability.Name | Out-Null
            Write-Host '    installed'
        }
        catch {
            Write-Host "    the Windows component would not install: $($_.Exception.Message)" -ForegroundColor Yellow
            Install-FromRelease
        }
    }
    else {
        Write-Host '==> This Windows build does not offer the OpenSSH.Server component' -ForegroundColor Yellow
        Install-FromRelease
    }
}

# ----------------------------------------------------------------------- the services

# One run of sshd creates the host keys and the default sshd_config, so start it before
# editing either.
Set-Service -Name sshd -StartupType Automatic
Start-Service -Name sshd

# The agent key service is what makes `ssh-add` work on the node itself; it is disabled
# out of the box and is not needed to accept logins, so leave a node's choice alone.
Write-Host "Service 'sshd' is $((Get-Service sshd).Status)."

# Restart sshd if it ever crashes, matching how the agent service is registered - a node
# that drops its shell is diagnosed the hard way.
& sc.exe failure sshd reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# ------------------------------------------------------------------- authorised keys

if (-not [string]::IsNullOrWhiteSpace($PublicKey)) {
    $account = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if (-not $account) { throw "No local account named '$UserName' on this machine." }

    # Compared by SID: the group and the member names are both localised, so matching on
    # "Administrators" fails on a Russian Windows, which is what these nodes run.
    $adminGroup = Get-LocalGroup -SID 'S-1-5-32-544'
    $isAdmin = [bool](Get-LocalGroupMember -Group $adminGroup -ErrorAction SilentlyContinue |
        Where-Object { $_.SID -eq $account.SID })

    if ($isAdmin) {
        $keyFile = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
        # Only Administrators and SYSTEM may write it, or sshd ignores the file and says
        # so only in its own log, where the operator will not think to look.
        $acl = { & icacls.exe $keyFile /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null }
    } else {
        $keyFile = Join-Path (Join-Path 'C:\Users' $UserName) '.ssh\authorized_keys'
        $acl = { & icacls.exe $keyFile /inheritance:r /grant "${UserName}:F" /grant 'SYSTEM:F' | Out-Null }
    }

    $null = New-Item -ItemType Directory -Force -Path (Split-Path $keyFile)
    $key = $PublicKey.Trim()
    $existing = if (Test-Path $keyFile) { Get-Content $keyFile } else { @() }

    if ($existing -contains $key) {
        Write-Host "==> Public key already authorised in $keyFile"
    } else {
        Write-Host "==> Authorising the public key for '$UserName' in $keyFile"
        # Rewritten whole rather than appended: Add-Content would inherit whatever
        # encoding the file already had, and sshd rejects a UTF-8 BOM in this file.
        $lines = @($existing | Where-Object { $_.Trim() }) + $key
        Set-Content -Path $keyFile -Value $lines -Encoding ascii
    }

    & $acl
    Write-Host "    $(if ($isAdmin) { "'$UserName' is an administrator, so sshd reads the shared admin key file" } else { "'$UserName' is a standard account" })"
}

# ---------------------------------------------------------------------- sshd_config

$configPath = Join-Path $env:ProgramData 'ssh\sshd_config'
if (Test-Path $configPath) {
    $config = Get-Content $configPath

    function Set-SshOption {
        param([string[]]$Lines, [string]$Name, [string]$Value)
        # Comment out every existing occurrence, commented or not, then append one
        # authoritative line: sshd honours the *first* match, so editing in place would
        # leave a stale earlier line in charge.
        $out = foreach ($line in $Lines) {
            if ($line -match "^\s*#?\s*$Name\s") { "# (xmrig-fleet) $line" } else { $line }
        }
        @($out) + "$Name $Value"
    }

    $config = Set-SshOption $config 'Port' $Port
    $config = Set-SshOption $config 'PubkeyAuthentication' 'yes'
    if (-not $AllowPasswordAuth) {
        $config = Set-SshOption $config 'PasswordAuthentication' 'no'
    }

    Set-Content -Path $configPath -Value $config -Encoding ascii
    Write-Host "sshd_config: port $Port, public keys on, passwords $(if ($AllowPasswordAuth) { 'left enabled' } else { 'off' })."
    Restart-Service sshd
}

# ------------------------------------------------------------------------- firewall

# Windows adds its own "OpenSSH-Server-In-TCP" rule scoped to Any when the feature is
# installed. Disable it: the whole point is that this port faces the tailnet only.
Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue |
    Disable-NetFirewallRule

$ruleName = 'xmrig-fleet ssh (tailnet)'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress $RemoteAddress `
    -Profile Any | Out-Null
Write-Host "Firewall: allowed TCP $Port from $RemoteAddress only; the Windows OpenSSH rule is disabled."

# ---------------------------------------------------------------------- verification

$listening = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($listening) {
    Write-Host "sshd is listening on $Port."
} else {
    Write-Warning "sshd is not listening on $Port. Look in $(Join-Path $env:ProgramData 'ssh\logs\sshd.log')."
}

$hostname = $env:COMPUTERNAME.ToLowerInvariant()
Write-Host ''
Write-Host "Done. From the operator machine: ssh $env:USERNAME@$hostname"
