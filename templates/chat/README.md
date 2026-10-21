# templates/chat — game-tailored Jinja chat templates

Serve-side fix-ups for models whose **embedded** chat template mishandles the payloads
KK-LLM-NPC sends. Drop-in files for llama.cpp / LM Studio; nothing in the mod changes —
these only reshape what the model sees.

## When you need one

The mod's conversation shapes that stock templates sometimes choke on:

- **Vision probes** send a user turn whose content is a parts array; string-concat
  templates (MiMo-7B-RL stock) crash on those with a TypeError.
- **Replayed tool calls**: assistant history carries `tool_calls` with a null id and
  `arguments` as a JSON string, an object, or double-encoded — templates that blindly
  re-serialize arguments double-quote the string form.
- **Native tool vocabulary**: with `[Console] NativeTools` on, console payloads carry a
  `tools` array; templates without a tools section can't teach the model the call
  syntax, and act mode sends no tools at all (the section then stays off).
- **Thinking control**: `Thinking=off` ships `chat_template_kwargs {"enable_thinking":
  false}`; only Qwen3-style templates act on that kwarg.

## The files

| File | For | Notes |
|------|-----|-------|
| `chat-kk-chatml.jinja` | ChatML families with the pipe-delimited specials (Qwen, GLM, ZDTaichu, MiMo-V2-Flash, …) | parts arrays collapse to text + an `[image]` marker; `role:"tool"` flattened to a user turn; `enable_thinking:false` emits the empty reasoning block; tools section only when tools are sent (Qwen2-style prose + the tool-call tag pair example); tool-call arguments accepted as JSON string or object. |
| `chat-mimo-7b.jinja` | MiMo-7B-RL specifically | Byte-faithful to the stock tokenizer_config template except the game's tool-call-arguments tolerance (stock re-quotes string arguments) and a header comment. No think machinery — the stock template has none. |

Both are written to the llama.cpp **minja** subset (no macros, no slicing, no custom
filters) and were render-tested through game-shaped payloads: string / dict /
double-encoded arguments, parts arrays, `role:"tool"`, tools present and absent,
thinking on and off. The special-token and tool-call tag literals appear in the files
verbatim by design — that is what the server must see.

## llama.cpp

```
llama-server -m <model>.gguf --jinja --chat-template-file templates/chat/chat-kk-chatml.jinja
```

(`--chat-template-kwargs '{"enable_thinking": false}'` sets the thinking switch
server-wide; the mod's per-request `chat_template_kwargs` from `Thinking=off` also
reaches the template, and absent kwarg means thinking stays on.)

## LM Studio

My Models tab → gear on the model → the **🧪 Advanced Configuration** sidebar →
**Prompt Template** → "Jinja Template" → paste the file contents (per model; see LM
Studio docs, "Prompt Template" page; right-click the sidebar → "Always Show Prompt
Template" to pin it). LM Studio does not document whether per-request kwargs reach the
template — for a hard `Thinking=off` prefer llama.cpp; LM Studio's own reasoning toggle
plus the mod's client-side think-strip cover the rest.

## Do not override fine-tuned bases

Training serializes episodes with the base model's OWN chat template at the token-id
level. If you run a KK-LLM-NPC fine-tune (e.g. the Qwen-track LoRA), keep the model's
embedded template — a serve-time override that disagrees with the training-time shape
puts the model in the wrong register.