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

    Authentication is by public key. A password-authenticated SSH server on a mining rig
    is an invitation, and the operator console already reaches these nodes without one.
    Pass -PublicKey (or set $env:XMRIG_FLEET_SSH_KEY) and the key is installed for
    -UserName. Which file that means is decided by whether the account is an
    administrator: Windows' sshd_config sends every admin login to one shared
    administrators_authorized_keys and ignores that account's own ~\.ssh, so a key
    written to the wrong one of the two is silently never read.

    Run this in an elevated PowerShell on the node. It can also be run straight from the
    web, which avoids the execution policy that blocks a downloaded .ps1 file:

        $env:XMRIG_FLEET_SSH_KEY = 'ssh-ed25519 AAAA... operator'
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

    # Account the key logs in as. Defaults to whoever is running the script.
    [string]$UserName = $env:USERNAME,

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

$capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' |
    Sort-Object Name | Select-Object -First 1
if (-not $capability) {
    throw 'This Windows build does not offer the OpenSSH.Server capability. Install Win32-OpenSSH from github.com/PowerShell/Win32-OpenSSH instead.'
}

if ($capability.State -eq 'Installed') {
    Write-Host "==> $($capability.Name) already installed"
} else {
    # Pulled from Windows Update as a Feature on Demand, so the node needs internet.
    Write-Host "==> Installing $($capability.Name)"
    Add-WindowsCapability -Online -Name $capability.Name | Out-Null
    Write-Host '    installed'
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
