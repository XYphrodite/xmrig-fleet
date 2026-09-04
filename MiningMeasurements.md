# Mining Measurements

What each machine actually earns, measured rather than estimated. Kept because calculator
estimates for this fleet have been wrong by 5–10x on three separate occasions, always in the
optimistic direction, and only pool credits have ever been trustworthy.

**Every figure here is labelled.** *Measured* means a pool credited it. *Benchmarked* means the
miner reported a rate with no pool involved. *Estimated* means a third party computed it and it
has not been checked against a payout.

**Last updated**: 2026-09-04 12:00

> No wallet addresses in this file. The Monero address lives in `miner.json` on each node and in
> `fleet.json` on the console; the Tari address lives in `C:\mining\tari-address.txt` on
> `mks68i7rtx`. All three are gitignored or off-repo, and this file is public.

---

## Fleet hardware

| Node | Tailnet | CPU | GPU | Role |
|------|---------|-----|-----|------|
| `re-7lqd67ahcm0r` | 100.89.154.125 | Intel i5 | RTX 3060 12 GB | Dev box, console, Ollama |
| `desktop-ib88isg` | 100.119.48.15 | Xeon E5-2680 v4 | RX 6500 XT 4 GB | Mining node |
| `mks68i7rtx` | 100.105.87.52 | i7-12700KF | RTX 4060 8 GB | Mining node, Ollama host |
| `moscow-enjoyer` | 100.114.217.79 | — | — | Planned; agent not installed |

---

## CPU mining — RandomX / Monero

Pool: Hashvault. This is the fleet's main and only reliably profitable activity.

| Node | Hashrate | Note |
|------|---------:|------|
| `mks68i7rtx` | 7,000–7,100 H/s | *Measured.* Needs a monitor window open in its session — see below |
| `desktop-ib88isg` | 6,273 H/s | *Measured.* Collapses to ~1,340 H/s when huge pages fragment |
| `re-7lqd67ahcm0r` | 990–2,210 H/s | *Measured.* Varies with desktop use |
| **Fleet** | **14.74 kH/s** | **≈46 ₽/day** *measured*, all three nodes up, XMR at 47,558 ₽ |

### CPU findings that move the number

- **A monitor window is worth ~60% on `mks68i7rtx`.** 4,380 H/s with nothing open, 7,092 H/s with
  Task Manager, 7,097 H/s with Resource Monitor. Eleven explanations tested and discarded; the
  cause is still unknown. `SessionMonitorService` keeps one open as a workaround.
- **Huge pages are worth 4.5x on the Xeon.** 5.97 kH/s at 100% allocation against 1.34 kH/s at 11%.
  The remedy is restarting the miner while RAM is free.
- **A hard CPU cap costs more than it saves.** On the i5: 2,210 H/s uncapped, 610 at rung 50
  (27.6%), 325 at rung 25 (14.7%), clean recovery to 2,390 on release. The step down from uncapped
  costs ~45% beyond proportionality — likely L3 eviction while the job's threads are frozen.

---

## GPU mining

### RTX 4060 8 GB — `mks68i7rtx`

Miner: lolMiner 1.98a unless noted. Shares GPU with Ollama; a watchdog stops mining while the
model is in use.

| Algorithm | Pool | Rate | Income | Temp / fan | Verdict |
|-----------|------|-----:|-------:|-----------|---------|
| kheavyhash | unMineable | — | — | — | **Broken.** SRBMiner 3.6.1: `PARSE error: 'params' has wrong number of fields`. lolMiner 1.98a dropped Kaspa entirely |
| blake3_an (ALPH) | unMineable | — | — | — | **Broken.** Pool accepts TCP, never answers the handshake |
| Etchash | unMineable | 31.4 Mh/s | **1.32 ₽/day** *measured*, 86 min | 63 °C / 31% | Worst of the working set |
| FishHash | unMineable | 21.0 Mh/s | **2.96 ₽/day** *measured*, 50 min | 65 °C / 37% | Middle |
| NexaPoW | unMineable | 62–64.6 Mh/s | **4.0 ₽/day** *measured*, two windows | 81 °C / 100% | Best on unMineable; also the most heat |
| Cuckaroo29 (Tari) | Kryptex | 4.48 g/s | **49.5 ₽/day** *measured*, 9 h 25 min | 69 °C / 40% | Current. **12x** the best unMineable result, and the only GPU configuration here that beats its own electricity |

