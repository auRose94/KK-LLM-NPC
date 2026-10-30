# Vision-Cortex Data Dumper — Spec

Status: design (not built). Target: add a dataset-emitter module to the KK-LLM-NPC
BepInEx plugin that writes per-frame training data for a future vision model.
Author's design consult, 2026-10-23 → 24. Deferred to hardware; this doc is the
contract before code.

---

## 1. Goal

Produce, on demand, a **labeled** training frame: one image the future VLM sees at
runtime, plus a `meta.json` carrying engine ground truth no screenshot/document
dataset can supply. The image is the *deployment format* so training format =
runtime format — no translation layer.

Two capture vectors (see §7): the agent's own stereo eyes (primary), and an
optional player-POV camera.

---

## 2. Frame layout — 4-quadrant composite

One 1024×1024 image, four 512×512 quadrants:

| Quadrant | Content |
|---|---|
| Top-left | **Left eye**, RGB |
| Top-right | **Right eye**, RGB |
| Bottom-left | **Left eye**, linear depth, 8-bit gray |
| Bottom-right | **Right eye**, linear depth, 8-bit gray |

When stereo is off, L and R are the same camera → duplicated quadrants, tagged
`stereo: false` in meta.

**Why a single composite (not 4 separate images):** a 1024² image is one vision-
encoder pass (~342 tokens after 2×2 patch merge on Qwen3-VL) instead of four
separate image blocks. Keeps the per-turn token budget sane in an already
clog-sensitive context.

### Dataset files per frame

```
<dataset>/<frame_id>/
    frame.png      # 1024² composite (the training image — exactly what the model sees)
    depth16.png    # 1024², 16-bit linear depth for the two bottom quadrants ONLY
                   #   (TL/TR blank). Precision layer for metric-depth supervision.
    meta.json      # ground truth (schema §5)
```

- `frame.png`: TL/TR = eye RGB (8-bit); BL/BR = 8-bit linear depth normalized to
  `[near, far]`. This is the model's actual input.
- `depth16.png`: 16-bit linear depth (R16) for the bottom half, so any model that
  wants *precise* metric depth has it without bloating the main frame. TL/TR are
  blank (0). Optional precision layer — the model trains on `frame.png` + meta.
- `meta.json`: see §5.

**Depth precision note:** 8-bit over the default 0–80 m far plane wastes ~all
precision past ~30 m, but everything this mod cares about (grabs/vore/penetration)
happens inside 4 m, where 8-bit is still fine. `depth16.png` is the safety net for
models that want metric depth. Keep `farClipPlane` modest (default 80 m → consider
a dumper-specific 20–30 m) so near-field resolution stays high.

---

## 3. Depth capture mechanism

Game is Unity 2021.3.45, **built-in RP** (no URP). Per-eye depth via a **depth
replacement shader**:

1. Build a `Shader` that, in its pass, writes normalized linear depth to the
   color output (a vertex-passthrough fragment: `o = UnityObjectToClipPos(v.pos);
   gl_FragColor = ...depth from o.w...`). No post-process needed.
2. Per eye: `_cam.depthTextureMode = Depth`; `camR.RenderWithShader(depthShader,
   "")` into a 512×512 RT; `ReadPixels` → Texture2D; map to 8-bit (frame.png) and
   16-bit (depth16.png).

Alternative (heavier): `depthTextureMode = DepthAndDepthTex` + a grab of
`_CameraDepthTexture`. The replacement-shader route is cleaner and self-contained.

**Transparents:** the depth shader must handle the scene's transparent objects
(they can be in front of solid geometry). Either render them in the depth pass or
accept minor artifacts at glass/window edges — note it in meta (`depth_transparent:
"approx"`).

---

## 4. Integration — where the code plugs in

The mod is built around a reflection-driven **module registry**
(`ModuleRegistry.cs`): modules expose `static Register()` that calls
`ModuleRegistry.Perception(hook)`, `Tool(...)`, `PhysicsTick(...)`. The dumper is
one such module — no shared-file edits.

| Need | Hook / API | Location |
|---|---|---|
| Render eye RGB | `_cam.Render()` + `ReadPixels` + `EncodeToJPG` | `Senses.cs:2241 CaptureImageBytes()` (reuse the exact render/read/stitch path) |
| Depth pass | new depth RT + replacement shader (§3) | new |
| Entity ground truth | `DescribeNearby()` — world pos, bounds, facing, distance, `CleanName`/`ClassifyUsable` | `Senses.cs:1206` |
| Entity projection (world→screen bbox) | `Camera.WorldToViewportPoint` per eye, normalize to quadrant | new |
| Cadence / main-thread render | `PhysicsTick` or a config-gated periodic capture | new |
| Disk write | extend the existing `DumpVisionFrame()` path | `Senses.cs` (vision debug dump) |
| Config | `ConfigEntry<T>` fields, same pattern as `_cfgVision*` | `NPCInstance.cs:49+` |

**Cadence is decoupled from the vision pass.** The vision worker captions on its
own schedule; the dumper captures at its own. Capture is on-demand `cam.Render()`
so there is **zero GPU cost between samples** — the eyes aren't rendered every
frame unless a sample is due.

---

## 5. meta.json schema (the gold)

