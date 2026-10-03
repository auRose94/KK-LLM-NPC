# Models that play well — the living compatibility list

A long-term record of which local models actually drive good NPCs in KK-LLM-NPC, with
ranking, per-family quirks, and (planned) in-game benchmarks. Updated from real playtests;
the `report_issue` AI-feedback log (`BepInEx/config/kkllmnpc_ai_reports.log`) and the mod's
own logs are the evidence trail — a model's entry should cite them.

## How the mod adapts per family (automatic)

`src/ModelFamilies.cs` classifies the loaded model id and adjusts handling — no config
needed: think-block stripping (Qwen3/GLM/Bonsai/deepseek-style reasoners), token headroom
on top of `MaxTokens` for hidden reasoning, tight payloads for chatty small families, and
a vision-capable flag. New families: run a playtest, note behavior, add a profile + test.

## Ranking (field status, Oct 2026)

Ranking is observational for now — no formal benchmark has run yet (see plan below).
Tiers reflect playtest experience so far.

| Rank | Family / examples | Think blocks | Vision | Tier notes | Field notes |
|------|-------------------|--------------|--------|------------|-------------|
| 🟢 1 | **Qwen** (`qwen3:4b..32b`, VL siblings, QwQ) | yes (stripped) | yes | sized normally, 128K-class | The bar. Reads first-person scenes well ("no problem looking at a scene in a picture and commenting on it"), spatial reasoning + console discipline, tidy JSON. |
| 🟢 1 | **ZDTaichu** (`zdtaichu5.0-9b`, GGUF+mmproj) | yes (stripped) | yes (mmproj) | 9B → small | Qwen3.5-9B backbone + spatial/embodied-AI training mix — "really fast 3D spatial reasoner". Card: temp 0 + top_p 0.95 + top_k 20 for spatial/grounding → set `[VisionModel] Temperature=0`. Great as the spatial captioner or an action/console model. |
| 🟢 1 | **Gemma** (`gemma-3-*) | no | yes | — | Literal template, strict-JSON friendly; no reasoning overhead. |
| 🟢 1 | **Muse Glimmer** (30B-A3B, `meta/muse-glimmer`) | no (128 headroom) | yes (native) | medium | **Validated exceptional (2026-10-02 playtests):** "really responsive and aware… truly impressive" — set its own goals ("Be by the blenders with player"), completed them unprompted, chatted naturally; prefers follow-mode travel once learned. Caveat: can be SLOW to figure out station/partner mechanics from raw text — compensated by structured data now (slot occupancy, cannot_use reasons, energy/food rules). (The follow freeze + the Nib console log were mod bugs, fixed — not model issues.) |
| 🟡 2 | **GLM** (`glm-4.x-flash`) | yes (stripped) | yes | — | Good console discipline, think-strip handled. |
| 🟡 2 | **Gemma** (`gemma-3-*) | no | yes | — | Literal template, strict-JSON friendly; no reasoning overhead. |
| 🟡 3 | **Ornith** (~4B) | no (256) | no | small | Small/lean; keeps payloads tight; the original playtest NPC. |
| 🟡 3 | **LFM (Liquid)** (`LFM2-*`) | no (128) | no | small | Compact efficient hybrids. |
| 🟡 3 | **Bonsai** (~4B) | yes (stripped, 512) | no | small | Tiny reasoner-distill; short but real CoT. |
| 🟡 3 | Vision captioners (llava/moondream) | no | as captioner | small | Caption-only — route via `[VisionModel]`, not as the action model. |
| 🔴 4 | **Nemotron** | yes (chatty, 768) | no | small | Heavy CoT + chatty; stripped now, but historically flopped. Retest candidate. |
| 🔴 4 | **Granite** | yes (verbose, 768) | no | small | Verbose contracts; same story. Retest candidate. |

Unknown-but-classified ids land as `generic` (runtime auto-detect decides tier); the
startup line `model family 'x' from '<id>'` always shows what was chosen.

## Benchmark plan (what a real ranking needs)

1. **A reproducible obstacle-course checklist** per model, same save/scene:
   (a) possess + greet; (b) navigate to a station behind a closed door; (c) grab →
   carry → drop on a target (seed on soil + use); (d) follow the player across a
   jump-trail route; (e) answer a [chat] question without repeating itself.
2. **Metrics, all already logged**: turns-to-task (from `KKLLMNPC: LLM loop` + tool
   history lines), tool error rate + which tools (the report log's last-10-tool-calls),
   say-suppression/self-reply incidents, model_error/format retries.
3. **Ground truth**: `kkllmnpc_ai_reports.log` (the NPC's own complaints) + BepInEx log
   parsing; the report tool exists precisely so benchmark runs produce their own
   evidence.
4. **Ranking output**: turns per task + complaint count per model → update this table's
   rank column with the numbers.

## Contributing a result

Playtest with one model, note: family line from the log, what worked/failed, the report
log contents. Add/adjust the row above (keep ranks evidence-backed), and if needed a
family profile in `ModelFamilies.cs` + its test. Benchmarks don't need code changes —
they need the checklist and the logs.