Cuckaroo29 benchmarked at **4.53 g/s** with no pool attached, against a third-party reference of
4.07 g/s — this card runs above spec.

### RX 6500 XT 4 GB — `desktop-ib88isg`

| Algorithm | Pool | Rate | Income | Verdict |
|-----------|------|-----:|-------:|---------|
| pearlhash | unMineable | 7.9 TH/s reported | **0** *measured* | Ran hours at 89 W. **Zero accepted shares.** The card genuinely hashed; the pool credited nothing |
| autolykos2 | — | — | — | **Does not fit.** Needs 6,935 MB, card has 3,883 MB free |
| Cuckaroo29 | — | — | — | **Refused.** lolMiner reports `Active: false (Unsupported device or driver version)` |
| NexaPoW | unMineable | 21.0 Mh/s | ~1.4 ₽/day *estimated* from the 4060's measured rate | Current |
| SHA3x (Tari) | — | — | ~0.04 ₽/day *estimated* | Not attempted. Network 499 Th/s is ASIC-owned |

**This card is a third of the 4060 on the same algorithm** (21.0 against 62 Mh/s), and cannot run
the one algorithm that pays properly. Its ceiling is roughly 1.4 ₽/day.

> **Careful with this node.** It is simultaneously the subject of a separate investigation into a
> session-0 `explorer.exe` that bugchecks it, and any change to its GPU load is a confound for a
> before/after CPU hashrate measurement. It is also memory-tight — a recursive directory scan over
> SSH has knocked it offline. Keep remote commands narrow, and pause the GPU miner while anyone is
> measuring this node's CPU.

### RTX 3060 12 GB — `re-7lqd67ahcm0r`

Not mining by operator decision. Hosts a local model.

---

## Coin and pool economics

| Constant | Value | Date |
|----------|-------|------|
| XMR | 47,558 ₽ / $548.72 | 2026-09-04 11:56 |
| USD | 86.67 ₽ | 2026-09-04 11:56 |
| XTM | $0.00056797 / 0.0492 ₽ (CoinGecko `minotari`, rank 1850) | 2026-09-04 11:56 |
| NEXA | $0.00000101 (rank 1156) | 2026-09-04 |
| Electricity | 6.61 ₽/kWh ($0.076) | — |
| RTX 4060 draw | 115 W limit; `power.draw` unavailable via nvidia-smi on this card | — |
| RX 6500 XT draw | 88.8 W *measured* | — |

### unMineable takes roughly half

**Measured realisation factor: 0.454.** Theoretical gross for NexaPoW at 64.6 Mh/s is ~10 ₽/day;
the pool credited 4.0. Stated fees (1% pool + 2% miner dev fee) explain about three points of the
~55% gap; the rest is the conversion spread into XMR.

Consequence: **the algorithm was never the main lever.** Every live unMineable endpoint clusters
within ~1 ₽/day of every other one after the haircut. Leaving unMineable is worth more than any
algorithm choice inside it.

| Endpoint | State (2026-09-04) |
|----------|--------------------|
| nexapow, blake3, autolykos, etchash, fishhash, kheavyhash | Live (TCP 3333 answers) |
| pyrinhash, karlsenhash, sha3x, kawpow | Dead |

**Payout thresholds decide whether money exists at all.** unMineable's XMR threshold is 0.03 XMR —
about 290–300 days at the measured rate, so the accrued balance was effectively unreachable and was
abandoned at 0.0000088 XMR (≈0.40 ₽). Kryptex's Tari threshold is 200 XTM, roughly 6–13 hours,
paid automatically each hour, no payout fee, 1% pool fee.

