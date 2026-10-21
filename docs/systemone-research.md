# SystemOne Research — decision-model API for KK-LLM-NPC

**Date:** 2026-10-21
**Source:** ollama/ollama repo — `docs/capabilities/decision.mdx`, `api/types.go`, `docs/openapi.yaml`; Ollama model library (`ollama.com/search?c=decision`).
**Status:** assessment + recommendations for the current `DecisionModel.cs` integration.

---

## TL;DR

We are using the **correct, canonical API**. `/v1/systemone` is Ollama's purpose-built
decision-model endpoint (v0.35.0+), not a hack around `/v1/chat/completions`. The current
noul-question schema is spec-compliant. One refinement (ask task/blocker as single
`choice` questions) would give more principled confidence and could be faster, but the current
design is not wrong.

---

## 1. Is /v1/systemone the right technology?

**Yes.** It is the authoritative, Ollama-native decision-model API. Requirements:

- Ollama **v0.35.0+** (decision models v0.35.1+ for image judging with Clef).
- A **decision model** in the request: **Nimble / Nimble-v3 / Tev1(4B,0.8B) / Clef / Clef-Flash**.
- These are the same models our POC benchmarked (clef-flash, tev1 family).
- Local requests need no API key; cloud models (`:cloud`) use ollama.com + a key.

Ollama deliberately keeps this API separate from chat. The `ollama/systemone()` Python method
and `Client.SystemOne()` Go method both POST to `/v1/systemone` — it is the first-class endpoint
Ollama built for exactly this state→classification use case. Our choice of endpoint is correct.

### Decision models available (Ollama library)
| model | size | vision | notes |
|---|---|---|---|
| **Nimble** | 9B | no | "fast typed classification" — canonical choice model |
| **Nimble-v3** | 9B | no | fast classification/routing/scoring |
| **Clef** | 27B | yes | cloud model, 64K ctx |
| **Clef Flash** | 9B | yes | multimodal, v0.35.1+ |
| **Tev1** | 4B / 0.8B | no | what we run; 4B more accurate than 0.8B |
| **Strands-decider** | 2B | no | fast classification/routing/scoring |
| **Kelv** | ? | no | scores answers to structured questions in a *single forward pass* |
| **Laya** | 421M | no | smallest/fastest; ModernBERT-based |
| **pplx-decider** | 27B | yes | Perplexity, vision |

Our default (`tev1:4b`) is the right balance of accuracy and latency. `Kelv` (single forward
pass) is worth watching for a future "make it even faster" pass.

---

## 2. Request/response schema (from OpenAPI + decision.mdx)

Request body is `{model, state, questions}` (all three required):

- **`state`** — base context for the decision. Accepts **plain text OR a JSON value** (object or
  array). Vision models additionally take `images` (base64 PNG/JPEG/WebP, shared by all questions,
  up to 4 images, 16 MP each, 8 MB total). State references fields via backticks.
- **`questions`** — named questions about the shared state; **answers are not passed to later
  questions** (independent), kept in request order.

Question object — three types, all require `type` and `instructions`:

| type | criteria field | returns |
|---|---|---|
| **`noul`** (yes/no) | object with `false` + `true` descriptions | probability of `true`, 0..1 (`{"type":"noul","noul":0.96}`) |
| **`choice`** | object of **2–26** named options → descriptions | most likely option + each option's probability + `confidence` |
| **`score`** | array of **2–10** ordered descriptions (lowest→highest) | probability-weighted average of zero-based levels (e.g. `0.8308` on a 0–2 scale) |

Example `choice` response:
```json
"label": {"type":"choice","choice":"bug",
  "probabilities":{"billing":0.0125,"bug":0.9781,"account":0.0093},"confidence":0.8906}
```

**Important nuance:** for `choice` and `score`, `confidence` is a *distribution-based* measure —
how strongly the model favors one answer over all others — not just the score of a single flag.
For `noul`, the returned value is `P(true)` on its own.

Other constraints: input is limited to the model's 65,536-token context window; cloud models cap
at 64 questions, each name up to 100 ASCII letters/digits/`_`/`.`/`-`.

---

## 3. Is our current usage correct?

**Yes — spec-compliant.** `DecisionModel.cs`:

- Sends `{model, state, questions, temperature:0.1}` to `/v1/systemone`. ✓
  - `temperature:0.1` is the correct setting for deterministic classification.
  - `state` is a stringified-JSON slice (`Json.Write(slice)`); Ollama accepts string state, so
    this is valid even though the canonical form is a JSON value.
- Every question is a **`noul`** with `criteria: {false, true}` — exactly the documented shape.
- Each question answers "does this condition hold?" — matches the docs' guidance to describe the
  condition, not to guess numbers/thresholds.
- We read `answers[key].noul` (the documented response field) — `DecisionParse.Noul()` reads it
  correctly, with float/long/int/string coercion.

One spec note worth fixing: our `noulQuestion` currently builds criteria as a dict, which is
correct, but the schema lists `noul` criteria as **required** in the openapi schema too. Our
current criteria has both `false` and `true` keys, so it satisfies the requirement.

---

## 4. Refinement worth adopting — one `choice` question per axis

We currently ask **6 task `noul` questions + 4 blocker `noul` questions**, then take argmax in
code. The more canonical design is:

- **One `choice` question** for the task with all 6 task labels as options.
- **One `choice` question** for the blocker with all blocker labels as options.

**Why this is better:**
1. **Principled confidence** — the returned `confidence` measures how strongly the model favors the
   winning option over *all* others, instead of our using the raw `noul` score of one flag (which
   is `P(true)` in isolation, not `P(task is correct among all candidates)`).
2. **Single answer object** — the model returns the full probability distribution once; no need to
   argmax across independent per-flag scores, which can each independently cross the 0.5 bar.
3. **Potentially faster** — fewer questions, single answer object.

**Caveat (don't change blindly):** `choice` returns confidence *only over the options you list*.
If a situation doesn't match any of our 6 tasks, a choice question will still force one of them.
A possible guard is to add an explicit `"none"/"unknown"` option so the model can decline.

**Recommendation:** keep the current `noul` schema running for now (it benchmarks well), but make
`choice` the recommended schema and switch the default once we add the `unknown` guard. It is a
drop-in improvement to `SystemoneQuestions()` + `SystemoneAnswers()` (both read `answers[key].choice`
and `.probabilities`).

---

## 5. Recommendations summary

1. **Keep `/v1/systemone` as the default endpoint.** It is the correct API.
2. **Adopt `choice` questions** for task + blocker (with an `unknown` guard) when upgrading the
   schema — strictly better confidence, likely faster.
3. **Consider `Kelv`** (single forward pass) or `Nimble` for a speed bump if latency becomes a
   concern; `tev1:4b` remains the right default otherwise.
4. **State can be a JSON value** — the current stringified-JSON slice is fine, no change needed.
5. **`temperature:0.1` is correct** for classification.
