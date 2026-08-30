<#
.SYNOPSIS
    Installs the xmrig-fleet agent as a Windows service on a mining node.

.DESCRIPTION
    Copies the published agent to the target directory, writes appsettings.json with the
    shared fleet token, opens the agent port to the tailnet only, and registers a service
    that starts automatically.

    Run this in an elevated PowerShell on the node. The agent needs Administrator to read
    temperature and power sensors and to control the miner process.

.EXAMPLE
    .\install-agent.ps1 -Token "my-fleet-secret" -SourcePath .\publish
#>
[CmdletBinding()]
param(
    # Shared secret, must match "token" in the console's fleet.json.
    [Parameter(Mandatory = $true)]
    [string]$Token,

    # Folder holding the published agent (dotnet publish output).
    [string]$SourcePath = "$PSScriptRoot\..\publish\agent",

    [string]$InstallPath = 'C:\Program Files\xmrig-fleet-agent',

    [int]$Port = 47800,

    # Loopback port the agent starts xmrig's own HTTP API on.
    [int]$XmrigApiPort = 47801,

    [string]$ServiceName = 'xmrig-fleet-agent'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell.'
}

if (-not (Test-Path $SourcePath)) {
    throw "Published agent not found at $SourcePath. Run deploy\publish.ps1 first."
}

$exeName = 'xmrig-fleet-agent.exe'
if (-not (Test-Path (Join-Path $SourcePath $exeName))) {
    throw "$exeName is missing from $SourcePath."
}

# Stop an existing service so its files can be replaced.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    # sc.exe delete returns before the service object disappears; wait it out.
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Copying agent to $InstallPath"
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

$settings = [ordered]@{
    Agent = [ordered]@{
        Token          = $Token
        ListenUrl      = "http://0.0.0.0:$Port"
        XmrigApiPort   = $XmrigApiPort
        AutoStartMiner = $false
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
    AllowedHosts = '*'
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $InstallPath 'appsettings.json') -Encoding utf8

# The agent port must not be reachable from the LAN or the internet: only from the tailnet.
$ruleName = "xmrig-fleet-agent ($Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress '100.64.0.0/10' `
    -Profile Any | Out-Null
Write-Host "Firewall: allowed TCP $Port from the tailnet range only (100.64.0.0/10)."

$binPath = '"{0}"' -f (Join-Path $InstallPath $exeName)
& sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "xmrig fleet agent" | Out-Null
& sc.exe description $ServiceName "Controls xmrig and reports hardware telemetry to the xmrig-fleet console." | Out-Null
# Restart the service if it ever crashes, so a node does not silently drop out of the fleet.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

$service = Get-Service -Name $ServiceName
Write-Host "Service '$ServiceName' is $($service.Status)."

try {
    $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/v1/info" -Headers @{ 'X-Fleet-Token' = $Token } -TimeoutSec 10
    Write-Host "Agent $($response.agentVersion) responding on $($response.hostname). Elevated: $($response.isElevated)."
    $tailscaleIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '100.*' } | Select-Object -First 1).IPAddress
    if ($tailscaleIp) { Write-Host "Add this node to the console as: $tailscaleIp`:$Port" }
} catch {
    Write-Warning "Service started but the API did not answer: $($_.Exception.Message)"
    Write-Warning "Check the Windows event log for source '$ServiceName'."
}