---

## Mechanisms found the hard way

These cost hours to find and will cost them again if forgotten.

- **Task Scheduler starts actions at priority 7 (BelowNormal).** With xmrig holding every CPU
  thread, the GPU miner could not submit shares before they went stale: **18% bad-share rate** on
  Cuckaroo29. Setting the task's `Priority` to 4 stopped it dead — valid shares went 18 → 137 with
  the stale count frozen at 4, so 119 consecutive shares landed without a single loss. Both
  `set-algo.ps1` and `set-pool.ps1` now carry `-Priority 4`.
- **On `mks68i7rtx` the GPU miner will not run in session 0.** Launched as SYSTEM it initialises
  both backends and then stops, never reaching worker-thread init. It must run under the logged-on
  account with `LogonType Interactive`. On `desktop-ib88isg` session 0 works fine — this is
  per-machine, not general.
- **On `mks68i7rtx`, `powershell.exe` launched by Task Scheduler as `local` dies instantly with
  `0xC0000005`.** Reproducible. The same script under SYSTEM runs fine. This may be related to that
  node's unexplained `taskmgr.exe` entry-point failure. Consequence: no PowerShell launcher in a
  task on that node — point the task at the executable, and run helper loops as SYSTEM.
- **`Get-NetTCPConnection` returns nothing from a service context on `mks68i7rtx`.** Read the TCP
  table directly with `[System.Net.NetworkInformation.IPGlobalProperties]` instead.
- **Ollama binds the tailnet address, not loopback** (`100.105.87.52:11434`). A watchdog polling
  `127.0.0.1` sees nothing.
- **sshd on `mks68i7rtx` resets roughly every other connection and has no working sftp.** Long
  sessions do not survive — a 30-minute measurement was lost this way. Push files as base64 in
  chunks, keep commands under the ~8 KB command-line limit, and retry everything.
- **A downloaded miner must be `Unblock-File`d** or cmd refuses to execute it with "access denied".

### GPU/model coexistence on `mks68i7rtx`

`C:\mining\gpu-guard.ps1`, scheduled task `xmrig-fleet-gpu-guard`, runs as SYSTEM.

Stops mining while any connection is open to Ollama's port, resumes after **300 s** of quiet. The
trigger is a connection, not a resident model: a model stays in VRAM for over twenty minutes after
one question and costs no GPU time while it does.

| Model speed | Measured |
|-------------|---------:|
| While the card mines | 19–24 tok/s |
| With the card free | **53 tok/s** |

Duty cycle measured over one hour of ordinary use: **90% mining**. Over a subsequent 9.4 h stretch:
**100%** — 453 samples, every one with the miner up, because nobody asked the model anything for
eleven hours. The feared 33% did not materialise, but both figures say more about how the model was
used that day than about the watchdog. Duty cycle must be read from `earn2.csv` over a working day
before it means anything.

---

## How measurements are taken

Two scripts on `mks68i7rtx`, both installed this session:

- `C:\mining\earn-log.ps1` → scheduled task `xmrig-fleet-earn`, SYSTEM. Appends to
  `C:\mining\earn2.csv` every minute: timestamp, algorithm, whether the miner was up, the
  unMineable balance (every 5th sample), and the miner's accepted-share count.
- `C:\mining\analyze.ps1` → groups the CSV by algorithm and reports XMR/day and ₽/day, counting
  only the minutes the miner was actually running.

`C:\mining\set-pool.ps1 -Algo X -Pool Y -User Z` re-registers the mining task for any algorithm and
pool. `set-algo.ps1` is the unMineable-shaped shortcut.

**The protocol that works**: switch, let it run at least an hour, read what the pool credited,
divide by minutes actually mined. Nothing else has been reliable.

---

## Open questions and future tests

Ordered by expected value, not by effort.

### 1. Monero + Tari merge mining — the largest unexplored lever

