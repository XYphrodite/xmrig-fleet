# xmrig-fleet - Project Context

## Project Overview

**xmrig-fleet** is a console fleet manager for [XMRig](https://github.com/xmrig/xmrig)
mining rigs joined by a Tailscale tailnet. A small HTTP agent on every mining PC starts
and stops the miner, reports hashrate and hardware telemetry, and installs or updates
XMRig on demand; a Spectre.Console TUI on the operator machine polls the whole fleet,
prices the electricity it burns, and reads the pool balance from Hashvault.

**Platform**: .NET 8 (`net8.0`), Windows 10/11 today, Linux-ready
**Language**: C# 12
**Domain**: Cryptocurrency mining operations / fleet telemetry and control

---

## Technical Stack

### Runtime & Frameworks

| Layer | Technology | Notes |
|-------|-----------|-------|
| Node agent | ASP.NET Core Minimal API (`net8.0`) | `xmrig-fleet-agent.exe`, Kestrel on `0.0.0.0:47800` |
| Operator console | .NET 8 console + Spectre.Console | `xmrig-fleet.exe`, interactive TUI **and** one-shot commands |
| Shared contracts | .NET 8 class library | DTOs referenced by both sides |
| Transport | Plain HTTP over the tailnet | Shared-secret header, no TLS (see **Security Model**) |
| Service hosting | Windows Service / systemd | Same binary, both hosts registered unconditionally |
| Miner telemetry | XMRig HTTP API on loopback | Agent starts XMRig with a per-process random token |

### Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Spectre.Console` | 0.57.2 | Tables, panels, prompts, live refresh in the console |
| `LibreHardwareMonitorLib` | 0.9.6 | CPU/GPU/RAM/board sensors on the agent |
| `Microsoft.Extensions.Hosting.WindowsServices` | 8.0.1 | Agent as a Windows service |
| `Microsoft.Extensions.Hosting.Systemd` | 8.0.1 | Agent as a systemd unit (Linux, untested) |
| `Microsoft.Win32.Registry` | 5.0.0 | PawnIO presence probe for sensor diagnostics |

### External Services

| Service | Endpoint | Used for |
|---------|----------|----------|
| Hashvault | `/v3/monero/pool/stats` | Difficulty, block reward, block time, pool hashrate, XMR price |
| Hashvault | `/v3/monero/wallet/{address}/stats` | Balance, shares, payouts, payout threshold |
| CoinGecko | `simple/price?ids=monero` | Fallback price when the pool lacks the currency |
| GitHub Releases | `repos/xmrig/xmrig/releases` | Miner install and update payloads |
| Tailscale CLI | `tailscale status --json` | Node discovery for the operator console |

### Codebase Size

Hand-written source only; excludes `bin`/`obj`, generated files, and documentation.

| Language | Files | Code lines |
|----------|------:|-----------:|
| C# (agent + console + contracts) | 22 | 2,848 |
| PowerShell (`deploy/`) | 2 | 127 |
| **Total** | **24** | **2,975** |

---

## Architecture Overview

The console holds no state about a running miner: every screen is rendered from a live
poll of the agents, and every action is an HTTP call. The agent is the only component
that touches the XMRig process, and it always launches the miner with the loopback HTTP
API enabled, so hashrate never has to be scraped from stdout.

```
  OPERATOR MACHINE
  +-----------------------------------------------------------+
  |  xmrig-fleet.exe   (Spectre.Console TUI + one-shot CLI)    |
  |    Dashboard | Miner control | Nodes | Hardware            |
  |    Economics | Pool & wallet | Settings                    |
  |                                                           |
  |    FleetService --- fans out, one AgentClient per node     |
  |    MarketService -- Hashvault + price feed (30s cache)     |
  |    TailscaleService -- `tailscale status --json`           |
  |    fleet.json -- nodes, token, wallet, kWh price           |
  +------------------------+----------------------------------+
                           |  HTTP + X-Fleet-Token
                           |  (Tailscale, 100.64.0.0/10)
        +------------------+------------------+
        |                  |                  |
        v                  v                  v
  +-----------+      +-----------+      +-----------+
  |  MINING PC|      |  MINING PC|      |  MINING PC|
  |           |      |           |      |           |
  | agent :47800  (Windows service / systemd)       |
  |   MinerService ------ starts/stops xmrig.exe    |
  |        |  loopback :47801, random bearer token  |
  |        v                                        |
  |   xmrig.exe --http-enabled  ---> the mining pool|
  |                                                 |
  |   HardwareService --- LibreHardwareMonitor      |
  |   InstallerService -- GitHub release -> disk    |
  |   miner.json -------- pushed pool/wallet/path   |
  +-------------------------------------------------+

  Console --- HTTPS ---> api.hashvault.pro   (balance, difficulty, price)
  Agent   --- HTTPS ---> api.github.com      (miner install / update)
```

### Design Principles

1. **The agent owns mining on its node.** `stop` terminates *every* `xmrig` process on
   the machine, not only the one this agent spawned. A fleet manager that leaves rogue
   miners running is worse than useless.
2. **Never present a guess as a measurement.** A missing or zero power sensor is
   reported as unmeasured, and the operator's `powerFallbackWatts` wins over the
   agent's own estimate — that number usually comes from a wall meter.
3. **Explain a blank cell.** When a sensor is absent the agent says why
   (`HardwareDto.SensorNotice`), probing the same registry value LibreHardwareMonitor
   itself reads, so the diagnostic cannot disagree with the library.
4. **Installing must not interrupt mining.** `InstallerService` stops the miner only
   when the target directory contains the executable that is currently running.
5. **Every screen has a scriptable twin.** Anything worth watching in the TUI is also a
   one-shot command with a meaningful exit code, for Task Scheduler and cron.
6. **Degrade, never crash.** Pool JSON is read field by field with tolerant fallbacks;
   a renamed field blanks one cell instead of breaking a screen.

---

## Project Components

The solution ([XmrigFleet.slnx](XmrigFleet.slnx)) contains **three** projects.

### 1. **XmrigFleet.Agent**
**Type**: ASP.NET Core Minimal API -> `xmrig-fleet-agent.exe`
**Location**: `src/XmrigFleet.Agent/`
**Purpose**: Runs on every mining PC. The only component that controls XMRig or reads
hardware.

**Key features**:
- Shared-secret middleware comparing `X-Fleet-Token` with `CryptographicOperations.FixedTimeEquals`; an empty token logs a loud warning.
- Environment is forced to `Production` unless `ASPNETCORE_ENVIRONMENT` says otherwise, so stack traces are never served to the tailnet.
- `UseWindowsService()` + `UseSystemd()` are both registered; each is a no-op when the process was not started by that service manager.
- `MinerConfigStore` persists pushed settings to `miner.json` beside the binary; a corrupt file never blocks startup.

**Services**:

| Class | Responsibility |
|-------|----------------|
| `MinerService` | Start/stop/restart XMRig, read `/2/summary` off the loopback API, keep the last 200 output lines |
| `HardwareService` | LibreHardwareMonitor sensors, power estimate, PawnIO diagnostics |
| `InstallerService` | Resolve the right GitHub release asset, download, unpack, repoint the config |
| `MinerConfigStore` | Durable per-node miner settings |

### 2. **XmrigFleet.Console**
**Type**: Console application -> `xmrig-fleet.exe`
**Location**: `src/XmrigFleet.Console/`
**Purpose**: The operator interface — a Spectre.Console TUI, plus one-shot commands for
unattended use.

**Screens** (`Ui/`): `Dashboard` (live table + totals), `MinerScreen` (start/stop/
install/push/logs), `NodesScreen` (discover/add/edit/test), `HardwareScreen`,
`EconomicsScreen`, `PoolScreen`, `SettingsScreen`.

**Supporting services**:

| Class | Responsibility |
|-------|----------------|
| `FleetService` | Parallel poll and parallel command fan-out, error normalisation |
| `AgentClient` | Typed HTTP client for one agent, long timeout for installs |
| `MarketService` | Hashvault wallet/pool parsing, atomic-unit scaling, price, 30s cache |
| `Economics` | Electricity cost, expected income, per-node profit split |
| `TailscaleService` | Parses `tailscale status --json` for node discovery |
| `FleetConfig` | `fleet.json` load/save (override with `XMRIG_FLEET_CONFIG`) |

### 3. **XmrigFleet.Contracts**
**Type**: Class library
**Location**: `src/XmrigFleet.Contracts/`
**Purpose**: The wire contract shared by both sides — `NodeSnapshotDto`,
`MinerStatusDto`, `HardwareDto`, `MinerConfigDto`, `InstallRequestDto`,
`CommandResultDto`, plus `ApiVersion.Current` so the console can warn on a mismatch.

---

## Agent HTTP API

All routes live under `/api/v1` and require the `X-Fleet-Token` header.

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/info` | Hostname, OS, agent version, API version, uptime, elevation |
| `GET` | `/status` | `NodeSnapshotDto` — info + miner + hardware in one call |
| `GET` | `/miner` | Miner status only |
| `POST` | `/miner/start` | Start XMRig with the stored config |
| `POST` | `/miner/stop` | Stop **all** XMRig processes on the node |
| `POST` | `/miner/restart` | Stop, settle, start |
| `GET` | `/hardware` | Sensors, components, power estimate, sensor notice |
| `GET` / `PUT` | `/config` | Read or patch the stored miner config |
| `POST` | `/install` | Install or update XMRig into a target directory |
| `GET` | `/logs` | Last 200 captured output lines |

---

## Configuration

### Console — `fleet.json`

```json
{
  "token": "shared-secret",
  "agentPort": 47800,
  "pollIntervalSeconds": 5,
  "electricity": { "pricePerKwh": 5.0, "currency": "RUB" },
  "pool": {
    "apiBase": "https://api.hashvault.pro/v3/monero",
    "url": "pool.hashvault.pro:443",
    "wallet": "4..."
  },
  "nodes": [
    { "name": "rig-1", "host": "100.119.48.15", "port": 47800,
      "enabled": true, "powerFallbackWatts": 220 }
  ]
}
```

### Agent — `appsettings.json`

```json
{
  "Agent": {
    "Token": "shared-secret",
    "ListenUrl": "http://0.0.0.0:47800",
    "XmrigApiPort": 47801,
    "AutoStartMiner": false
  }
}
```

⚠️ **Both files hold secrets** (fleet token, wallet address). They are listed in
[.gitignore](.gitignore) and must stay out of version control.

---

## Build & Deployment

### Local build

```powershell
dotnet build                                    # whole solution
dotnet run --project src/XmrigFleet.Agent       # agent in the foreground
dotnet run --project src/XmrigFleet.Console     # interactive TUI
```

### Publish and roll out

```powershell
# 1. Self-contained binaries; nodes need no .NET runtime
.\deploy\publish.ps1                            # -Runtime linux-x64 for Linux nodes

# 2. On each mining PC, from an elevated PowerShell
.\install-agent.ps1 -Token "<fleet token>" -SourcePath .\agent
```

`install-agent.ps1` copies the payload to `C:\Program Files\xmrig-fleet-agent`, writes
`appsettings.json`, registers the service with SCM restart actions, opens the port to
`100.64.0.0/10` **only**, and verifies the API answers. For Linux nodes,
[deploy/xmrig-fleet-agent.service](deploy/xmrig-fleet-agent.service) is the systemd unit.

### One-shot commands

```bash
xmrig-fleet status              # exit 1 if any node is unreachable
xmrig-fleet start  [node ...]   # no names = every enabled node
xmrig-fleet stop   [node ...]
xmrig-fleet restart
xmrig-fleet economics
xmrig-fleet pool
```

---

## Security Model

- **Transport is not encrypted.** Confidentiality comes from the tailnet; the token
  only stops other tailnet or LAN hosts from driving the miner. Restrict access further
  with Tailscale ACLs.
- **Token comparison is constant-time**, and a blank token is logged as a warning at
  startup rather than silently accepted.
- **The agent needs Administrator/root** to read sensors and to kill a miner started by
  another user. Elevation is reported in `/info` and surfaced in **Test connection**.
- **XMRig's own API is bound to loopback** with a random per-process bearer token, so
  nothing else on the node can drive the miner through it.

---

## Directory Structure

```
xmrig-fleet/
├── src/
│   ├── XmrigFleet.Agent/          # node agent -> xmrig-fleet-agent.exe
│   │   ├── Program.cs             # minimal API, token middleware, service hosting
│   │   ├── MinerService.cs        # XMRig process control + loopback API reader
│   │   ├── HardwareService.cs     # sensors, power estimate, PawnIO diagnostics
│   │   ├── InstallerService.cs    # GitHub release -> unpack -> repoint config
│   │   └── AgentOptions.cs        # options + miner.json store
│   ├── XmrigFleet.Console/        # operator console -> xmrig-fleet.exe
│   │   ├── Ui/                    # Dashboard, Miner, Nodes, Hardware, Economics, Pool, Settings
│   │   ├── FleetService.cs        # parallel poll and command fan-out
│   │   ├── MarketService.cs       # Hashvault + price parsing
│   │   ├── Economics.cs           # cost / income / profit maths
│   │   ├── TailscaleService.cs    # tailnet discovery
│   │   └── Cli.cs                 # one-shot commands
│   └── XmrigFleet.Contracts/      # DTOs shared by both sides
├── deploy/
│   ├── publish.ps1                # self-contained publish for agent + console
│   ├── install-agent.ps1          # service + firewall + verification on a node
│   └── xmrig-fleet-agent.service  # systemd unit for Linux nodes
├── README.md                      # operator guide (Russian)
├── ProjectContext.md              # this document
└── XmrigFleet.slnx
```

---

## Development Status

### Implemented & Verified Live ✅
- [x] Agent HTTP API with shared-secret auth (unauthenticated request returns `401`)
- [x] Hardware read on real silicon — CPU model, 6c/12t split, motherboard, RAM, GPU temperature/power/VRAM
- [x] Physical-core counting that survives SMT thread-named sensors
- [x] Power reported as measured vs estimated, with the node fallback taking precedence
- [x] PawnIO diagnostic using LibreHardwareMonitor's own detection key
- [x] Adopting a miner started outside the agent (image path read from the live process)
- [x] Install/update: downloaded XMRig `v6.26.0`, unpacked, resolved the executable, and left a running miner untouched
- [x] Release-asset matching by OS **and** architecture (`-windows-x64.zip`, `-linux-static-x64.tar.gz`)
- [x] Hashvault parsing against live data — balance, payout threshold, paid total, network hashrate, XMR price in the configured currency
- [x] One-shot CLI (`status`, `economics`, `pool`, `help`) with meaningful exit codes
- [x] Clean solution build, zero warnings

### Implemented, Not Yet Verified Live ⏳
- [ ] `start` / `stop` / `restart` against a real miner — untested because an unrelated
      XMRig was mining on the only available node and `stop` is fleet-wide by design
- [ ] Interactive TUI rendering in a real terminal (the development session had
      redirected output; the console correctly refuses and prints CLI usage instead)
- [ ] `install-agent.ps1` service registration and firewall scoping on a clean node
- [ ] Linux agent: systemd unit, `linux-static-x64` install path, `/sys` sensors
- [ ] Whether installing PawnIO actually restores CPU temperature and package power

### Planned 📋
- [ ] **Bring CPU temperature and package power online**: install PawnIO on one node,
      confirm the sensors appear, then roll it out fleet-wide and drop the
      `powerFallbackWatts` workaround where real readings exist
- [ ] Hashrate history with a sparkline per node
- [ ] Alerting: node offline, miner dead, temperature over threshold
- [ ] Compare estimated income against Hashvault `dailyCredited` on one screen
- [ ] Per-node XMRig config templates (thread pinning, huge pages, MSR flags)
- [ ] Automatic miner restart when a node reports zero hashrate while mining

### Known Issues / Risks ⚠️
- **`stop` kills every `xmrig` process on the node**, including one an operator started
  by hand. Deliberate, but destructive if a node is shared.
- **Hashrate is unreadable for a miner the agent did not start** — it holds its own API
  token. The console shows `mining (no api)` until the miner is restarted through the
  fleet.
- **Income is an expectation, not a payout.** It is hashrate share × block reward ×
  720 blocks/day. Reconcile against **Pool & wallet**.
- **CPU temperature and package power need PawnIO** ([pawnio.eu](https://pawnio.eu)).
  Without it LibreHardwareMonitor starts and reports only what needs no kernel access,
  which is why GPU figures appear and CPU figures do not. PawnIO's compatibility with
  Windows Memory Integrity (HVCI) is **not stated by its authors** — verify per machine,
  and check `CodeIntegrity/Operational` event 3033 if a driver is blocked.
- **Pushing pool settings does not restart the miner**; the operator must restart it for
  new settings to apply.
- **No automated tests.** Verification so far is manual against a live agent.

---

## Glossary

| Term | Meaning |
|------|---------|
| **Node** | One mining PC running the agent, addressed by its Tailscale IP |
| **Fleet token** | Shared secret in `X-Fleet-Token`; must match `Agent:Token` on every node |
| **Fallback watts** | Operator-supplied power figure used when no sensor measures draw |
| **Sensor notice** | Agent-generated explanation for a missing sensor, shown in the console |
| **PawnIO** | Separately installed kernel driver LibreHardwareMonitor uses to reach MSRs |
| **Atomic units** | Hashvault reports XMR scaled by `config.sigDivisor` (10^12) |
| **Adopted miner** | An XMRig process the agent found rather than started |

---

## Document Information

**Document Version**: v1.0
**Last Updated**: 2026-08-30
**Product Version**: 1.0.0
**Status**: Active
**Repository**: `c:\Repos\xmrig-fleet` (branch `master`)
**Related docs**: [README.md](README.md) (operator guide, Russian)
