## More roleplay signal, less context spam

Your two asks are in tension (more events vs. less context). The balance you asked for is: **events ride the existing `[event]` queue (cheap, deduped) gated behind a default set, and the agent can silence categories it finds distracting via a filter tool; meanwhile the *most* valuable — live body state — becomes pollable instead of event-driven.** Pain/sweat/temperature/dirt aren't player mechanics (confirmed absent), so we don't fake them; we surface what the game *can* give.

---

### 1. A default event set + an agent-controlled filter tool
Reuse the live `PushWorldEvent` → `_worldEvents` (CarrySense.cs:28/31/42) → `DrainReagentEvents` (Body.cs:887) → `result["consumed"]` (Senses.cs:278) channel — no new queue.

- **Category tags.** Every `[event]` string gets a short tag prefix it already has (`[sex]`, `[lay]`, `[event]`, `[overheard]`, danger, new `[sensation]`). I'll normalize these to a canonical set of categories.
- **Filter tool** — new `event_filter`/`set_events` tool (like `say`): `category` + `action` (`off`/`on`). Sets per-category allow flags the drain step checks. Danger is **never** suppressible. Default = all major categories allowed; the agent can turn off e.g. `[sex]` gossip it finds distracting, then turn it back on later.
- **Scope = the curated default set** (major transitions only, no per-tick micro-changes — avoids the clog):
  - climax (`UpdateHorniness` high→low crossing, Movement.cs:372 — currently sets `_horny` but does *not* event)
  - penetrate/exit, carry/grab/release, egg-lay readiness, vore-sate, body change on possession — these already fire events, keep them; they're the good ones
  - a thresholded "big arousal/stim jump" event (the game already watches `stim > _lastStim + 0.08f` at Movement.cs:1018 — piggyback)

### 2. Sensation change detection (producers)
`PollSensation()` beside `PollCarrySense()` in a new `SensationSense.cs`, called from `FixedUpdateSafe`. Compares current `_horny` + normalized `stim`/penetration state to last-seen; fires a thresholded event on a real transition (not per frame), deduped by a per-category cooldown. Keeps the code where carry-sense already lives so event production is centralized and easy to read.

### 3. Look-at tool — aim, then tell
New `lookat` tool (Tools.cs, wired into `RunTool` + `ActionParamProps`, namespaced in `enumVals`):
- **Target by `id`** (an id the model already sees in `nearby`) **or by `yaw_deg`/`pitch_deg`**. Resolves id → cached world position, then turns the body via the existing `ToolLook` machinery (Tools.cs:894) and fires a ray, returning live **position + what's hit** (name, kind, distance, facing, category) — so the model can route toward it and react in this turn.
- **"Properly looking" event:** if the ray lands on a partner or a notable usable, push a `[looked]` event **to bystanders via `FanoutWorldEventExcept`** (Main.cs:171) — other agents see *who* is looking *at what*. This is the raycast info passed as an event, exactly as you described.

### 4. Pollable body-state block (the real value)
Add a compact `sensation` block each turn via a `ModuleRegistry.Perception` hook (Senses.cs:356 — the clean per-module hook, no Llm/Senses edits needed), so the model reads live game state without waiting for an event:
- arousal/stim level + trend, current penetrations (who/what, hole, depth, velocity) from `_pen`/`_dickIn`, egg/belly volume + readiness, grabbed flag, in-station flag. This is "body state the game can deliver" surfaced as pollable data rather than event spam.

### 5. Pathfinding — fewer replans + quieter nav (the frame-drop + clog fix)
Two distinct problems, fixed separately:
- **Context clog first (your priority):** `[nav]` events fire on nearly every replan and pile into the `[event]`/history rings. Rework `NavOutcome` to fire only on **outcomes** that matter (arrived, blocked, no-route, big divergent replan) with a longer dedup, not on every internal replan. Fewer `[nav]` = less clog; the new `lookat` gives the model on-demand position knowledge so it doesn't need a nav event to know where things are.
- **Frame drops (investigate-safe subset):** `Follow`-mode currently replans aggressively via `PathPolicy.ShouldReplan` (PathCore.cs) — add a per-NPC replan **rate-limit** so it can't plan every physics frame; the follow path is cheap to cache when identical. The bigger win (pooling A* scratch beyond `N >= 262144`, Finding.cs:474) is the risky part — I'll gate it behind a config flag or defer it to a follow-up rather than risk the shared-grid correctness.

---

### Order
1. Sensation change detection (`PollSensation` / `SensationSense.cs`).
2. Filter tool (`event_filter`/`set_events`) + category gating in drain.
3. `lookat` tool + `[looked]` bystander event.
4. Pollable `sensation` perception block.
5. Nav-event reduction + follow-mode replan rate-limit.
6. Build + tests.

I'll wire everything to the existing partial-class/tool/registry patterns and run the full suite after each chunk. The only speculative risk is #5's perf path — I'll keep it conservative and flag it.