Tari is merge-mineable with Monero's RandomX. The fleet already runs 14.3 kH/s of RandomX around
the clock, so merge mining would earn XTM **on top of** existing XMR with no extra hardware and no
extra watts. Requires a full `monerod`, a Tari base node, and the Tari merge-mining proxy — or a
P2Pool-based stack. Pools supporting it were experimental as of 2024.

**Test**: stand the stack up on one node, point that node's xmrig at the proxy, and compare its XMR
credit before and after — merge mining must not reduce it — while watching XTM accrue.

### 2. Does the Tari payout actually arrive?

Everything about Tari so far is a pool-side balance, not received money. After 9 h 25 min:
**44.30 XTM confirmed, 348.55 XTM pending, 0 paid.** Kryptex pays from the confirmed balance at a
200 XTM threshold, and pending coins mature after 60 blocks — so the first payout is still ahead.

**Test**: read the Tari wallet after 24 h. Until coins land, treat 49.5 ₽/day as unconfirmed. This
is the same discipline that caught pearlhash paying zero while the card looked busy at 89 W.

### 3. Is the 0.454 unMineable haircut real?

Measured once, on one algorithm. If it holds, direct pools are worth roughly 2x on every card.

**Test**: mine NexaPoW on a direct pool (Kryptex runs one) with a Nexa address for one hour, and
compare against the unMineable measurement for the same card and algorithm. Clean A/B — same
algorithm, same hardware, two pools.

### 4. Regional pool server

Kryptex's dashboard suggests the RU server would give a higher effective hashrate than the global
one currently in use (24 ms latency).

**Test**: one hour on each, compare valid-share rate. Cheap, and stale shares are unpaid work.

### 5. Watchdog cooldown

300 s was chosen deliberately, and one hour of observation showed 90% duty. But that hour was quiet.

**Test**: let `earn2.csv` accumulate a full 24 h, compute the real duty cycle from the `up` column,
then decide whether 90 s would recover meaningful time.

### 6. Power limit versus heat

The 4060's `power.draw` is not readable through nvidia-smi on this card, so the 115 W figure is a
limit, not a measurement.

**Test**: step `nvidia-smi -pl` through 115/100/85/70 W, recording hashrate and temperature at each.
Produces the heat-per-rouble curve — which matters here because the heat is wanted.

### 7. PawnIO

Still unresolved from before this work: CPU temperature and package power need it, and nobody has
installed it on a node to see whether the sensors appear.

### 8. Automatic huge-page recovery

The Xeon loses 4.5x when huge pages fragment, with no other symptom. The `Pages` column exposes it,
but recovery is still manual.

**Test**: a rule that restarts the miner when allocation drops below a threshold and free RAM allows
— and a measurement of how often that actually fires.

### 9. Generalise the recorder

`earn-log.ps1` only knows unMineable's balance API. Kryptex's balance lives behind a
client-rendered page and had to be read externally.

**Improvement**: record `(node, algorithm, pool, balance)` from a per-pool adapter, so any future
comparison is automatic rather than hand-assembled.

---

## The honest summary

At 6.61 ₽/kWh, a GPU drawing ~110 W costs about **17.5 ₽/day** to run.

**Every algorithm tried on unMineable was net-negative** against that, by 13–16 ₽/day. On that
evidence GPU mining here was defensible only as resistive heating that returned part of its cost.

**Tari on a direct pool broke that.** 49.5 ₽/day measured against 17.5 ₽/day of electricity is
**+32 ₽/day net** — the first configuration in this fleet where the card pays for itself and then
some. One RTX 4060 now earns roughly what all three CPUs earn together (46 ₽/day). The lever was
never the algorithm; it was leaving a pool that kept 55%.

Two things temper that. The 49.5 ₽/day is nine hours old and **no payout has arrived yet**, and the
9.4 h that produced it ran at 100% duty because nobody used the local model. Both need a full day
before the number is safe to plan around.

The CPU fleet remains the steady earner at **46 ₽/day** and needs nothing but the machines staying
on. The cheapest improvement available is still a node that is switched off, not an algorithm.