```jsonc
{
  "frame_id": "20261024-153012-00042",
  "timestamp": 1790000000.123,
  "source": "agent",                 // "agent" | "player"
  "agent": {
    "id": 42, "name": "Kobold", "class": "kobold",   // species from genes
    "claimed_by_llm": true, "agent_name": "Grib"     // if a sibling agent
  },
  "camera": {                        // per eye; player POV has one entry, source="player"
    "L": {
      "pos":     [0.0, 1.2, 0.0],    // world, meters
      "forward": [0.0, 0.0, 1.0],    // normalized
      "up":      [0.0, 1.0, 0.0],
      "fov": 90.0, "near": 0.1, "far": 80.0,
      "ipd": 0.063,
      "vel": [0.1, 0.0, -0.3],       // linear, m/s (from transform delta / dt)
      "avel": [0.0, 0.4, 0.0]        // angular, rad/s
    },
    "R": { ... }
  },
  "entities": [                      // projected annotations — the training labels
    {
      "id": "Kobold(Clone)#1847",     // gamedev/engine name (Unity name + instanceID)
      "natural_name": "Kobold",       // CleanName (strips "(Clone)")
      "natural_class": "kobold",      // ClassifyUsable/ClassifyByType result
      "category": "kobold-agent",     // player | kobold | kobold-agent | usable | station | item
      "world_pos": [1.2, 0.0, 3.4],
      "bounds": [0.6, 1.4, 0.6],      // size (m)
      "facing": [0.0, 1.0, 0.0],
      "distance": 3.6,                // m from eye
      "height_rel": "level",          // above | below | level
      "relative_vel": [0.0, 0.0, -0.2],
      "visible": true,                // raycast from eye to entity center
      "bbox_L": [0.52, 0.31, 0.18, 0.44],  // x,y,w,h NORMALIZED 0..1 within the L quadrant
      "bbox_R": [0.49, 0.31, 0.18, 0.44]
    }
  ],
  "scene": { "room": "MainMap", "area": "Farm" },   // if derivable
  "consent": { "frame": null },        // consent-frame state (see §8) — null = none
  "state": {                          // context that makes the frame meaningful
    "goal": "find_food", "held": null, "belly_guests": [], "arousal": 0.0
  },
  "stereo": true,
  "depth": { "near": 0.1, "far": 80.0, "units": "meters", "transparent": "approx" }
}
```

### Two-axis entity vocabulary (train both)

Every entity carries **both** names so the model learns them together:
- `natural_name` / `natural_class` — the human-facing label (`CleanName`,
  `ClassifyUsable`) the model can *voice* in RP.
- `id` (gamedev/engine name) — the machine-consumption label, like the radar's
  internal naming. "Grabber (claw machine)": the natural class + the engine id.

**One vocabulary across vision and text.** The entity set here is the same
`DescribeNearby()` set the text senses already feed the decision model, so the
decision model's existing knowledge of these names transfers to vision output
with zero relearning.

---

## 6. Sampling cadence

- **Event-driven** (high value): grab engaged, swallow, held-person arrival,
  state change (goal/held/belly). These are the frames that teach *action↔vision*.
- **Periodic poll**: 1–2 Hz while `DumpData` is on.
- **Dedup**: perceptual hash on the composite; skip near-duplicate frames (same
  balance philosophy as the nav-event reduction).
- **Cost**: capture is on-demand render, so idle = no GPU spend.

---

## 7. Player-POV vector (optional)

A second capture vector: a UI-culling camera parked at the player transform,
`source: "player"`. **Never a screen grab** — the HUD must never appear in
training data; use a culling mask that excludes the UI layer.

- Metadata: `source: "player"`, single camera entry at the player transform.
- Mono games have one camera → duplicated L/R quadrants, `stereo: false`.
- Value: (a) more training volume; (b) a known-good reference view per scene —
  player POV is the cleanest canonical angle, useful as an evaluation anchor and
  for teaching that eye height varies per agent while the world doesn't.

---

## 8. Safety coupling (ties to the NSFW stance)

The dumper records `consent.frame` state per frame so the dataset can later
support the consent-frame mechanism (CNC-with-frame vs. no-frame). This keeps the
data collection aligned with the runtime safety ladder — a frame's consent state
is ground truth the model can learn to read. Not a behavior change to the dumper;
just an extra meta field sourced from the existing consent detector.

The `entities[].category` labels must key on **behavior/role, never species**
(see [[nsfw-safety-stance]]): the detector's not-a-person rule reaches through the
social script, not the fantasy wrapper. The dumper records what *is*, so this is
mostly a labeling-discipline note for the corpus curation step.

---

## 9. Phased build plan

1. **Color composite + meta skeleton.** Reuse `CaptureImageBytes()` at 512²/eye →
   1024² composite. Write `frame.png` + minimal `meta.json` (camera + entities from
   `DescribeNearby()`, projected bboxes). Config `_cfgDumpData` + `_cfgDumpEvery` +
   `_cfgDumpDir`. **This alone produces usable data.**
2. **Depth quadrants.** Add the depth RT + replacement shader (§3) → BL/BR +
   `depth16.png`.
3. **Cadence + dedup.** Event hooks + perceptual-hash skip.
4. **Player-POV vector** (§7).
5. **Consent/state fields** (§8) once the consent-frame work lands.
6. **Corpus curation + HF packaging** (SFW-filtered subset, gated repo — the
   gated release is the stance's public face).

---

## 10. Open questions

- `farClipPlane` for the dumper: default 80 m vs. a tighter 20–30 m for near-field
  depth resolution. Lean tight.
- Whether to store `frame.png` as the *only* image (8-bit depth quadrants) and
  drop `depth16.png` unless a model needs it — keep it optional from day one.
- Entity set scope: limit `entities[]` to `DescribeNearby()`'s 14 m sphere, or
  extend to all claimed/spectated bodies for wider FOV coverage.
- `state` block: does the model need goal/held/belly in the training frame, or is
  that context noise for pure perception? Lean include (it's free and action-
  conditioned frames are the point).
