## Ollama as First-Class Host (adopt external, discover, compaction, WebUI)

### The constraint shaping everything
The game (Proton/wine) **cannot spawn Ollama** from inside the game. So "manage" Ollama = **adopt an externally-running instance** + discover + monitor — not spawn (unlike kk-serve, which spawns its python companion). This is actually simpler: no spawn watchdog, just health + model discovery.

### The bug this also fixes
Compaction **never fires for Ollama**: `ContextCore.FillRatio()` returns 0 when `ModelProbe.DetectedContextLength == 0`, and Ollama was never probed for its context window (only KoboldCpp's `/model` sets it). Fixing discovery automatically makes compaction work for Ollama users.

### Ollama API facts (verified)
- `/api/tags` → `{ "models": [{ "name": "tev1:4b", "family": "qwen2", "format":"gguf", "size":<bytes>, "digest":"sha256:..." }] }`
- `/api/show` → `info.info.context_length` (the real window) + `general.architecture` (family for `ModelFamilies` classification) + quantization
- `/v1/models` (OpenAI) also works as fallback
- No `/health` or `/shutdown` for the server — liveness = a probe to `/api/tags` or `/v1/models`. Serve-time `num_ctx` is a modelfile setting, not per-request.

### Files & changes

**1. New `src/Ollama.cs`** — external-adoption watcher thread, mirroring KKServe's "health truth beats process truth" discipline (minus spawn):
- Watcher thread polls the running Ollama at an interval.
- `_adopted` = true once an Ollama answers a probe (`/api/tags` first, then `/v1/models`).
- On adoption: fetch installed models + current model info (context length, architecture).
- `EndpointOverride()` returns the Ollama `/v1/chat/completions` base when adopted and healthy, gated by a new `[Ollama].AutoEndpoint` flag — so an existing LM Studio setup is never hijacked.
- `Snapshot()` / `HandlePost()` transport over the existing WebUI HTTP server, mirroring KKServe.

**2. New `src/OllamaArgs.cs`** — pure, game-free logic (unit-testable):
- Parse an Ollama endpoint to derive its base URL (strip `/v1/chat/completions` etc.).
- Compute a safe `num_ctx` given the model's reported context (the "fit within window" math).
- Parse `/api/tags` + `/api/show` JSON into a list of `{name, family, contextLength, size, digest}`.

**3. `src/ModelProbe.cs`** — add an Ollama probe branch in `Probe()`:
- Try `/api/tags` → sets `DetectedModelName` + `AvailableModels` (Ollama tags as `id`).
- Try `/api/show` for the current model → sets `DetectedContextLength` (the window) + `DetectedTier` (architecture) + `DetectedQuantization`.
- Falls back to existing `/v1/models` + KoboldCpp `/model` if Ollama isn't the host.

**4. `ContextManager` / `ContextCore`** — compaction now works for Ollama automatically once `DetectedContextLength` is set. No new policy — just no longer silently returns 0.

**5. WebUI (`WebUI.cs`)** — an `Ollama` section:
- Adoption status + installed-models list (from `/api/tags`) + a **"Use this model"** button that fills `[LLM].Model`.
- LLM model field becomes a `<select>` of Ollama's installed models (populated from the probe), falling back to free-text if Ollama isn't detected.

**6. Config (`Main.cs`)** — a new `[Ollama]` group:
- `Enabled` (adopt running Ollama), `Endpoint` (default `http://127.0.0.1:11434`), `AutoEndpoint` (route LLM traffic when healthy), `PollIntervalSec`, `Model` (dropdown target).

**7. `tests/test_ollama.cs`** — register `truncatedargs`-style suite for `OllamaArgs.cs` parsing (tags/show parsing, endpoint/context math, free-text fallback).

### Order of work
1. `OllamaArgs.cs` (pure) + `test_ollama.cs` → build & test.
2. `ModelProbe.cs` Ollama discovery branch.
3. `Ollama.cs` watcher (adoption + model info + endpoint override).
4. WebUI Ollama section + model dropdown.
5. `Main.cs` `[Ollama]` config group.
6. Full build + full suite (`run_tests.sh` already flags orphan tests via `audit_coverage`).

### What's intentionally out of scope
- In-game **spawn/stop** of Ollama (can't exec under Proton).
- Raising `num_ctx` — that's the user's modelfile (config hint + the fit-math tells them the window).
