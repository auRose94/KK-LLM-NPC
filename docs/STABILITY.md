# Stability — long-session testing guide

Practical notes for running KoboldKare + KKLLMNPC + local LLM servers on one
machine for LONG sessions, written from a real incident. This is the living
checklist behind the project's stability direction (see CHANGELOG v1.7.0+).

## The 2026-11-18 incident, with receipts

Hardware: AMD Strix Halo APU (Radeon 8060S, **unified memory** — CPU and GPU
share one 117Gi RAM pool), CachyOS, game under Proton, LM Studio serving
gemma instances on the same die.

Timeline from the journals:

| Time | Evidence |
|---|---|
| 11:39:26 | game session active (`output_log.txt` last write) |
| 11:39:56 | kernel: `amdgpu: ring gfx_0.1.0 timeout` → `GPU reset end with ret = -110` → **`GPU Recovery Failed`** → `MES failed to respond to msg=RESET` |
| 11:40:28 | display dead; reboot |

Diagnosis: a GPU command stream hung (ring gfx timeout). On an APU the same
gfx rings serve the game's rendering AND local-LLM compute (Vulkan); when
amdgpu's own recovery fails (`-110`), the only exit is a reboot. This is
compute/display contention on shared silicon — **not** an engine fault, and
raw RAM was never the limit (117Gi).

## Long-session checklist (APU / shared-graphics machines)

1. **Cap the game's FPS** (in-game VSync, or a frame limiter). Sustained
   lighter frames = cooler APU (CPU+GPU share one thermal budget) and
   shallower command queues — the single best stability lever.
2. **Trim the heavy in-game graphics** for testing sessions: shadows and
   post-processing first. Shorter frames leave queue room for the LLM's
   compute bursts.
3. **Keep model instances modest** — `[Ollama]/[LMStudio] Replicas = 2` +
   server-side per-model concurrency beats 4 loaded instances (see the
   v1.6.0 VRAM warning; 4× a multi-GB model black-screened this machine).
4. **Separate workloads when you can** — the second-machine instinct is
   correct: serving models off-box removes the compute/display contention
   entirely. Same-box alternative: pause heavy inference while actively
   rendering.
5. **Watch for the warning signs live** — in a side terminal:
   `journalctl -kf | grep -iE "amdgpu.*(timeout|reset|recover)"`.
   A `ring ... timeout` line means the display is about to go down; save
   your work. `radeontop` / `mangohud` show the shared-DRAM and ring load.
6. **Keep Mesa current** (CachyOS ships fresh) — Strix Halo support is
   actively maturing upstream; several early gfx1150 hang classes are fixed
   kernel-side.
7. What NOT to blame: the engine, for this class of event. The Unity-layer
   issues in the same logs (per-frame NREs, coroutine aborts) ARE engine/game
   bugs and the mod patches those — see the v1.7.0 JigglePhysics guards.

## What the mod already patches (stability direction)

- `BreedingStation.CanUse` NRE per physics frame (v1.3.0 guard).
- `JiggleSkin.UpdateMesh` per-frame NRE + `JiggleSkin.OnDisable` stale-rig
  deref that aborted the game's player-model swap coroutine (v1.7.0 guards).
- Startup probe no longer blocks the chainloader (~12s, v1.7.0 async).
- Stuck-breaker replan storms, vision hammer, salvage loops (v1.5.x–v1.6.0).

## Package currency (checked 2026-11-18, CachyOS / Strix Halo)

Already current: `mesa-git 27.0.0-devel`, `linux-firmware 20261019` (carries
the gfx1150/MES microcode — the reset path in the incident receipts), kernel
`linux-cachyos 7.3.5`, `libdrm 2.4.134`, `llvm-libs 23.1.5`. The morning hang
happened ON this stack — currency is necessary but not sufficient.

The two levers not yet pulled:

1. **NTSYNC** — the kernel module is built and loaded (`CONFIG_NTSYNC=m`),
   but the game runs stock Proton 11.0-2. Newer Proton builds
   (`proton-cachyos`, or `PROTON_USE_NTSYNC=1` on stock Proton) switch the
   game's synchronization primitives from fsync-style emulation to native
   ntsync — a known fix for CPU-side stalls/hangs under Proton. Change ONE
   variable at a time so a difference is attributable. Verify the module is
   actually engaged while playing: `lsmod | grep ntsync` should show a
   nonzero refcount.
2. **MangoHud as verification** — pressure-vessel picks the NEWER of the
   Steam-runtime vs system Mesa, so your mesa-git should win; confirm it
   rather than assume: `mangohud %command%` in launch options shows the live
   RADV/driver string plus frametime spikes (a stutter climb is the early
   warning of the kind of submission storm that precedes a ring timeout).

Optional, advanced: `umr` (AUR) — AMD's GPU register/ring forensics; if a
hang recurs it can dump what the ring was executing, which turns "driver
reset, cause unknown" into a filing.

## When it happens again — collect these

1. `journalctl -k -b -1 | grep -iE "amdgpu|gpu"` (the kernel receipts).
2. `BepInEx/LogOutput.log` + the game's `output_log.txt` (mod vs engine).
3. What LM Studio/Ollama was doing at the time (instances, request load).

Post them to a bug report — the kernel line is the fingerprint that says
"driver/hardware" vs "engine" vs "mod".
