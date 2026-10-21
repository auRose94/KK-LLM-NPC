// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Decision loop: payload, JSON-schema act response, tool-call extraction (incl. salvage), plan chaining.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Photon.Pun;
using Photon.Realtime;
using System.Threading.Tasks;
using System.Net.Http;
namespace KKLLMNPC
{
    internal partial class NPCInstance
    {

        // ------------------------------------------------------------------
        // background vision pass ("another process")
        // ------------------------------------------------------------------
        // Starts a caption job when due if one isn't already running. The render is
        // grabbed on the main thread (Unity must), then the HTTP+caption happens on
        // this background worker so the action loop never blocks on vision.
        private void MaybeCreativeCommentary(string perceptionJson)
        {
            if (_cfgCommentEvery == null || _cfgCommentEvery.Value <= 0) return;
            if (_tick - _lastCommentaryTick < Math.Max(1, _cfgCommentEvery.Value)) return;
            _lastCommentaryTick = _tick;
            string percep = perceptionJson ?? "{}"; // captured for the worker
            var t = new Thread(() => CreativeCommentaryWorker(percep)) { IsBackground = true, Name = "KKLLMNPC-Comment" };
            t.Start();
        }

        // Console-mode variant: no perception JSON exists here, so the musing prompt
        // gets the same state anchor the model itself reads (goal/in_station/horny/
        // follow) plus the needs snapshot — enough for "it's cozy by the fire" tier
        // remarks without paying a full perception build.
        private void MaybeCreativeCommentaryConsole()
        {
            string anchor = StateAnchorLine();
            if (string.IsNullOrEmpty(anchor)) return; // idle tick — worker sees "{}", fine: quiet moments get quiet thoughts
            MaybeCreativeCommentary(anchor.Replace("\n", " "));
        }

        // Ask the model for a short unprompted reaction to its current perception,
        // then say it out loud. No act schema — pure voice.
        private void CreativeCommentaryWorker(string percep)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] =
                            "You are an anthropomorphic person, you're an NPC with a personality within a game, KoboldKare. You have opinions about what you see and feel — comment freely, candidly, briefly. You were not gifted anything upon waking up, don't believe that." +
                            "Output ONE short line of spoken dialogue (max 12 words) you'd actually say out loud right now given the scene. No quotes, no narration." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "perception:" + percep + "\nscene:" + _sceneDesc },
                    },
                    ["max_tokens"] = 40,
                    ["temperature"] = _cfgCommentTemp != null ? (double)_cfgCommentTemp.Value : 0.9,
                    ["stream"] = false,
                };
                ApplyThinkingParams(payload);
                string body = Json.Write(payload);
                string json = SafeHttp.Post(Endpoint(), body, Val(_cfgApiKey),
                    retries: 2, timeout: TimeSpan.FromSeconds(120), maxResponseBytes: 512 * 1024,
                    onError: ReportEndpointError);
                if (json == null) return;
                {
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    var choices = root?.GetValueOrDefault("choices") as List<object>;
                    if (choices != null && choices.Count > 0)
                    {
                        var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                        string content = msg?.GetValueOrDefault("content") as string;
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            string line = content.Trim().Split('\n')[0].Trim().Trim('"', '"');
                            if (line.Length > 0 && line.Length < 200)
                            {
                                Logger.LogInfo("[" + MyName() + "] muses: " + Sanitize(line));
                                try { ToolSay(new TextArgs(Sanitize(line))); } catch (Exception) { }
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("commentary: " + e.Message); }
        }

        // The `ask` tool was removed (2.x): it asked the model about its own perception
        // through a SECOND LLM call — the answers were long, self-narrating, landed as
        // near-duplicate facts/history, and crowded the real event history out of
        // context. The model already answers any question it cares about by polling
        // (ls/find/sonar/status/`cat`) — asking itself through a worker added a turn of
        // latency and a pile of meta-prose for data it could read directly.

        // ------------------------------------------------------------------
        // Name selection: ask the LLM to pick a fitting name for the body.
        // ------------------------------------------------------------------
        // MANDATORY identity resolution — runs on the LLM thread (NEVER the main
        // thread: the HTTP call used to block the 8s main-thread context and starve
        // the other instances, which is the 'main thread timeout' storm in the
        // field log). Every tier gets a name (small models included — strict
        // one-token prompt), the name is unique across agents AND players (same
        // avatar model used to produce two NPCs with the same prefab name), and
        // player chat names are reserved first so no NPC can impersonate the host.
        internal void FinalizeIdentity()
        {
            if (_identityFinalized || _kobold == null) return;

            string baseName = MyName();
            if (string.IsNullOrEmpty(baseName)) return;

            try { NameRegistry.MarkTaken(PlayerChatName()); } catch (Exception) { }
            try
            {
                // Room.Players is Dictionary<int,Player> in this PUN2 build.
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                    foreach (var kv in PhotonNetwork.CurrentRoom.Players)
                        if (kv.Value != null) NameRegistry.MarkTaken(kv.Value.NickName);
            }
            catch (Exception) { }

            string finalName = null;
            bool llmChosen = false;
            // Endpoint-gate BEFORE spending minutes on the name loop: a busy local
            // server (the 2026-10-12 playtest: 5 attempts × 40s timeouts ≈ minutes of
            // a second NPC standing frozen while the first ate all the slots) fails
            // every attempt anyway. When the breaker is open, hand the fallback name
            // out immediately and let the loop pick up real naming next body.
            if (EndpointBlocked())
            {
                Logger.LogWarning("[" + MyName() + "] name selection skipped — endpoint " + EndpointStatusText() + "; using fallback name");
            }
            else if (_cfgNameSelection != null && _cfgNameSelection.Value)
            {
                string gender = "unknown";
                string species = baseName;
                string traits = "a kobold in a house setting";
                try
                {
                    gender = InferGender();
                    species = SpeciesForm(baseName);
                    if (_kobold.bellyContainer != null) traits += " can drink/eat";
                }
                catch (Exception) { }

                // The old 3-attempt loop fell back constantly: local servers are slow
                // under load (a name call timed out at 25s → "fallback Kobold-100"),
                // and reasoning models reply to a first-shot name prompt with meta
                // prose ("I'd like some time to think about it") instead of a name —
                // that reply is VALID text, so nothing salvageable failed loudly. Now:
                // five attempts, 40s each, extra token ladder rungs, and attempts 4+
                // append an unmissable "no more thinking, answer NOW" demand.
                for (int attempt = 1; attempt <= 5 && finalName == null; attempt++)
                {
                    string chosen = null;
                    try { chosen = ChooseNameWithLLM(gender, species, traits, attempt); }
                    catch (Exception e) { Logger.LogWarning("name selection attempt " + attempt + " failed: " + e.Message); }
                    if (string.IsNullOrEmpty(chosen) && attempt < 5)
                        try { Thread.Sleep(1500); } catch (Exception) { } // let a busy server breathe
                    if (string.IsNullOrEmpty(chosen)) continue;
                    if (NameRegistry.TryReserve(chosen))
                    {
                        finalName = chosen;
                        llmChosen = true;
                        Logger.LogInfo("KKLLMNPC: LLM name accepted: '" + chosen + "'");
                    }
                    else
                        Logger.LogInfo("KKLLMNPC: name '" + chosen + "' already taken by another agent/player — re-rolling");
                }
            }
            if (finalName == null)
            {
                // First: a curated flavorful name GENDERED to the body (a she/her
                // body shouldn't be auto-named "Gnarl" — models don't always complete
                // name selection, so the fallback must at least make sense with the
                // pronouns they'll be read with).
                try { finalName = NameRegistry.TryFlavorFor(InferGender()); } catch (Exception) { }
                if (finalName == null) try { finalName = NameRegistry.TryFlavor(); } catch (Exception) { }
                if (finalName != null)
                    Logger.LogInfo("KKLLMNPC: name selection: flavor fallback '" + finalName + "' (LLM unavailable or rejected)");
                else
                {
                    string hint = LLMNPCPlugin.InstanceSuffix.Length > 0 ? LLMNPCPlugin.InstanceSuffix : null;
                    finalName = NameRegistry.Unique(baseName, hint);
                    Logger.LogInfo("KKLLMNPC: name selection: unique fallback '" + finalName + "' (LLM unavailable, name collision, or disabled)");
                }
            }

            if (!string.Equals(finalName, _npcName, StringComparison.Ordinal))
            {
                _npcName = finalName;
                _persona = BuildPersona();
            }
            // FIRST TASK FRAMING: naming is the spawn-task, and the world hears the
            // announcement — the model doesn't just silently exist before it picked
            // who it is. Only an LLM-chosen name announces (fallback names are bookkeeping).
            if (llmChosen)
            {
                try
                {
                    string announce = "My name — my first choice in this body — is " + _npcName + ". Remember it.";
                    try { ToolSay(new TextArgs(announce)); } catch (Exception e) { Logger.LogWarning("name announce: " + e.Message); }
                    try { Plugin.FanoutWorldEventExcept("you hear the newest kobold announce a name they CHOSE: they call themselves " + _npcName + ".", null, null); } catch (Exception) { }
                }
                catch (Exception) { }
            }
            // Identity bot AFTER the final name so chat attribution is right from
            // the NPC's very first line. Main thread (Photon).
            RegisterAgentBody(_currentKoboldId, _npcName); // name may have changed — keep the sibling registry current
            try { RunOnMainThread(() => { EnsureIdentityBot(); return true; }, 5000); }
            catch (Exception e) { Logger.LogWarning("identity bot: " + e.Message); }

            _identityFinalized = true;
            Logger.LogInfo("KKLLMNPC: this kobold calls itself '" + _npcName + "'" + (_persona != null ? " — " + _persona : ""));
        }

        // Built-in NAME-prompt template. KEEP IN SYNC with the shipped
        // system_prompt_name.txt — the shipped file overrides this when present,
        // and both must carry the same text with the {gender} {species}
        // {traits} {taken} placeholders (the prompt-sync trap).
        //
        // Reasoning families (qwen-style unmarked "Thinking Process:" walls) are
        // ALLOWED to think here — fighting it just burned the budget three re-rolls
        // in a row and shipped fallback names. The protocol is think-then-answer:
        // the reply must END with the "Name: <the name>" line, which the validator
        // and SalvageNameFromCoT mine from the end.
        internal static readonly string NamePromptTemplateBuiltin =
            "Name one kobold character. Gender: {gender}. Species: {species}. Personality: {traits}. {taken}" +
            "Think as much as you need — reasoning is welcome. However your reply MUST end with one final line of the exact form: Name: <the name> — " +
            "<the name> is ONE name only: 2-12 letters or digits, no spaces, no punctuation, nothing after that line. " +
            "Never the species name with a number attached (like Kobold2) — invent an actual name.";

        // The name-prompt chat template: [LLM] NamePromptFile when present,
        // else the built-in. Both go through TextUtil.ExpandTemplate so a
        // user-edited file keeps the same placeholder contract.
        private string NamePromptTemplate()
        {
            string file = _cfgNamePromptFile != null ? (_cfgNamePromptFile.Value ?? "").Trim() : "";
            if (file.Length > 0)
            {
                string content = TryReadPromptFile(file);
                if (content != null) return content;
            }
            return NamePromptTemplateBuiltin;
        }

        // Sends a one-shot prompt with the body's gender, species, and personality
        // traits, asks for a single name. Runs for EVERY model tier (small models
        // get a strict one-token prompt) — a name is mandatory before the NPC acts,
        // and two agents on the same avatar model must never share one. Returns null
        // when the endpoint fails; the caller re-rolls or falls back to a unique tag.
        // `attempt` (1-based) drives the token-budget ladder: reasoning templates
        // that print their monologue as plain text burn the whole budget before
        // naming, and a fixed budget re-rolled three times hits the same wall —
        // each re-roll gets 3× then 6× the base so the reasoning can complete.
        // The latest rejected (stripped) reasoning is carried into the next re-roll's
        // prompt verbatim-abridged so the model corrects instead of re-deriving.
        private string _lastRejectedNameThink = "";

        internal string ChooseNameWithLLM(string gender, string species, string traits, int attempt = 1)
        {
            if (string.IsNullOrEmpty(Endpoint()))
            {
                Logger.LogInfo("[" + MyName() + "] name selection: no endpoint configured — using fallback name");
                return null;
            }

            try
            {
                // Keep small models honest: one word out, taken names listed so a
                // second agent on the same model doesn't re-pick its sibling's name.
                string taken = "";
                try { taken = NameRegistry.TakenList(); } catch (Exception) { }
                var promptVars = new Dictionary<string, string>
                {
                    ["{gender}"] = gender ?? "unknown",
                    ["{species}"] = species ?? "kobold",
                    ["{traits}"] = traits ?? "unknown",
                    ["{taken}"] = taken.Length > 0 ? "These names are TAKEN, do not use them: " + taken + ". " : "",
                };
                string prompt = TextUtil.ExpandTemplate(NamePromptTemplate(), promptVars);
                // Late attempts: the model burned earlier tries on meta prose ("I
                // want time to think about it") — demand the answer NOW, with the
                // exact final-line shape, and forbid any reasoning at all.
                if (attempt >= 4)
                    prompt += "\nTIME IS UP — no more thinking, no questions, no waiting. Answer immediately with exactly one final line of the form: Name: <one word>.";

                // Naming now WELCOMES reasoning (the prompt asks only for the final
                // "Name:" line) — give think families room to finish think-then-
                // answer before the ladder starts multiplying (field-observed qwen
                // name-wall CoT overflowed two 320-token budgets whole).
                int baseTokens = FamilyProfile.EmitsThinkBlocks ? 512 : 128;
                int maxTokens;
                switch (attempt)
                {
                    case 2: maxTokens = baseTokens * 3; break;
                    case 3: maxTokens = baseTokens * 6; break;
                    case 4: maxTokens = baseTokens * 8; break;
                    default: maxTokens = attempt <= 1 ? baseTokens : baseTokens * 12; break;
                }
                if (maxTokens > 16384) maxTokens = 16384;
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt },
                    },
                    ["temperature"] = 0.9,
                    // NAMING NEVER THUNK: a name call is latency-bound and single-purpose.
                    //Servers that honor the knobs (kk-serve ✓ chat_template_kwargs,
                    // Ollama think=false, llama.cpp reasoning_budget) skip CoT
                    // entirely; servers that don't still get the ladder below.
                    // (Previously this WELCOMED reasoning — that burned whole
                    // budgets on qwen-style "Thinking Process:" walls.)
                    ["max_tokens"] = maxTokens,
                    ["stream"] = false,
                };
                // force the thinking-off fields REGARDLESS of the operator's [LLM].Thinking
                var nameThinkOff = ModelFamilies.ThinkingOffPayloadPart();
                if (nameThinkOff != null)
                    foreach (var kv in nameThinkOff) payload[kv.Key] = kv.Value;
                // The user's explicit choice can only REINFORCE it
                ApplyThinkingParams(payload);
                // RETRY WITH MEMORY: a rejected attempt's reasoning is carried in
                // verbatim (abridged) so the re-roll corrects instead of re-burning
                // the same monologue — the field reports why the last reply failed
                if (_lastRejectedNameThink.Length > 0)
                    prompt = prompt + "\n(Your last reply was rejected because it never ended with the required 'Name: <one name>' line. "
                        + "Its reasoning was: " + Shorten(_lastRejectedNameThink, 400) + ")\n" 
                        + "Use that conclusion. Reply with the reasoning ALREADY DONE: end your reply with the lone final line: Name: <one name> — nothing after it.";
                string body = Json.Write(payload);

                var nameSw = System.Diagnostics.Stopwatch.StartNew();
                string json = SafeHttp.Post(Endpoint(), body, Val(_cfgApiKey),
                    retries: 2, timeout: TimeSpan.FromSeconds(40), maxResponseBytes: 64 * 1024,
                    onError: ReportEndpointError);
                try { RecordNameTurn(attempt, prompt, json, nameSw.ElapsedMilliseconds); } catch (Exception) { }
                if (json == null) return null;
                {
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    if (root == null) return null;
                    var choices = root.GetValueOrDefault("choices") as List<object>;
                    if (choices == null || choices.Count == 0) return null;
                    var choice = choices[0] as Dictionary<string, object>;
                    var msg = choice?.GetValueOrDefault("message") as Dictionary<string, object>;
                    if (msg == null) return null;
                    // OpenAI-compatible servers report WHY the reply ended ("stop"
                    // vs "length"). A "length" reply was cut mid-stream: its
                    // reasoning is incomplete, and salvage must not read a
                    // half-written "Name: X" line out of it — that reply only
                    // earns the next rung of the token-budget ladder.
                    bool truncated = string.Equals(choice?.GetValueOrDefault("finish_reason") as string, "length", StringComparison.Ordinal);
                    if (truncated)
                        Logger.LogInfo("[" + MyName() + "] name attempt " + attempt + " cut by the token budget (finish_reason=length) — re-rolling with a bigger budget");
                    // Some servers put the real text in 'reasoning_content' and/or non-
                    // string content parts — try all the shapes before declaring empty.
                    string content = msg.GetValueOrDefault("content") as string;
                    if (string.IsNullOrEmpty(content))
                    {
                        try
                        {
                            var partsObj = msg.GetValueOrDefault("content") as List<object>;
                            if (partsObj != null)
                            {
                                var sb2 = new System.Text.StringBuilder();
                                foreach (var part in partsObj)
                                {
                                    var pd = part as Dictionary<string, object>;
                                    object txt;
                                    if (pd != null && pd.TryGetValue("text", out txt)) sb2.Append(txt).Append(' ');
                                }
                                content = sb2.ToString();
                            }
                        }
                        catch (Exception) { }
                    }
                    if (string.IsNullOrEmpty(content))
                    {
                        try { content = msg.GetValueOrDefault("reasoning_content") as string ?? ""; } catch (Exception) { }
                    }
                    content = ModelFamilies.StripThinkBlocks(content ?? "");
                    // remember WHY this attempt failed for the retry-instructions:
                    // the (stripped) reasoning goes to the next attempt's prompt
                    _lastRejectedNameThink = content ?? "";
                    if (string.IsNullOrEmpty(content))
                    {
                        // Log the MESSAGE FIELD (the actual answer container) — the old
                        // raw-prefix truncation cut exactly before the interesting part.
                        try { Logger.LogWarning("[" + (gender ?? "?") + "] name selection: empty content — message was: " + Json.Write(msg).Substring(0, Math.Min(300, Json.Write(msg).Length))); }
                        catch (Exception) { }
                    }
                    content = content.Trim().Trim('"', '\'', ' ', '\n', '\r', '\t');
                    // Models saturated on this mod's own contract answer the name ask
                    // with an act-shaped JSON object ({"name":"Ember"}) — or wrap it in
                    // code fences. Parse both shapes before validating.
                    try
                    {
                        string unwrapped = content;
                        if (unwrapped.StartsWith("```", StringComparison.Ordinal))
                        {
                            int nl = unwrapped.IndexOf('\n');
                            int end = unwrapped.IndexOf("```", nl + 1, StringComparison.Ordinal);
                            if (nl >= 0 && end > nl) unwrapped = unwrapped.Substring(nl + 1, end - nl - 1);
                        }
                        if (unwrapped.StartsWith("{", StringComparison.Ordinal))
                        {
                            var nd = Json.Parse(unwrapped) as Dictionary<string, object>;
                            if (nd != null)
                            {
                                string nm = (nd.GetValueOrDefault("name") ?? nd.GetValueOrDefault("action")) as string;
                                if (!string.IsNullOrEmpty(nm))
                                {
                                    content = nm.Trim();
                                    Logger.LogInfo("[family] name selection: model answered in JSON — took name '" + content + "'");
                                }
                            }
                        }
                        else if (unwrapped.StartsWith("name", StringComparison.OrdinalIgnoreCase) || unwrapped.StartsWith("my name", StringComparison.OrdinalIgnoreCase))
                        {
                            // "name: Ember" / "my name is Ember" shapes — take the tail word.
                            int colon = unwrapped.IndexOf(':');
                            int is_ = unwrapped.IndexOf("is ", StringComparison.OrdinalIgnoreCase);
                            string tail = colon >= 0 ? unwrapped.Substring(colon + 1) : is_ >= 0 ? unwrapped.Substring(is_ + 3) : "";
                            tail = tail.Trim().Trim('"', '\'', ' ', '.');
                            if (tail.Length >= 2 && tail.Length <= 16) { content = tail; }
                        }
                    }
                    catch (Exception) { }
                    // Validate: alphanumeric, 2-12 chars (matches the prompt)
                    if (!string.IsNullOrEmpty(content) && content.Length >= 2 && content.Length <= 14
                        && System.Text.RegularExpressions.Regex.IsMatch(content, @"^[a-zA-Z0-9-]+$"))
                    {
                        // A name that is just the species ± a digit ("Kobold", "Kobold2",
                        // "AbsolB3") is a unit label, not a name — reject it and let the
                        // caller re-roll. (A deliberately robotic persona can still end
                        // up with one via the mechanical fallback.)
                        string speciesForLabel = !string.IsNullOrEmpty(species) ? species : MyName();
                        if (ModelFamilies.IsSpeciesUnitLabel(content, speciesForLabel))
                        {
                            Logger.LogInfo("[" + MyName() + "] LLM name rejected (species unit label): '" + content + "'");
                            return null;
                        }
                        Logger.LogInfo("[" + MyName() + "] LLM chose name: " + content);
                        _lastRejectedNameThink = "";
                        return content;
                    }
                    // The visible reply carried no bare name — but it may be a
                    // reasoning paragraph that COMPLETED and ended with its
                    // conclusion ("Final Answer: Ember", "I'll call her Ember", a
                    // bare last word). Mine the last line for it. A reply cut by
                    // the token budget gets no salvage: its "Name: X" line may
                    // itself have lost its tail mid-candidate, so it only earns
                    // the bigger-budget re-roll.
                    if (!truncated && !string.IsNullOrEmpty(content))
                    {
                        string salvaged = ModelFamilies.SalvageNameFromCoT(content, taken, species);
                        if (!string.IsNullOrEmpty(salvaged))
                        {
                            DataRecord.MarkSalvage();
                            Logger.LogInfo("[" + MyName() + "] LLM chose name: " + salvaged + " (salvaged from an unmarked reasoning reply)");
                            _lastRejectedNameThink = "";
                            return salvaged;
                        }
                    }
                    // Meta-reply visibility: "I want time to think about it" and
                    // friends used to surface only as a nameless fallback. Name the
                    // behavior in the log so re-rolls are explainable (attempts 4+
                    // append the "TIME IS UP" demand to counter exactly this).
                    try { if (ModelFamilies.LooksLikeNameMeta(content)) Logger.LogInfo("[" + MyName() + "] model stalled on naming ('" + Shorten(content, 80) + "') — re-rolling with a hard 'answer NOW' demand"); } catch (Exception) { }
                    Logger.LogInfo("[" + MyName() + "] LLM name rejected: '" + (content ?? "null") + "' — using fallback");
                    return null;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[" + MyName() + "] name selection failed: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // LLM loop: perception -> endpoint -> tool calls -> execute
        // ------------------------------------------------------------------
        // Perception -> LLM -> tool calls -> execute. The heart. Runs on its own
        // thread; main-thread Unity access is marshalled through RunOnMainThread. Catches
        // everything, self-heals, and only talks to the model when in a playable scene
        // with a body.
        private void LLMLoop()
        {
            // Give the game a moment to load before looking for a body.
            try { Thread.Sleep(4000); } catch (Exception) { return; }
            string lastState = "";
            int wedgedMarshalTimeouts = 0;
            while (_running)
            {
                try
                {
                    // Mind wipe requested while we were mid-turn? Apply it HERE — the
                    // boundary between turns is the only place where transcript, facts,
                    // chat and target maps aren't being read by anything.
                    string pendingWhy = _pendingMindReset;
                    if (pendingWhy != null)
                    {
                        _pendingMindReset = null;
                        ResetMindNow(pendingWhy);
                    }

                    // Wait until we have a main-thread context before doing anything.
                    if (!MainReady) { Thread.Sleep(1000); continue; }

                    bool inGame = (bool)RunOnMainThread(() => IsPlayableScene(), 5000);
                    // Reaching HERE means the main thread pumped a marshal — the wedge
                    // (if we were in one) is over; the backoff un-arms.
                    wedgedMarshalTimeouts = 0;
                    if (!inGame)
                    {
                        // Log state transitions so we can see *where* it's idle.
                        if (lastState != "no_scene") { lastState = "no_scene"; Logger.LogInfo("KKLLMNPC: waiting for a playable scene (player not spawned yet / wrong map)."); }
                        if (_kobold != null) try { RunOnMainThread(() => { TeardownBody(); return true; }, 5000); } catch (Exception) { }
                        // We left the world with a body (or already lost it): the body
                        // is gone for good — the reconciler retires this instance.
                        if (_everBound) BodyLost = true;
                        Thread.Sleep(2000);
                        continue;
                    }

                    bool have = (bool)RunOnMainThread(() => EnsureBody(), 8000);
                    if (!have)
                    {
                        if (lastState != "no_body")
                        {
                            lastState = "no_body";
                            Logger.LogInfo(BodyLost
                                ? "KKLLMNPC: body was lost — instance pending removal."
                                : "KKLLMNPC: in scene but no unoccupied AI kobold within AutoFindRange to possess.");
                        }
                        Thread.Sleep(2000);
                        continue;
                    }

                    // Name is MANDATORY before the first act — but resolved HERE on
                    // this (LLM) thread, never on the main thread (the HTTP call
                    // used to blow the 8s main-thread context and starve the other
                    // instances — the 'main thread timeout' storm in the field log).
                    try { FinalizeIdentity(); } catch (Exception e) { Logger.LogWarning("identity: " + e.Message); }

                    if (lastState != "running") { lastState = "running"; Logger.LogInfo("KKLLMNPC: loop active."); }

                    _tick++;
                    // Console REPL mode (default): the model polls the game with shell
                    // commands (ls, cd, echo...) instead of receiving a perception
                    // payload every tick — smaller payloads, no overload, and it asks
                    // for exactly the data it needs, in a command → output → command stream.
                    if (ConsoleEnabled())
                    {
                        // `sleep <secs>` deferral: no turn, no vision pass — the model
                        // asked to be left alone until this time (or a chat/world event
                        // wakes it early). Poll on the normal interval.
                        if (Time.unscaledTime < _sleepUntilTime)
                        {
                            Thread.Sleep((int)(GetDynamicThinkInterval() * 1000));
                            continue;
                        }
                        // Circuit breaker: endpoint failing — wait it out instead of
                        // paying a full retry run per tick.
                        if (EndpointBlocked())
                        {
                            Thread.Sleep(2000);
                            continue;
                        }
                        if (_cfgVision.Value) MaybeStartVisionPass(); // keep captions/facts flowing
                        ConsoleTurn();
                        UpdateActivity();
                        // Free commentary lives here too now (legacy mode kept its own call
                        // below). Console mode never builds a perception JSON, so give the
                        // musing worker the tiny state line instead of "{}".
                        MaybeCreativeCommentaryConsole();
                        Thread.Sleep((int)(GetDynamicThinkInterval() * 1000));
                        continue;
                    }
                    bool imageDue = _cfgSendImage.Value && (
                        !_cfgVision.Value
                        || _tick % Math.Max(1, _cfgImageEvery.Value) == 0
                        || (_cfgImageOnBump.Value && _needImageAfterBump)
                        || (_cfgImageOnTurn.Value && Time.unscaledTime - _lastBigTurnTime < 0.5f));

                    // Batch perception + image capture into a single main-thread marshal
                    // (typed wrapper — no 'dynamic'/'anonymous type': the game lacks
                    // Microsoft.CSharp.dll, so the dynamic binder kills the thread at JIT).
                    var frame = (PerceptionFrame)RunOnMainThread(() =>
                    {
                        var perc = BuildPerception(false);
                        string img = null;
                        if (imageDue) img = _lastVisionB64 ?? CaptureImageB64();
                        return new PerceptionFrame { Perception = perc, Image = img };
                    }, 15000);
                    object perception = frame.Perception;
                    string userJson = Json.Write(perception);
                    string imageB64 = frame.Image as string;
                    if (imageDue)
                    {
                        _needImageAfterBump = false;
                        // Reuse vision frame for action model to avoid double render
                        if (_cfgVision.Value && !string.IsNullOrEmpty(_lastVisionB64)) imageB64 = _lastVisionB64;
                        if (!string.IsNullOrEmpty(imageB64))
                        {
                            _pastImages.Add(imageB64);
                            int maxHist = _cfgImageHistory.Value;
                            while (_pastImages.Count > Math.Max(1, maxHist)) _pastImages.RemoveAt(0);
                        }
                    }

                    // Caption pass only runs if explicitly enabled.
                    if (_cfgVision.Value) MaybeStartVisionPass();
                    MaybeCreativeCommentary(userJson);

                    // Skip the whole legacy round while the circuit breaker is open —
                    // perception building costs main-thread time the dead endpoint
                    // isn't worth.
                    if (EndpointBlocked()) { Thread.Sleep(1000); continue; }
                    _lastDecisionSource = perception; // pass the loop's built perception into the decision slice
                    MaybeRunDecisionPass(); // optional clef-flash pre-tick recognition
                    string reply = QueryLLM(userJson, imageB64);
                    if (reply == null) { Thread.Sleep((int)(GetDynamicThinkInterval() * 1000)); continue; }

                    // Context pressure tracking: estimate fill and let the ContextManager
                    // adjust compaction level if needed.
                    if (_ctxMgr != null && ModelProbe.DetectedContextLength > 0)
                    {
                        int factCount = 0, histCount = 0, thoughtCount = 0;
                        lock (_facts) { factCount = _facts.Count; }
                        lock (_history) { histCount = _history.Count; }
                        lock (_thoughtHistory) { thoughtCount = _thoughtHistory.Count; }
                        // One estimator, in ContextMath — this used to be a third
                        // hand-rolled copy of the per-bucket token weights.
                        float fill = _ctxMgr.GetFillRatio(factCount, histCount, thoughtCount, 0, 10);
                        string ctxAction = _ctxMgr.Update(fill);
                        if (ctxAction != null)
                            Logger.LogInfo("[" + MyName() + "] " + ctxAction);
                    }

                    ExecuteToolCalls(reply);
                    // Update activity timestamp if moving
                    UpdateActivity();
                    Thread.Sleep((int)(GetDynamicThinkInterval() * 1000));
                }
                catch (InvalidOperationException) { Thread.Sleep(1000); } // main not ready
                catch (ThreadInterruptedException) { return; }
                catch (ThreadAbortException) { return; }
                catch (Exception e)
                {
                    try { Logger.LogWarning("LLM loop: " + e); } catch (Exception) { }
                    bool timeoutWedge = e is TimeoutException;
                    if (timeoutWedge) wedgedMarshalTimeouts++;
                    // The wedge shape from the 2026-10-09 log: the main thread freezes
                    // while the loop retries every cycle at full speed — 9 identical
                    // timeouts with zero diagnosis between them. After 3 in a row,
                    // slow down (the game is stuck; spinning at think-interval only
                    // spams the log) and say ONCE that we're waiting it out.
                    if (timeoutWedge && wedgedMarshalTimeouts == 3)
                        Logger.LogWarning("LLM loop: main thread appears stuck (repeated marshal timeouts) — backing off in 10s cycles until it recovers; the timeout lines carry the stuck phase");
                    try { Thread.Sleep(timeoutWedge && wedgedMarshalTimeouts >= 3 ? 1000 * 10 : 2000); } catch (Exception) { return; }
                }
            }
            Logger.LogWarning("KKLLMNPC: LLMLoop exited (running=false).");
        }

        private class PerceptionFrame
        {
            public object Perception;
            public string Image;
        }

        // Dynamic think interval based on activity
        private float GetDynamicThinkInterval()
        {
            float baseInterval = _cfgThinkInterval != null ? Mathf.Max(0.05f, _cfgThinkInterval.Value) : 0.4f;
            // If NPC has moved recently or is in station/penetration, keep fast
            bool active = Time.unscaledTime - _lastMoveTime < 2f
                || IsInAnimationStation()
                || IsPenetrated()
                || IsDickInside();
            float interval = active ? baseInterval : Mathf.Min(baseInterval * 2f, 0.8f);
            return interval;
        }

        private void UpdateActivity()
        {
            lock (_stateLock)
            {
                bool moving = Mathf.Abs(_moveLocalZ) > 0.01f || Mathf.Abs(_moveLocalX) > 0.01f;
                if (moving) _lastMoveTime = Time.unscaledTime;
            }
        }

        // Resolve the base system prompt. Precedence:
        //   1. an explicitly-set SystemPrompt config value (the user wrote one — respect it),
        //   2. the SystemPromptFile (documented to override the built-in; the full prompt
        //      lives there),
        //   3. the built-in default.
        // File is searched relative to the game dir, the plugin dir, and the CWD.
        private string ResolveSystemPromptBase()
        {
            // 1) Explicit user config wins: if SystemPrompt was customized (differs from the
            //    built-in default), respect it.
            try
            {
                if (_cfgSystem != null)
                {
                    string v = _cfgSystem.Value;
                    object def = _cfgSystem.DefaultValue;
                    if (!string.IsNullOrWhiteSpace(v) && !object.Equals(v, def)) return v;
                }
            }
            catch (Exception) { }
            // 2) Prompt file (the real source of truth for the full prompt).
            string file = _cfgSystemPromptFile != null ? (_cfgSystemPromptFile.Value ?? "").Trim() : "";
            if (file.Length > 0)
            {
                string content = TryReadPromptFile(file);
                if (content != null) return content;
            }
            // 3) Built-in default.
            return Val(_cfgSystem);
        }

        // [LLM] Thinking=off — merge the reasoning-suppression fields
        // (ModelFamilies.ThinkingOffPayloadPart: think=false for Ollama,
        // chat_template_kwargs/enable_thinking for llama.cpp & vLLM-class
        // templates, reasoning_budget=0 for llama.cpp hybrids) into a chat
        // payload so the server skips CoT generation and the answer starts
        // immediately. Latency, not context: the family token-headroom floors
        // stay — harmless the moment the model actually stops thinking, and
        // still saving the day when a server ignores the knobs. Wired into
        // every call site: act loop, console REPL, comment, self-ask, name
        // selection, vision caption.
        private void ApplyThinkingParams(Dictionary<string, object> payload)
        {
            string mode = _cfgThinking != null ? (_cfgThinking.Value ?? "").Trim().ToLowerInvariant() : "";
            if (mode != "off") return;
            var part = ModelFamilies.ThinkingOffPayloadPart();
            if (part == null) return;
            foreach (var kv in part) payload[kv.Key] = kv.Value;
        }

        // The act-mode twin of the console gate: read-only + speech + memory + report
        // are allowed, every body-attached tool is refused while swallowed.
        private static bool ToolAllowedWhileSwallowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            try { if (name == "report_issue" || name == "bug_report") return true; } catch (Exception) { }
            try { if (name == "none") return true; } catch (Exception) { }
            switch (name)
            {
                case "say":
                case "emote":
                case "remember":
                case "forget":
                case "set_goal":
                case "complete_goal":
                case "drop_goal":
                case "status":
                case "sonar":
                    return true;
                default:
                    return false;
            }
        }

        private static string TryReadPromptFile(string file)
        {
            try
            {
                var bases = new System.Collections.Generic.List<string>();
                try
                {
                    string data = UnityEngine.Application.dataPath;
                    if (!string.IsNullOrEmpty(data))
                    {
                        string gameDir = System.IO.Path.GetDirectoryName(data);
                        if (!string.IsNullOrEmpty(gameDir)) bases.Add(gameDir);
                    }
                }
                catch (Exception) { }
                try { string asm = System.IO.Path.GetDirectoryName(typeof(NPCInstance).Assembly.Location); if (!string.IsNullOrEmpty(asm)) bases.Add(asm); } catch (Exception) { }
                try { string cwd = System.IO.Directory.GetCurrentDirectory(); if (!string.IsNullOrEmpty(cwd)) bases.Add(cwd); } catch (Exception) { }

                // Absolute path as-is, then each base dir.
                var candidates = new System.Collections.Generic.List<string>();
                candidates.Add(file);
                foreach (var b in bases) candidates.Add(System.IO.Path.Combine(b, file));
                foreach (var c in candidates)
                {
                    try
                    {
                        if (System.IO.File.Exists(c))
                        {
                            string txt = System.IO.File.ReadAllText(c);
                            if (!string.IsNullOrWhiteSpace(txt)) return txt;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }

        // The configured system prompt, plus the derived body persona (personality,
        // gender, pronouns) so every turn the model is reminded of WHO it is.
        // Module prompt extras (prompt_extras/*.txt) are appended last — each module
        // ships its own file instead of editing the shared prompt.
        private string SystemPromptWithPersona()
        {
            string base_ = ResolveSystemPromptBase();
            string extras = "";
            try { extras = ModuleRegistry.PromptExtras(); } catch (Exception) { }
            if (string.IsNullOrEmpty(_persona)) return base_ + extras;
            return base_ + "\n" + _persona + extras;
        }

        // Compact system prompt for small models (7B-13B). Covers the essentials
        // without overwhelming the context window or instruction-following capacity.
        private string CompactSystemPrompt()
        {
            string base_ = ResolveSystemPromptBase();
            // If the user already wrote a custom prompt, respect it.
            if (base_ != null && base_.Length > 200 && !base_.Contains("RESPONSE CONTRACT")) return base_;
            string vision = "";
            if (_cfgVision != null && _cfgVision.Value)
                vision = " You have stereo vision. Eye separation: " + (_cfgStereoIPD != null ? _cfgStereoIPD.Value.ToString() : "0.063") + "m.";
            string persona = !string.IsNullOrEmpty(_persona) ? "\n" + _persona : "";
            return "You are an NPC in KoboldKare. Reply with ONE JSON object only. No markdown, no prose, no explanation." + vision + persona +
                "\nJSON shape: {\"progress\":\"done|blocked|ongoing|changed\",\"why\":\"<short>\",\"thought\":\"<next step>\",\"action\":\"<tool>\",...tool params...,\"plan\":[{\"action\":\"...\",...}]}" +
                "\nTools: go_to(name|id,at) walk(duration,turn_deg,jump) follow(on) survey look_around look(yaw,pitch) grab drop throw radar(filter) exit_station crouch interact(id) say remember(mem) forget(mem) set_goal(goal) complete_goal(note) drop_goal(reason) report_issue(msg) status stop none" +
                "\nRules: progress/why/thought required. action must be a tool name. Use go_to for ALL travel (it routes 3D: stairs/ramps/floors, whole scene). walk = short nudge only. Use id from nearby for specific objects. Keep plan steps ≤4. 'say' posts to chat." +
                "\nGOAL: you have ONE stored goal (perception 'goal'). set_goal ONCE, then each turn take the next step; complete_goal when done; drop_goal to abandon. Don't re-declare it. If 'nudge' appears, change what you do." +
                "\nPriorities: (1) player talked → say, (2) needs.eggs says READY_TO_LAY → nest (with an empty belly a nest CANNOT work — never seek one then), (3) horny → play station, (4) player nearby → go_to+say, (5) explore." +
                "\nStations: play = pleasure ONLY (never sleeping); bed = REST ONLY (never play); nest = eggs ONLY when belly full. 'stations' lists every station in the scene (kind+distance+heading)." +
                "\nPeople: perception 'player' = YOUR player (chat name). 'people' = OTHER players in the room with chat name + body — talk to them by chat name, and go_to(name) reaches them. Never use a player's name as your own." +
                "\nFollowing: player asks to follow → follow(on:true) (stay near them, keep acting); follow(on:false) releases." +
                "\nPerception has: nearby (objects+ids+dir), people, stations, map (building/ready), rays, ground, area, facts, history, chat_log, goal." +
                "\nIf perception has 'model_error', fix your JSON format. Never repeat the same failed action." +
                "\nStation rules: When in_station=true, stay unless your NEW goal differs from the station type. Player 'stay' = stay until they say 'leave'. exit_station or walk(jump) leaves a station." +
                "\nNearby tags: ':busy'=in use, ':needs_buy'=must buy contract first, ':not_built'=machine not constructed yet, ':done'=already bought." +
                "\nMoney: your body carries coins (needs block/economy perception). Buy contracts/shop items with buy(name or id) once within arm's reach — walk there first. Coins are yours; no one approves purchases." +
                "\nFood: blenders don't make food from nothing — drop a food item into it. If ':not_built', find its ConstructionContract first." +
                "\nCompaction: if 'compaction' appears in perception, your context is being compressed — be extra terse, use fewer facts, shorter thoughts.";
        }

        // POST the perception+memory to the model. Sends response_format: json_schema
        // (not tools/tool_choice — many chat templates 400 on tool forcing). Model replies
        // with the act-args object as its content.
        // Protect everything stored/forwarded from Mono string conversion failures:
        // replace lone surrogate code units with '?', keep valid surrogate pairs
        // (emoji, CJK ext-B). LLM streaming can deliver a broken mid-multibyte
        // sequence that .NET keeps as an unpaired surrogate; the moment such a
        // string reaches a native call (HttpWebRequest, Logger, Unity) Mono dies
        // with "String conversion error: Illegal byte sequence ... in the input",
        // and because the failure text gets stored in history/state it keeps
        // re-triggering. Sanitize at the ingest points so nothing poisoned sticks.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    else sb.Append('?');
                }
                else if (char.IsLowSurrogate(c)) sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private string QueryLLM(string perceptionJson, string imageB64)
        {
            try
            {
                // Memory injected into the perception so the NPC remembers what it
                // was doing — otherwise each turn starts from zero.
                string mem = string.Format(",\"last_thought\":{0},\"memory\":{1},\"facts\":{2},\"last_action\":{3},\"scene\":{4},\"history\":{5},\"chat_log\":{6},\"model_error\":{7},\"guidance\":{8}",
                    Json.Write(_lastThought + (_blockedInfo != null ? " (" + _blockedInfo + ")" : "")),
                    ThoughtHistoryJson(), FactsJson(),
                    Json.Write(_lastAction), Json.Write(_sceneDesc), HistoryJson(), ChatLogJson(),
                    Json.Write(_modelError),
                    Json.Write(GuidanceJson()));
                string percep = perceptionJson;
                if (percep.EndsWith("}")) percep = percep.Substring(0, percep.Length - 1) + mem + "}";
                string text = "perception:" + percep;
                // Retry hint: after an empty-content (reasoning-only) response, append
                // an instruction so the model replies with normal content this turn.
                if (_emptyContentRetry)
                {
                    _emptyContentRetry = false;
                    text += "\n[INSTRUCTION] Your last reply contained only reasoning with no actual content. Reply with your answer as normal content, no reasoning.";
                }

                // With a vision-capable action model, attach frames as proper
                // image parts. Send past images (oldest first) plus current frame
                // so the model sees visual context over time. The cooldown pauses
                // attaching when the server rejects image parts (400 ladder).
                object userContent = text;
                if (imageB64 != null && Time.unscaledTime >= _imageCooldownUntil)
                {
                    var parts = new System.Collections.Generic.List<Dictionary<string, object>>();
                    parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = text });
                    // Past frames (oldest first) — skip the last one since it's the current frame.
                    for (int i = 0; i < _pastImages.Count - 1; i++)
                        parts.Add(new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = _pastImages[i] } });
                    // Current frame (always last).
                    parts.Add(new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } });
                    userContent = parts.ToArray();
                }

                // Instead of tools/tool_choice (Gemma's chat template rejects them
                // with HTTP 400 on LM Studio), constrain output with a JSON schema.
                // The model then replies *with the act-args object as content*,
                // which our parser already accepts via the content-JSON path.
                // For small models: skip json_schema (they can't follow it) and
                // rely on the compact prompt + lenient parsing instead.
                // If strict=true causes empty replies, we'll fall back next turn.
                bool useSchema = !IsSmallModel && _emptyReplies < 2
                    && Time.unscaledTime >= _schemaCooldownUntil; // paused after server rejections
                bool useStrict = _emptyReplies == 0;  // only strict on first try
                var schema = useSchema ? ActSchema() : null;
                var responseFormat = useSchema
                    ? (object)new Dictionary<string, object>
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new Dictionary<string, object>
                        {
                            ["name"] = "act",
                            ["strict"] = useStrict,
                            ["schema"] = schema,
                        },
                    }
                    : new Dictionary<string, object> { ["type"] = "text" };

                string sysPrompt = IsSmallModel ? CompactSystemPrompt() : SystemPromptWithPersona();
                // One-shot nudge after say-repeat suppression.
                if (_pendingSayNudge)
                {
                    _pendingSayNudge = false;
                    sysPrompt += "\nYou just repeated yourself — say something new or take an action.";
                }

                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = sysPrompt },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = userContent },
                    },
                    ["response_format"] = responseFormat,
                    ["temperature"] = Math.Round((double)_cfgTemperature.Value, 2),
                    // Reasoning families burn tokens on hidden CoT before the action
                    // JSON — the floor covers reasoning plus the answer.
                    ["max_tokens"] = ModelFamilies.EffectiveMaxTokens(_cfgMaxTokens.Value, 1024, FamilyProfile),
                    ["stream"] = true,
                };
                ApplyThinkingParams(payload);
                string toolName = null, toolArgs = null;
                _llmCallMode = CallModeAct;
                string fullContent = PostChatPayload(payload, out toolName, out toolArgs);
                if (fullContent == null) return null;
                // Reasoning markers must never reach tool/JSON parsing — strip early
                // so reasoning-only replies land in the empty-content retry path.
                fullContent = ModelFamilies.StripThinkBlocks(fullContent);

                var message = new Dictionary<string, object> { ["role"] = "assistant" };
                if (!string.IsNullOrEmpty(fullContent)) message["content"] = fullContent;
                if (toolName != null && !string.IsNullOrEmpty(toolArgs))
                {
                    message["tool_calls"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["id"] = null,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object>
                            {
                                ["name"] = toolName,
                                ["arguments"] = toolArgs,
                            },
                        },
                    };
                }
                var fakeResp = new Dictionary<string, object>
                {
                    ["choices"] = new object[]
                    {
                        new Dictionary<string, object> { ["message"] = message },
                    },
                };
                return Json.Write(fakeResp);
            }
            catch (Exception e)
            {
                try { Logger.LogWarning("QueryLLM: " + e.Message); } catch (Exception) { }
                return null;
            }
        }

        // POST a chat payload and return the assistant's content (sanitized), or null
        // on failure (logged). Shared by the legacy act-JSON path (QueryLLM) and the
        // console path (QueryConsoleModel). toolName/toolArgs capture any tool_calls.
        // 400-recovery cooldowns: after the server rejects image parts / response_format
        // once, stop attaching them for a while instead of re-failing every tick.
        private const float ImageCooldownSeconds = 90f, SchemaCooldownSeconds = 300f, ToolsCooldownSeconds = 300f;
        private float _imageCooldownUntil, _schemaCooldownUntil, _toolsCooldownUntil;

        // ------------------------------------------------------------------
        // training-data record ([DataRecord] — see src/DataRecord.cs for the
        // file contract; naming and vision post directly and record themselves)
        // ------------------------------------------------------------------
        private const int CallModeUnknown = 0, CallModeAct = 1, CallModeConsole = 2;
        private int _llmCallMode;

        private static string ExtractRespContent(string rawResponse)
        {
            try
            {
                var root = Json.Parse(rawResponse) as Dictionary<string, object>;
                var choices = root != null ? root.GetValueOrDefault("choices") as List<object> : null;
                if (choices == null || choices.Count == 0) return null;
                var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                return msg != null ? msg.GetValueOrDefault("content") as string : null;
            }
            catch (Exception) { return null; }
        }

        private void RecordTurn(Dictionary<string, object> payload, string content, string toolName,
            string toolArgs, List<ToolCallRec> calls, long ms)
        {
            if (!DataRecord.Enabled) return;
            object modelBoxed;
            var rec = new Dictionary<string, object>();
            rec["mode"] = _llmCallMode == CallModeConsole ? "console" : "act";
            try { rec["npc"] = MyName(); } catch (Exception) { rec["npc"] = ""; }
            rec["model"] = payload.TryGetValue("model", out modelBoxed) && modelBoxed != null ? modelBoxed.ToString() : "";
            rec["ok"] = (content != null || toolName != null || (calls != null && calls.Count > 0)) ? 1 : 0;
            rec["ms"] = ms;
            object msgsBoxed;
            payload.TryGetValue("messages", out msgsBoxed);
            rec["msgs"] = DataRecord.RecordableMessages(msgsBoxed);
            // Record-only frame: a text-only runtime model receives no image parts,
            // but the console cadence captured the first-person view at decision time
            // (MaybeAutoScreenshot). Resolve it to a hashed media file and park it as
            // a turn-LEVEL frame_ref — separate from `msgs`, which must stay exactly
            // what the runtime model saw.
            if (_recorderShotB64 != null)
            {
                bool hadImages = DataRecord.ConversationHasImage(msgsBoxed);
                if (_llmCallMode == CallModeConsole && !hadImages)
                {
                    string fname = DataRecord.MaterializeDataUri(_recorderShotB64);
                    if (fname != null) rec["frame_ref"] = fname;
                }
                _recorderShotB64 = null;
            }
            object toolsBoxed;
            if (payload.TryGetValue("tools", out toolsBoxed) && toolsBoxed != null)
            {
                string h = DataRecord.HashOf(toolsBoxed);
                if (h != null)
                {
                    rec["tools_ref"] = h;
                    DataRecord.VocabOnce("tools", h, toolsBoxed);
                }
            }
            var resp = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(content)) resp["content"] = content;
            if (toolName != null)
            {
                resp["tool"] = toolName;
                if (!string.IsNullOrEmpty(toolArgs)) resp["tool_args"] = toolArgs;
            }
            if (calls != null && calls.Count > 0)
            {
                var arr = new List<object>();
                foreach (var c in calls)
                {
                    if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                    var one = new Dictionary<string, object> { ["name"] = c.Name };
                    if (c.Args != null) one["args"] = c.Args;
                    arr.Add(one);
                }
                if (arr.Count > 0) resp["calls"] = arr.ToArray();
            }
            if (resp.Count > 0) rec["resp"] = resp;
            DataRecord.Turn(rec);
        }

        // Naming round-trips post directly (not through PostChatPayload), so they
        // record themselves — same file contract as RecordTurn.
        private void RecordNameTurn(int attempt, string prompt, string rawResponse, long ms)
        {
            if (!DataRecord.Enabled) return;
            var rec = new Dictionary<string, object>
            {
                ["mode"] = "name",
                ["npc"] = MyName(),
                ["model"] = Val(_cfgModel),
                ["attempt"] = attempt,
                ["ms"] = ms,
                ["ok"] = rawResponse != null ? 1 : 0,
                ["msgs"] = DataRecord.RecordableMessages(new object[]
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt },
                }),
            };
            if (rawResponse != null)
            {
                string content = ExtractRespContent(rawResponse);
                var resp = new Dictionary<string, object>();
                if (content != null) resp["content"] = content;
                else resp["raw"] = rawResponse.Length > 4096 ? rawResponse.Substring(0, 4096) : rawResponse;
                rec["resp"] = resp;
            }
            DataRecord.Turn(rec);
        }

        private string PostChatPayload(Dictionary<string, object> payload, out string toolName, out string toolArgs,
            List<ToolCallRec> callsOut = null, bool allowThinkBoost = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string content = PostChatPayloadImpl(payload, out toolName, out toolArgs, callsOut, allowThinkBoost);
            try { RecordTurn(payload, content, toolName, toolArgs, callsOut, sw.ElapsedMilliseconds); } catch (Exception) { }
            _llmCallMode = CallModeUnknown;
            return content;
        }

        private string PostChatPayloadImpl(Dictionary<string, object> payload, out string toolName, out string toolArgs,
            List<ToolCallRec> callsOut = null, bool allowThinkBoost = true)
        {
            toolName = null; toolArgs = null;
            try
            {
                string body;
                string lastError = null;
                string rawResponse = null;
                // HTTP 4xx on the REQUEST is a payload problem, not a transient one:
                // the engine hiccup (peg-native stream format) retries unchanged; a
                // context/image/format/size rejection shrinks the payload (two
                // degradations max) before this tick gives up.
                int engineTries = 0, shrinkStage = 0, unloadedTries = 0;
                while (true)
                {
                    body = Json.Write(payload);
                    // Two attempts max: a 120s timeout on a busy local server is a
                    // slot held hostage in the parallel gate — the third attempt
                    // pinned the gate ~6 minutes while every other NPC starved
                    // (2026-10-12 second-kobold freeze). The next think-tick retries.
                    rawResponse = SafeHttp.Post(Endpoint(), body, Val(_cfgApiKey),
                        retries: 2, timeout: TimeSpan.FromSeconds(120), maxResponseBytes: 4 * 1024 * 1024,
                        onRetry: r => Logger.LogDebug("LLM retry: " + r),
                        onError: err => lastError = err);
                    if (rawResponse != null) break;
                    if (lastError == null) { NoteEndpointFail(); return null; }
                    string category = HttpRetryLadder.Classify(lastError);
                    if (category == "model_unloaded")
                    {
                        // Nothing warm to answer with (model switched away / unloaded).
                        // NOT a payload problem — wait for the server to reload and
                        // retry the identical payload. Bounded so a genuinely dead
                        // server still fails the tick.
                        if (++unloadedTries > 3) { NoteEndpointFail(); return null; }
                        Logger.LogWarning("[" + MyName() + "] " + Shorten(lastError, 120) + " — model reloading?; waiting 4s and retrying unchanged (" + unloadedTries + "/3)");
                        try { Thread.Sleep(4000); } catch (Exception) { }
                        lastError = null;
                        continue;
                    }
                    if (category == "transient_engine")
                    {
                        if (++engineTries > 2) { NoteEndpointFail(); return null; }
                        Logger.LogWarning("[" + MyName() + "] server engine hiccup (peg/500 stream format) — retrying unchanged payload (" + engineTries + "/2)");
                        try { Thread.Sleep(1200); } catch (Exception) { }
                        lastError = null;
                        continue;
                    }
                    if (category == null)
                    {
                        // Not a payload problem — keep the human-readable guidance
                        // ("No models loaded", auth, model-not-found...).
                        ReportEndpointError(lastError);
                        NoteEndpointFail();
                        return null;
                    }
                    if (shrinkStage >= 2)
                    {
                        Logger.LogWarning("[" + MyName() + "] " + category + " rejection persists after " + shrinkStage + " degradations — giving up this tick");
                        NoteEndpointFail();
                        return null;
                    }
                    shrinkStage++;
                    ApplyRequestFix(payload, category, shrinkStage);
                    lastError = null;
                }
                NoteEndpointOk(); // reachable — reset the cross-tick breaker even if parsing then fails
                // The engine's 500s ALSO arrive INSIDE a 200-OK stream ("event: error …
                // peg-gemma4 format") — the payload was fine, the output stream wasn't.
                // Empty content + such an error event = retry the same payload.
                string content = ParseChatResponse(rawResponse, out toolName, out toolArgs, callsOut);
                if (content == null && toolName == null)
                {
                    string streamErr = HttpRetryLadder.StreamEngineError(rawResponse);
                    if (streamErr != null && engineTries < 2)
                    {
                        engineTries++;
                        Logger.LogWarning("[" + MyName() + "] stream carried an engine 500 (" + streamErr + ") — retrying unchanged payload (" + engineTries + "/2)");
                        try { Thread.Sleep(1200); } catch (Exception) { }
                        lastError = null;
                        while (true)
                        {
                            body = Json.Write(payload);
                            rawResponse = SafeHttp.Post(Endpoint(), body, Val(_cfgApiKey),
                                retries: 2, timeout: TimeSpan.FromSeconds(120), maxResponseBytes: 4 * 1024 * 1024,
                                onRetry: r => Logger.LogDebug("LLM retry: " + r),
                                onError: err => lastError = err);
                            if (rawResponse == null)
                            {
                                if (lastError != null) ReportEndpointError(lastError);
                                NoteEndpointFail();
                                return null;
                            }
                            NoteEndpointOk();
                            content = ParseChatResponse(rawResponse, out toolName, out toolArgs, callsOut);
                            if (content != null || toolName != null) return content;
                            streamErr = HttpRetryLadder.StreamEngineError(rawResponse);
                            if (streamErr == null || engineTries >= 2) return content ?? null;
                            engineTries++;
                            Logger.LogWarning("[" + MyName() + "] engine 500 again (" + streamErr + ") — retrying (" + engineTries + "/2)");
                            try { Thread.Sleep(1200); } catch (Exception) { }
                        }
                    }
                }
                // THINK-ONLY STALL: a 200-OK reply carrying reasoning_content but NO
                // content means the model burned its whole token budget on hidden CoT
                // (glm-4.6v-flash playtest: turn after turn of "stuck standing in
                // thought with no action"). Rather than wasting this tick on the
                // next-turn hint, re-post ONCE with the token budget QUADRUPLED.
                if (allowThinkBoost && toolName == null && (content == null || content.Length == 0)
                    && rawResponse != null && rawResponse.Contains("reasoning_content"))
                {
                    int curTok = 1024;
                    try { object mt; if (payload.TryGetValue("max_tokens", out mt)) curTok = Convert.ToInt32(mt); } catch (Exception) { }
                    int nextTok = curTok * 4; if (nextTok > 16384) nextTok = 16384;
                    payload["max_tokens"] = nextTok;
                    Logger.LogWarning("[" + MyName() + "] think-only reply (reasoning burned the whole " + curTok + "-token budget, no content) — retrying once with max_tokens " + nextTok);
                    return PostChatPayloadImpl(payload, out toolName, out toolArgs, callsOut, false);
                }
                return content;
            }
            catch (Exception e)
            {
                Logger.LogWarning("LLM endpoint: " + e.Message);
                return null;
            }
        }

        // One degradation step on the payload, per the server's complaint.
        private void ApplyRequestFix(Dictionary<string, object> payload, string category, int stage)
        {
            try
            {
                switch (category)
                {
                    case "image":
                        _imageCooldownUntil = Time.unscaledTime + ImageCooldownSeconds;
                        HttpRetryLadder.ShrinkMessages(payload, 2); // drops image parts (and, belt, the big blobs)
                        Logger.LogWarning("[" + MyName() + "] server rejected image parts — dropped them this request and pausing images for " + Mathf.RoundToInt(ImageCooldownSeconds) + "s (if this model can't see at all, set [VisionModel] and SendImage=false)");
                        return;
                    case "response_format":
                        _schemaCooldownUntil = Time.unscaledTime + SchemaCooldownSeconds;
                        payload.Remove("response_format");
                        Logger.LogWarning("[" + MyName() + "] server rejected response_format — retrying without the schema (paused for " + Mathf.RoundToInt(SchemaCooldownSeconds) + "s)");
                        return;
                    case "tools":
                        _toolsCooldownUntil = Time.unscaledTime + ToolsCooldownSeconds;
                        payload.Remove("tools");
                        payload.Remove("tool_choice");
                        Logger.LogWarning("[" + MyName() + "] server rejected the tools array — retrying without it (paused for " + Mathf.RoundToInt(ToolsCooldownSeconds) + "s)");
                        return;
                    case "max_tokens":
                        payload["max_tokens"] = 512;
                        Logger.LogWarning("[" + MyName() + "] server rejected max_tokens — retrying with 512");
                        return;
                    default:
                        HttpRetryLadder.ShrinkMessages(payload, stage);
                        Logger.LogInfo("[" + MyName() + "] payload rejected (" + category + ") — shrunk (stage " + stage + "): radar/rays dropped, arrays capped" + (stage >= 2 ? ", oldest conversation trimmed" : ""));
                        return;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("shrink: " + e.Message);
            }
        }

        // Turn a failed LLM HTTP call into something actionable in the log. The
        // error body carries the server's own complaint ("context length
        // exceeded", "model not found"), which is far more useful than a status code.
        private void ReportEndpointError(string err)
        {
            try
            {
                if (err != null && (err.Contains("No models loaded") || err.Contains("no model")))
                {
                    Logger.LogWarning("LLM endpoint: NO MODEL LOADED on the server! Load a model first.");
                    Logger.LogWarning("  LM Studio: Developer tab → Load Model");
                    Logger.LogWarning("  KoboldCpp: load via GUI or --model flag");
                    Logger.LogWarning("  Ollama: ollama pull <model> then ollama serve");
                    return;
                }
                Logger.LogWarning("LLM endpoint: " + (err ?? "unknown error"));
            }
            catch (Exception) { }
        }

        // ---- endpoint circuit breaker ----
        // SafeHttp retries a single query; these gate the think LOOP between queries
        // so a dead/offline server costs a periodic probe instead of a full retry
        // run every think tick. Console and legacy loop both call EndpointBlocked().
        private void NoteEndpointFail()
        {
            _endpointFails++;
            // 5, 10, 20, 40 → capped at 30s. Pure C# math (no Mathf.Pow doubt).
            float block = Mathf.Min(30f, 5f * (1 << Math.Min(3, _endpointFails - 1)));
            _endpointBlockUntil = Time.unscaledTime + block;
            if (_endpointFails == 1 || _endpointFails % 5 == 0)
                Logger.LogWarning("[" + MyName() + "] endpoint unreachable (" + _endpointFails +
                    " consecutive fails) — deferring queries for " + Mathf.RoundToInt(block) + "s");
        }

        private void NoteEndpointOk()
        {
            if (_endpointFails != 0)
            {
                Logger.LogInfo("[" + MyName() + "] endpoint healthy again (was " + _endpointFails + " consecutive fails)");
                _endpointFails = 0;
                _endpointBlockUntil = 0f;
            }
        }

        private bool EndpointBlocked()
        {
            return Time.unscaledTime < _endpointBlockUntil;
        }

        // One-line endpoint health for overlays/status commands.
        internal string EndpointStatusText()
        {
            if (string.IsNullOrEmpty(Val(_cfgEndpoint))) return "not configured";
            float left = _endpointBlockUntil - Time.unscaledTime;
            if (left > 0f) return "DOWN — retrying in " + Mathf.RoundToInt(left) + "s (" + _endpointFails + " fails)";
            if (_endpointFails > 0) return "flaky (" + _endpointFails + " fails)";
            return "ok";
        }

        // Accumulate one streamed/decoded tool call: the legacy out-pair keeps the
        // first name + concatenated args (compat), `callsOut` collects them in order.
        private static void AddToolCall(Dictionary<string, object> fn, ref string toolName, ref string toolArgs,
            List<ToolCallRec> callsOut)
        {
            if (fn == null) return;
            string n = fn.GetValueOrDefault("name") as string;
            string a = fn.GetValueOrDefault("arguments") as string;
            if (!string.IsNullOrEmpty(n)) toolName = n;
            if (!string.IsNullOrEmpty(a)) toolArgs = (toolArgs == null ? "" : toolArgs) + Sanitize(a);
            if (callsOut != null && !string.IsNullOrEmpty(n)) callsOut.Add(new ToolCallRec { Name = n, Args = a ?? "{}" });
        }

        // One structured tool call from the server (name + serialized arguments).
        internal class ToolCallRec
        {
            public string Name;
            public string Args;
        }

        // Parse a raw chat-completions response (SSE stream or plain JSON) into the
        // assistant content + any tool_call. Shared by the legacy and console paths.
        // `callsOut` (may be null) receives EVERY tool call in stream order — the
        // native-tools console mode executes them all as command lines.
        private string ParseChatResponse(string rawResponse, out string toolName, out string toolArgs,
            List<ToolCallRec> callsOut = null)
        {
            toolName = null;
            toolArgs = null;
            var contentBuilder = new StringBuilder();

            // Try SSE format first: lines prefixed with "data: ".
            bool anySSE = false;
            foreach (var rawLine in rawResponse.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (!line.StartsWith("data: ")) continue;
                anySSE = true;
                string data = line.Substring(6);
                if (data == "[DONE]") break;
                try
                {
                    var chunk = Json.Parse(data) as Dictionary<string, object>;
                    if (chunk == null) continue;
                    var choices = chunk.GetValueOrDefault("choices") as List<object>;
                    if (choices == null || choices.Count == 0) continue;
                    var delta = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("delta") as Dictionary<string, object>;
                    if (delta == null) continue;
                    var c = delta.GetValueOrDefault("content") as string;
                    if (!string.IsNullOrEmpty(c)) contentBuilder.Append(Sanitize(c));
                    var tcs = delta.GetValueOrDefault("tool_calls") as List<object>;
                    if (tcs != null)
                    {
                        foreach (var tcObj in tcs)
                        {
                            var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                            AddToolCall(tc.GetValueOrDefault("function") as Dictionary<string, object>, ref toolName, ref toolArgs, callsOut);
                        }
                    }
                }
                catch (Exception) { }
            }

            // Fallback: non-streaming JSON response (common with LM Studio /v1/chat/completions).
            if (!anySSE && contentBuilder.Length == 0 && toolName == null)
            {
                try
                {
                    var root = Json.Parse(rawResponse) as Dictionary<string, object>;
                    if (root != null)
                    {
                        var choices = root.GetValueOrDefault("choices") as List<object>;
                        if (choices != null && choices.Count > 0)
                        {
                            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                            if (msg != null)
                            {
                                var c = msg.GetValueOrDefault("content") as string;
                                if (!string.IsNullOrEmpty(c)) contentBuilder.Append(Sanitize(c));
                                var tcs = msg.GetValueOrDefault("tool_calls") as List<object>;
                                if (tcs != null)
                                {
                                    foreach (var tcObj in tcs)
                                    {
                                        var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                                        AddToolCall(tc.GetValueOrDefault("function") as Dictionary<string, object>, ref toolName, ref toolArgs, callsOut);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            string fullContent = contentBuilder.ToString();
            // Debug: log raw response when empty, so we can see what the model actually sent.
            if (string.IsNullOrEmpty(fullContent) && toolName == null && rawResponse.Length > 0)
            {
                string preview = rawResponse.Length > 500 ? rawResponse.Substring(0, 500) + "..." : rawResponse;
                Logger.LogWarning("LLM raw response (empty content): " + preview);
                // Reasoning-only stream: model emitted reasoning_content but no content.
                // Set retry flag so next turn appends a hint to the user message.
                if (rawResponse.Contains("reasoning_content"))
                {
                    _emptyContentRetry = true;
                }
            }
            return fullContent;
        }

        private void ExecuteToolCalls(string responseJson)
        {
            if (string.IsNullOrEmpty(responseJson)) { Logger.LogWarning("llm reply: null/empty"); return; }
            Dictionary<string, object> root;
            try { root = Json.Parse(responseJson) as Dictionary<string, object>; }
            catch (Exception e) { Logger.LogWarning("parse llm json: " + e.Message); return; }
            if (root == null) { Logger.LogWarning("llm reply not an object"); return; }

            if (!root.TryGetValue("choices", out var choicesObj)) { Logger.LogWarning("llm reply: no choices"); return; }
            var choices = choicesObj as List<object>; if (choices == null || choices.Count == 0) { Logger.LogWarning("llm reply: empty choices"); return; }
            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
            if (msg == null) { Logger.LogWarning("llm reply: no message"); return; }

            string content = (msg.GetValueOrDefault("content") as string)?.Trim();

            // Pull the `act` arguments wherever they live (tool_calls or content JSON).
            // Extra native tool_calls beyond the first become plan steps (the model
            // often answers a tool-forced request with a SEQUENCE of calls — that's
            // its plan, executed in order below).
            var extraCalls = new List<JsonObj>();
            JsonObj args = ExtractActArgs(msg, content, extraCalls);

            if (args == null)
            {
                NotifyInvalidResponse();
                // The model failed to produce a valid act call. Show it the failure:
                // empty reply, prose instead of JSON, or JSON that doesn't parse.
                // The error is injected back into the NEXT perception as 'model_error'
                // so it corrects the format instead of repeating it.
                if (!string.IsNullOrEmpty(content))
                {
                    _lastThought = content.Length > 80 ? content.Substring(0, 80) : content;
                    _lastAction = "say";
                    // Still say the line so the NPC isn't inert, but flag the formatting.
                    string hint = IsSmallModel
                        ? "You replied with PLAIN TEXT. Reply with ONLY a JSON object. Example: {\"progress\":\"done\",\"why\":\"I see a bed\",\"thought\":\"go to bed\",\"action\":\"go_to\",\"name\":\"bed\"}. No words outside the JSON."
                        : "You replied with PLAIN TEXT, not a structured act call. REPLY WITH ONLY ONE compact JSON object: {\"progress\":\"done|blocked|ongoing|changed\",\"why\":\"<1 short clause>\",\"thought\":\"<goal in <10 words>\",\"action\":\"<tool>\",...tool args...}. No prose, no markdown, no explanation around it. Your last text: " + (content.Length > 120 ? content.Substring(0, 120) + "..." : content);
                    SetModelError(hint);
                    Logger.LogInfo("LLM (plain text -> say): " + content);
                    try { ToolSay(new TextArgs(content)); } catch (Exception e) { Logger.LogWarning("say fallback: " + e.Message); }
                }
                else
                {
                    _emptyReplies++;
                    if (_emptyReplies == 1 || Time.unscaledTime - _lastEmptyReplyLog > 20f)
                    {
                        _lastEmptyReplyLog = Time.unscaledTime;
                        Logger.LogWarning("llm reply: no tool call and no content (streak=" + _emptyReplies + ")");
                    }
                    string hint = IsSmallModel
                        ? "Your last reply was EMPTY. Reply with ONLY a JSON object. Example: {\"progress\":\"ongoing\",\"why\":\"looking around\",\"thought\":\"explore\",\"action\":\"look_around\"}"
                        : "Your last reply was EMPTY (no content, no tool call). REPLY WITH ONLY ONE compact JSON object per the act schema: {\"progress\":\"...\",\"why\":\"...\",\"thought\":\"...\",\"action\":\"...\",...}. Never reply with nothing.";
                    SetModelError(hint);
                    // Feed it back to the model so next turn it corrects instead of idling.
                    PushHistory("eval", "empty reply");
                }
                return;
            }

            // The model produced a valid structured act — clear any outstanding error feedback.
            _modelError = null;
            _emptyReplies = 0;  // valid response — reset empty streak
            NotifyValidActJson();
            // Even a structured act can use an invalid tool; that's flagged in ExecuteStep.

            _lastThought = args.S("thought", _lastThought);
            PushThought(_lastThought);
            NoteThought(_lastThought); // repetition guard (goal machine)
            // Track the self-assessment too, so progress notes become visible history.
            string progress = args.S("progress", "");
            string why = args.S("why", "");
            if (progress.Length > 0 || why.Length > 0)
                PushHistory("eval", progress + (why.Length > 0 ? " (" + why + ")" : ""));

            Logger.LogDebug($"act: thought=\"{_lastThought}\"");

            // Fuzzy match the root action for small models.
            string rootAction = args.S("action", "none");
            if (rootAction != "none" && !IsKnownToolWord(rootAction))
            {
                string fuzzy = FuzzyMatchTool(rootAction);
                if (fuzzy != null)
                {
                    Logger.LogInfo("fuzzy root action: " + rootAction + " → " + fuzzy);
                    args = new JsonObj(new Dictionary<string, object>(args.Dict) { ["action"] = fuzzy });
                    rootAction = fuzzy;
                }
            }

            // Execute the root action, then the queued plan[] steps, with a delay
            // between each. Runs on the LLM thread — sleeping here is fine.
            ExecuteStep(args);
            var planRaw = new List<JsonObj>();
            planRaw.AddRange(extraCalls);
            planRaw.AddRange(args.A("plan") ?? new List<JsonObj>());
            var plan = DeduplicatePlan(planRaw);
            int max = Math.Max(1, _cfgMaxPlan.Value);
            if (IsSmallModel) max = Math.Min(max, 4); // small models plan poorly — cap lower
            int ran = 0;
            foreach (var stepRaw in plan)
            {
                if (ran >= max) { Logger.LogWarning($"plan capped at {max} steps"); break; }
                if (!_running) return;
                // Validate and fuzzy-fix each plan step before executing.
                var step = stepRaw;
                string stepAction = step.S("action", "");
                if (!string.IsNullOrEmpty(stepAction) && !IsKnownToolWord(stepAction))
                {
                    string fuzzy = FuzzyMatchTool(stepAction);
                    if (fuzzy != null)
                    {
                        Logger.LogInfo("fuzzy plan step: " + stepAction + " → " + fuzzy);
                        step = new JsonObj(new Dictionary<string, object>(step.Dict) { ["action"] = fuzzy });
                    }
                    else
                    {
                        Logger.LogWarning("plan step has unknown action '" + stepAction + "', skipping");
                        PushHistory("plan", "skip:" + stepAction);
                        ran++;
                        continue;
                    }
                }
                float wait = Mathf.Clamp(step.F("wait", _cfgStepDelay.Value), 0f, 3f);
                if (wait > 0f) Thread.Sleep((int)(wait * 1000f));
                else Thread.Sleep((int)(_cfgStepDelay.Value * 1000f));
                ExecuteStep(step);
                ran++;
            }
        }

        // One action step: optional say, then the action itself.
        // One action in a plan: say first (so speech lands on time), then the movement/
        // interaction itself. outcome is summarized into history so the model sees its effects.
        private void ExecuteStep(JsonObj args)
        {
            string action = args.S("action", "none");
            string say = args.S("say", "");
            _lastAction = action;
            Logger.LogDebug($"  step action={action}" + (say.Length > 0 ? $" say=\"{say}\"" : ""));
            if (!string.IsNullOrEmpty(say))
            {
                try { RunTool("say", new TextArgs(say)); } catch (Exception e) { Logger.LogWarning("say: " + e.Message); }
            }
            if (action != "none")
            {
                object result = RunTool(action, args);
                string summary = SummarizeResult(action, result);
                PushHistory(action, summary);
                // Tool-log ring: attached to report_issue blocks so the dev sees what
                // the model tried right before it got frustrated.
                RecordToolCall(action + "(" + ToolArgsBrief(args) + ") -> " + (string.IsNullOrEmpty(summary) ? "ok" : summary));
                // Notable outcomes become facts the model can recall later.
                if (action == "interact" && summary != null && summary.StartsWith("used ", StringComparison.Ordinal))
                {
                    string what = summary.Substring(5);
                    RememberFact(what + " used" + (_navTargetName != null ? " near " + _navTargetName : ""));
                }
                else if (action == "go_to" && _navTargetName != null)
                    RememberFact("went to " + _navTargetName);
                Logger.LogInfo($"  {action} -> {Json.Write(result)}");
            }
            else PushHistory(action, say.Length > 0 ? "said" : null);
        }

        // Short result digest for the history buffer ("used Bed", "blocked", "ok").
        // Compact the tool result into a few words for the history log
        // ('walk -> blocked', 'interact -> used BedStation').
        private string SummarizeResult(string action, object result)
        {
            try
            {
                var s = Json.Write(result);
                if (string.IsNullOrEmpty(s) || s == "null") return null;
                if (s.Contains("\"ok\":true"))
                {
                    if (action == "interact" && s.Contains("\"used\":\""))
                    {
                        int a = s.IndexOf("\"used\":\"", StringComparison.Ordinal) + 8;
                        int b = s.IndexOf('"', a);
                        if (b > a) return "used " + s.Substring(a, b - a);
                    }
                    if (action == "say") return "said";
                    return _blockedInfo != null ? _blockedInfo : "ok";
                }
                if (s.Contains("\"reason\":\""))
                {
                    int a = s.IndexOf("\"reason\":\"", StringComparison.Ordinal) + 10;
                    int b = s.IndexOf('"', a);
                    if (b > a) return "fail:" + s.Substring(a, b - a);
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // Clean up a plan array: remove 'none' actions, collapse consecutive duplicates,
        // and cap at maxPlan steps. Small models often emit ["none","none","none"] or
        // repeat the same action 5 times. Returns the cleaned list (or the original if clean).
        private List<JsonObj> DeduplicatePlan(List<JsonObj> plan)
        {
            if (plan == null || plan.Count == 0) return plan;
            var cleaned = new List<JsonObj>();
            string lastAction = null;
            foreach (var step in plan)
            {
                string action = step.S("action", "").ToLowerInvariant().Trim();
                if (string.IsNullOrEmpty(action) || action == "none") continue;
                // Skip consecutive duplicate actions (e.g. walk, walk, walk → walk once).
                if (action == lastAction) continue;
                cleaned.Add(step);
                lastAction = action;
            }
            return cleaned.Count != plan.Count ? cleaned : plan;
        }

        // Finds the `act` function arguments regardless of how the model formatted them.
        // Find the act args across any reply shape: real tool_calls, JSON in content,
        // {"name":...,"arguments":{...}}, {"walk":{...}}, markdown-fenced, or truncated. Salvage
        // whatever fields we can when a reasoning model runs out of tokens mid-JSON.
        // Additional tool_calls after the first are returned via `extraCalls`.
        private JsonObj ExtractActArgs(Dictionary<string, object> msg, string content, List<JsonObj> extraCalls = null)
        {
            // 1) Proper tool_calls array.
            if (msg.TryGetValue("tool_calls", out var tcsObj) && tcsObj is List<object> tcs)
            {
                JsonObj first = null;
                foreach (var tcObj in tcs)
                {
                    var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                    var fn = tc.GetValueOrDefault("function") as Dictionary<string, object>; if (fn == null) continue;
                    string name = fn.GetValueOrDefault("name") as string;
                    string argStr = fn.GetValueOrDefault("arguments") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    // Fuzzy match tool name for small models.
                    if (!IsKnownToolWord(name)) { string fuzzy = FuzzyMatchTool(name); if (fuzzy != null) name = fuzzy; }
                    // Nested-JSON models double-encode arguments; unwrap once.
                    var unwrapped = UnwrapArgs(name, argStr);
                    if (first == null) { first = unwrapped; continue; }
                    if (extraCalls != null && unwrapped.S("action", "none") != "none")
                        extraCalls.Add(unwrapped);
                }
                if (first != null) return first;
            }

            // 2) JSON in content: {"thought":...,"action":...} or {"name":"act","arguments":{...}}
            if (!string.IsNullOrEmpty(content))
            {
                var parsed = TryParseObj(content);
                if (parsed != null)
                {
                    if (parsed.ContainsKey("action"))
                    {
                        // Fuzzy match the action name for small models.
                        string act = parsed["action"] as string;
                        if (act != null && !IsKnownToolWord(act))
                        {
                            string fuzzy = FuzzyMatchTool(act);
                            if (fuzzy != null) parsed["action"] = fuzzy;
                        }
                        return new JsonObj(parsed);
                    }
                    string n = (parsed.GetValueOrDefault("name") ?? parsed.GetValueOrDefault("tool")) as string;
                    if (n != null)
                    {
                        object a = parsed.GetValueOrDefault("arguments") ?? parsed.GetValueOrDefault("args");
                        return a is Dictionary<string, object> ad ? new JsonObj(ad)
                             : a is string astr ? new JsonObj(astr)
                             : null;
                    }
                    // Single action like {"walk": {"speed":1}} — treat first known key as action.
                    foreach (var k in new[] { "go_to", "walk", "follow", "walk_ray", "survey", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "throw", "say", "emote", "status", "radar", "report_issue" })
                        if (parsed.ContainsKey(k))
                        {
                            var inner = new Dictionary<string, object> { ["action"] = k, ["thought"] = "implicit" };
                            if (parsed[k] is Dictionary<string, object> innerArgs)
                                foreach (var kv in innerArgs) inner[kv.Key] = kv.Value;
                            return new JsonObj(inner);
                        }
                }
                // 2b) No JSON at all: some chat templates emit a flat OpenAI-style
                //     function-call like "action: go_to(name=\"TopDoor\")" as plain text.
                //     Honor it so the NPC acts instead of reading its own plan aloud.
                var flat = TryParseFlatArgs(content);
                if (flat != null) return flat;

                // 2c) Small models: try aggressive salvage — scan for ANY known tool word
                //     followed by ( or = or : and build an act call from it.
                if (IsSmallModel)
                {
                    var salvage = TrySalvageSmallModel(content);
                    if (salvage != null) return salvage;
                }
            }
            return null;
        }

        // Parse a flat `key: value` reply that isn't JSON, e.g.
        //   progress: ongoing
        //   why: I see a TopDoor to the right...
        //   thought: Explore new room via door to right
        //   action: go_to(name="TopDoor")
        // and also the inline function-call form:
        //   action: go_to(name="TopDoor", at=1)
        //   action: interact(id=2)
        // Returns args if an action (and its call args) could be recovered, else null.
        private JsonObj TryParseFlatArgs(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            var fields = new Dictionary<string, string>();
            string action = null;
            string actionArgs = null;
            string implicitValue = null; // payload from `action: say "text"` space form
            // Some chat templates prepend control/role tokens before the actual reply,
            // e.g. "self<|message|>progress: ongoing\n..." Strip them so the first real
            // "key: value" line is recognized.
            content = StripTemplatePrefix(content);
            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                string key = null, val = null;
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    key = line.Substring(0, colon).Trim().ToLowerInvariant();
                    val = line.Substring(colon + 1).Trim();
                }
                else
                {
                    // No colon in the line. Three shapes:
                    //  - bare call:    "go_to(name=\"BedStation\")" — known tool + parens
                    //  - space form:   "say \"Hi, I'm Yorha!\"" — known tool + space + value
                    string lineLower = line.ToLowerInvariant();
                    int paren = line.IndexOf('(');
                    string beforeParen = paren > 0 ? line.Substring(0, paren).Trim().ToLowerInvariant() : "";
                    int firstSpace = line.IndexOf(' ');
                    string maybeKey = firstSpace > 0 ? line.Substring(0, firstSpace).Trim().ToLowerInvariant() : lineLower;
                    if (paren > 0 && IsKnownToolWord(beforeParen))
                    {
                        // Bare tool call on its own line → treat as the action.
                        key = "action";
                        val = line.Trim();
                    }
                    else if (firstSpace > 0 && IsKnownToolWord(maybeKey))
                    {
                        key = maybeKey;
                        val = line.Substring(firstSpace + 1).Trim();
                    }
                }
                if (key == null || key.Length == 0 || val == null || val.Length == 0) continue;
                if (key == "action")
                {
                    // Value forms: "go_to", "go_to(name=\"TopDoor\")", or the loose
                    // "say \"Hi! I'm Yorha\"" / "say Hi!" space form.
                    string v = val;
                    int parenPos = v.IndexOf('(');
                    if (parenPos > 0)   // "go_to(name=\"TopDoor\")" (paren anywhere)
                    {
                        action = v.Substring(0, parenPos).Trim();
                        int close = v.LastIndexOf(')');
                        actionArgs = close > parenPos ? v.Substring(parenPos + 1, close - parenPos - 1) : "";
                    }
                    else
                    {
                        int space = v.IndexOf(' ');
                        string tool = space > 0 ? v.Substring(0, space).Trim() : v;
                        string remainder = space > 0 ? v.Substring(space).Trim() : "";
                        if (remainder.Length > 0 && IsKnownToolWord(tool))
                        {
                            action = tool;   // "say \"Hi!\"" — tool with a payload value
                            implicitValue = remainder;
                        }
                        else action = tool;   // bare "say" / "go_to"
                    }
                }
                else fields[key] = val;
            }
            // Some chat templates drop the "action:" header and just emit the tool's
            // arg field directly: "say: Hi, I'm Yorha!" or "go_to: TopDoor". Infer the
            // action from a known tool key when no explicit action line was present.
            if (action == null)
            {
                string[] knownTools = new[] { "say", "emote", "go_to", "walk", "follow", "walk_ray", "survey", "remember", "look_around", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "throw", "status", "radar", "report_issue", "none" };
                foreach (var k in knownTools)
                {
                    string v;
                    if (fields.TryGetValue(k, out v))
                    {
                        action = k;
                        // "say: Hi, I'm Yorha!" -> say param = the text; quoted "TopDoor"
                        // under go_to -> name. Map the field's own value onto its primary
                        // arg so the tool gets real input, not just an empty call.
                        string content2 = v;
                        if (content2.Length > 0)
                        {
                            content2 = content2.Trim().Trim('"', '\'');
                            if (content2.Length > 0)
                            {
                                if (k == "say") fields[k] = content2;
                                else if (k == "go_to") { if (!fields.ContainsKey("name")) fields["name"] = content2; }
                                else if (k == "remember") { if (!fields.ContainsKey("mem")) fields["mem"] = content2; }
                                else if (k == "follow")
                                {
                                    // Parse "on:true" / "on:false" into fields so ToolFollow gets the param.
                                    var fp = content2.Split(':');
                                    if (fp.Length == 2 && bool.TryParse(fp[1].Trim(), out bool onVal))
                                        fields["on"] = onVal.ToString();
                                    else if (bool.TryParse(content2.Trim(), out onVal))
                                        fields["on"] = onVal.ToString();
                                }
                                else if (k == "report_issue") { if (!fields.ContainsKey("msg")) fields["msg"] = content2; }
                                else if (k == "radar") { if (!fields.ContainsKey("filter")) fields["filter"] = content2; }
                            }
                        }
                        break;
                    }
                }
            }
            if (action == null) return null;
            // Validate the action is one we know; if not, don't fabricate.
            // Use fuzzy matching for small models that produce misspelled tool names.
            bool known = IsKnownToolWord(action);
            if (!known)
            {
                string fuzzy = FuzzyMatchTool(action);
                if (fuzzy != null) { action = fuzzy; known = true; }
            }
            if (!known) return null;

            var d = new Dictionary<string, object> { ["action"] = action };
            foreach (var f in fields) d[f.Key] = f.Value;

            // `action: say "text"` — hang the payload off the tool's primary arg.
            if (implicitValue != null && implicitValue.Length > 0)
            {
                string iv = implicitValue.Trim().Trim('"', '\'');
                if (iv.Length > 0)
                {
                    if (action == "say" && !d.ContainsKey("say")) d["say"] = iv;
                    else if (action == "go_to" && !d.ContainsKey("name")) d["name"] = iv;
                    else if (action == "remember" && !d.ContainsKey("mem")) d["mem"] = iv;
                }
            }

            // Parse inline call args like name="TopDoor" or id=2 or at=1 into the dict.
            if (actionArgs != null && actionArgs.Length > 0)
                ApplyCallArgs(d, actionArgs);

            // Bolt a 'thought' onto anything that didn't carry one.
            if (!d.ContainsKey("thought")) d["thought"] = "implicit";
            Logger.LogInfo("flat-parse recovered action: " + action + (actionArgs != null && actionArgs.Length > 0 ? " (" + actionArgs + ")" : ""));
            return new JsonObj(d);
        }

        // True if the lowercased string is one of the tools the model can call
        // (legacy aliases jump/move_to/walk_ray included — RunTool maps them).
        private bool IsKnownToolWord(string key)
        {
            if (ModuleRegistry.IsModuleTool(key)) return true;
            switch (key)
            {
                case "walk":
                case "walk_ray":
                case "go_to":
                case "follow":
                case "survey":
                case "stop":
                case "look":
                case "jump":
                case "exit_station":
                case "crouch":
                case "move_to":
                case "interact":
                case "grab":
                case "drop":
                case "say":
                case "status":
                case "remember":
                case "look_around":
                case "set_goal":
                case "complete_goal":
                case "drop_goal":
                case "forget":
                case "radar":
                case "sonar":
                case "report_issue":
                case "report":
                case "bug_report":
                case "complain":
                case "throw":
                case "toss":
                case "activate":
                case "none": return true;
                default: return false;
            }
        }

        // Fuzzy match a potentially-misspelled tool name to the closest valid tool.
        // Returns the canonical tool name or null if nothing close enough.
        // Designed for small models that produce "goo_to", "saaay", "intercat", etc.
        private static string FuzzyMatchTool(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string s = input.ToLowerInvariant().Trim();
            // Exact match first.
            if (IsKnownToolWordStatic(s)) return s;
            // Common small-model misspellings: doubled letters, transposed chars, extra/missing chars.
            // Build a list of (pattern → canonical) for the most frequent errors.
            var aliases = new Dictionary<string, string>
            {
                ["goo_to"] = "go_to",
                ["go_too"] = "go_to",
                ["goto"] = "go_to",
                ["go_t"] = "go_to",
                ["wal"] = "walk",
                ["waalk"] = "walk",
                ["wakl"] = "walk",
                ["wakk"] = "walk",
                ["waalk_ray"] = "walk_ray",
                ["walk_r"] = "walk_ray",
                ["suvey"] = "survey",
                ["survay"] = "survey",
                ["survy"] = "survey",
                ["surver"] = "survey",
                ["surevy"] = "survey",
                ["intercat"] = "interact",
                ["interac"] = "interact",
                ["intreract"] = "interact",
                ["interatc"] = "interact",
                ["intract"] = "interact",
                ["rember"] = "remember",
                ["remembr"] = "remember",
                ["remmber"] = "remember",
                ["rememeber"] = "remember",
                ["reember"] = "remember",
                ["saaay"] = "say",
                ["saay"] = "say",
                ["sya"] = "say",
                ["sy"] = "say",
                ["cum"] = "orgasm",
                ["cummed"] = "orgasm",
                ["exitt_station"] = "exit_station",
                ["exit_statoin"] = "exit_station",
                ["exit_staton"] = "exit_station",
                ["exit_stat"] = "exit_station",
                ["loo_around"] = "look_around",
                ["look_arond"] = "look_around",
                ["look_ardound"] = "look_around",
                ["lookarond"] = "look_around",
                ["move_o"] = "move_to",
                ["mov_to"] = "move_to",
                ["moveo"] = "move_to",
                ["look_aound"] = "look_around",
                ["folow"] = "follow",
                ["follows"] = "follow",
                ["folo"] = "follow",
                ["follw"] = "follow",
            };
            string alias;
            if (aliases.TryGetValue(s, out alias)) return alias;
            // Substring containment: "go" in a blob → go_to, "walk" in a blob → walk, etc.
            // Order matters: check longer matches first to avoid "go" matching before "go_to".
            var orderedList = new List<string> { "complete_goal", "set_goal", "drop_goal", "exit_station", "look_around", "walk_ray", "go_to", "follow", "move_to", "interact", "remember", "forget", "survey", "walk", "look", "jump", "crouch", "grab", "drop", "throw", "say", "stop", "status", "radar", "report_issue", "none" };
            foreach (var mt in ModuleRegistry.ModuleToolNames()) if (!orderedList.Contains(mt)) orderedList.Add(mt);
            string[] ordered = orderedList.ToArray();
            foreach (var tool in ordered)
                if (s.Contains(tool)) return tool;
            // Prefix match: first 3+ chars of a known tool.
            foreach (var tool in ordered)
                if (tool.Length >= 3 && s.Length >= 3 && tool.StartsWith(s.Substring(0, Math.Min(3, s.Length)))) return tool;
            // Levenshtein distance ≤2 from any known tool.
            string best = null; int bestDist = 999;
            foreach (var tool in ordered)
            {
                int d = Levenshtein(s, tool);
                if (d < bestDist && d <= 2) { bestDist = d; best = tool; }
            }
            return best;
        }

        private static bool IsKnownToolWordStatic(string key)
        {
            if (ModuleRegistry.IsModuleTool(key)) return true;
            switch (key)
            {
                case "walk":
                case "walk_ray":
                case "go_to":
                case "follow":
                case "survey":
                case "stop":
                case "look":
                case "jump":
                case "exit_station":
                case "crouch":
                case "move_to":
                case "interact":
                case "grab":
                case "drop":
                case "say":
                case "status":
                case "remember":
                case "look_around":
                case "set_goal":
                case "complete_goal":
                case "drop_goal":
                case "forget":
                case "radar":
                case "sonar":
                case "report_issue":
                case "report":
                case "bug_report":
                case "complain":
                case "throw":
                case "toss":
                case "activate":
                case "none": return true;
                default: return false;
            }
        }

        private static int Levenshtein(string a, string b) => ChatSimilarity.LevenshteinDistance(a, b);

        // Strip leading chat-template control/role tokens from a reply, e.g.
        // "self<|message|>progress: ongoing..." or "<|user|>...". Repeatedly removes
        // a leading <|...|> token plus any word glued before it (like "self").
        // No-op for normal replies.
        private string StripTemplatePrefix(string content)
        {
            // Remove chat-template control/role tokens anywhere in the reply, e.g. the
            // leading "self<|message|>progress:…" or the embedded "say<|message|>Hi!…".
            //  - If the word glued before the token is a TOOL word ("say", "go_to", …),
            //    keep the word and replace the token with a space ("say Hi!").
            //  - Otherwise the word is a role token ("self", "user", …) and is dropped
            //    along with the token so "self<|message|>progress:" -> "progress:".
            if (content.IndexOf("<|", StringComparison.Ordinal) < 0) return content;
            var sb = new System.Text.StringBuilder(content.Length);
            int i = 0, n = content.Length;
            while (i < n)
            {
                if (i + 1 < n && content[i] == '<' && content[i + 1] == '|')
                {
                    int gt = content.IndexOf('>', i);
                    if (gt >= 0)
                    {
                        // Find the word immediately before this token (if any).
                        int wordStart = i;
                        while (wordStart > 0 && !char.IsWhiteSpace(content[wordStart - 1]) && content[wordStart - 1] != '<')
                            wordStart--;
                        string word = i > wordStart ? content.Substring(wordStart, i - wordStart) : "";
                        bool keepWord = word.Length > 0 && IsKnownToolWord(word);

                        // Back out the word we already appended.
                        int trim = i - wordStart;
                        if (sb.Length >= trim) sb.Length -= trim;

                        if (keepWord) sb.Append(word);

                        int end = gt + 1;
                        // Separate the kept tool word from what follows, if they'd touch.
                        if (keepWord && sb.Length > 0 && sb[sb.Length - 1] != ' ' && end < n && content[end] != ' ')
                            sb.Append(' ');
                        else if (!keepWord && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            sb.Append(' ');
                        i = end;
                        continue;
                    }
                }
                sb.Append(content[i]);
                i++;
            }
            return sb.ToString().TrimStart('\n', '\r', ' ');
        }

        // Turn "name=\"TopDoor\", at=1, id=2, speed=0.5" into typed fields in dict.
        private void ApplyCallArgs(Dictionary<string, object> d, string callArgs)
        {
            // Split on commas not inside quotes.
            var parts = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuote = false;
            foreach (char c in callArgs)
            {
                if (c == '"') inQuote = !inQuote;
                if (c == ',' && !inQuote) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());

            foreach (var partRaw in parts)
            {
                string part = partRaw.Trim();
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 1) continue;
                string k = part.Substring(0, eq).Trim();
                string v = part.Substring(eq + 1).Trim().Trim('"', '\'');
                if (k.Length == 0) continue;
                double num;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                    d[k] = num;
                else
                    d[k] = v;
            }
        }

        private JsonObj UnwrapArgs(string name, string argStr)
        {
            if (string.IsNullOrEmpty(argStr)) return TrySalvageTruncated(argStr) ?? new JsonObj("{}");
            var parsed = TryParseObj(argStr);
            if (parsed == null)
            {
                // Truncated/unterminated JSON from a length-cut reasoning model.
                var salvaged = TrySalvageTruncated(argStr);
                if (salvaged != null) return salvaged;
                return new JsonObj("{}");
            }
            if (!parsed.ContainsKey("action") && parsed["arguments"] is string nested)
            {
                var inner = TryParseObj(nested);
                if (inner != null && inner.ContainsKey("action")) return new JsonObj(inner);
            }
            if (name != "act" && name != null) parsed["action"] = name;
            return new JsonObj(parsed);
        }

        // Given partial JSON like {"action":"wal...   or   {"action":"walk","say":"hi
        // extract whatever action/say we can with regex so the NPC still acts.
        // (Delegates to the dependency-free TruncatedArgs module, whose regexes are compiled once
        // instead of per call — see src/TruncatedArgs.cs.)
        private JsonObj TrySalvageTruncated(string s)
        {
            var salvaged = TruncatedArgs.Salvage(s);
            if (salvaged != null)
                Logger.LogWarning("salvaged truncated act args: action=" + salvaged.Dict["action"]);
            return salvaged;
        }

        // Small-model salvage: scan freeform text for a known tool word and infer
        // an act call from it. Handles outputs like:
        //   "I'll go to the bed. go_to(name=\"BedStation\")"
        //   "Let me walk forward. walk(duration=2)"
        //   "say Hello there!"  (just the tool word + payload)
        private JsonObj TrySalvageSmallModel(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string lower = text.ToLowerInvariant();
            // Try each known tool and see if it appears in the text with args.
            string[] tools = new[] { "go_to", "walk", "follow", "walk_ray", "survey", "interact", "move_to", "look_around", "look", "remember", "say", "jump", "exit_station", "crouch", "grab", "drop", "throw", "stop", "status", "report_issue", "radar", "none" };
            foreach (var tool in tools)
            {
                // Look for "tool(args...)" pattern.
                int idx = lower.IndexOf(tool + "(");
                if (idx >= 0)
                {
                    int open = idx + tool.Length;
                    int close = text.IndexOf(')', open);
                    string args = close > open ? text.Substring(open + 1, close - open - 1) : "";
                    var d = new Dictionary<string, object> { ["action"] = tool, ["thought"] = "salvaged" };
                    if (args.Length > 0) ApplyCallArgs(d, args);
                    Logger.LogInfo("small-model salvage: " + tool + "(" + args + ")");
                    return new JsonObj(d);
                }
                // Look for "tool: value" or "tool value" patterns (e.g. "say Hello").
                int spaceIdx = -1;
                if (lower.StartsWith(tool + ": ")) spaceIdx = tool.Length + 2;
                else if (lower.StartsWith(tool + " ")) spaceIdx = tool.Length + 1;
                if (spaceIdx > 0 && tool != "none")
                {
                    string payload = text.Substring(spaceIdx).Trim().Trim('"', '\'');
                    if (payload.Length > 0)
                    {
                        var d = new Dictionary<string, object> { ["action"] = tool, ["thought"] = "salvaged" };
                        if (tool == "say" && !d.ContainsKey("say")) d["say"] = payload;
                        else if (tool == "go_to" && !d.ContainsKey("name")) d["name"] = payload;
                        else if (tool == "remember" && !d.ContainsKey("mem")) d["mem"] = payload;
                        else if (tool == "report_issue" && !d.ContainsKey("msg")) d["msg"] = payload;
                        else if (tool == "radar" && !d.ContainsKey("filter")) d["filter"] = payload;
                        else if (tool == "follow")
                        {
                            // Parse "on:true" / "on:false" into the dict.
                            var fp = payload.Split(':');
                            if (fp.Length == 2 && bool.TryParse(fp[1].Trim(), out bool onVal))
                                d["on"] = onVal;
                            else if (bool.TryParse(payload.Trim(), out onVal))
                                d["on"] = onVal;
                        }
                        Logger.LogInfo("small-model salvage: " + tool + " → " + payload);
                        return new JsonObj(d);
                    }
                }
            }
            // Last resort: if the text contains a JSON-like fragment with "action", try to extract it.
            int actIdx = lower.IndexOf("\"action\"");
            if (actIdx >= 0)
            {
                // Try to find a JSON object starting a few chars before "action".
                int start = lower.LastIndexOf('{', actIdx);
                if (start >= 0 && start > actIdx - 30)
                {
                    var parsed = TryParseObj(text.Substring(start));
                    if (parsed != null && parsed.ContainsKey("action"))
                    {
                        string act = parsed["action"] as string;
                        if (act != null && !IsKnownToolWord(act))
                        {
                            string fuzzy = FuzzyMatchTool(act);
                            if (fuzzy != null) parsed["action"] = fuzzy;
                        }
                        return new JsonObj(parsed);
                    }
                }
            }
            return null;
        }

        private static Dictionary<string, object> TryParseObj(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Strip markdown fences that chat models love to wrap JSON in.
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
                s = s.Trim();
            }
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return Json.Parse(s.Substring(a, b - a + 1)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        // Adapter so say can be called with a raw string.
        private class TextArgs : JsonObj
        {
            public TextArgs(string text) : base(new Dictionary<string, object> { ["text"] = text }) { }
        }

        // Dispatch the LLM's chosen tool. Always returns a result object — never throws —
        // so a bad tool call is just a 'fail:' line in history, not a crash.
        private object RunTool(string name, JsonObj p)
        {
            // Fuzzy match the tool name for small models.
            if (name != null && !IsKnownToolWord(name))
            {
                string fuzzy = FuzzyMatchTool(name);
                if (fuzzy != null) { Logger.LogInfo("fuzzy tool: " + name + " → " + fuzzy); name = fuzzy; }
            }
            // Swallowed (this body is inside someone's stomach): hands and legs reach
            // nothing out here — body tools fail fast (and explain) instead of
            // nudging physics around inside the eater. Speech/memory/status stay.
            if (Swallowed && !ToolAllowedWhileSwallowed(name))
            {
                Logger.LogInfo("swallow: tool '" + name + "' refused while swallowed (mind only)");
                return new { ok = false, reason = "swallowed",
                    note = "you are inside a stomach — hands, legs and machines do nothing from here; say/emote, remember, goals and status still work" };
            }
            try
            {
                switch (name)
                {
                    case "walk": return ToolWalk(p);
                    case "walk_ray": return ToolWalk(p); // legacy alias — walk now takes turn_deg/ray_deg
                    case "go_to": return ToolGoTo(p);
                    case "follow": return ToolFollow(p);
                    case "survey": return ToolSurvey(p);
                    case "remember": return ToolRemember(p);
                    case "forget": return ToolForget(p);
                    case "set_goal": return ToolSetGoal(p);
                    case "complete_goal": return ToolCompleteGoal(p);
                    case "drop_goal": return ToolDropGoal(p);
                    case "look_around": return ToolLookAround(p);
                    case "stop": return ToolStop();
                    case "look": return ToolLook(p);
                    case "jump": return ToolJump(); // legacy alias — walk(jump=true)
                    case "exit_station": return ToolExitStation();
                    case "crouch": return ToolCrouch(p);
                    case "move_to": return ToolGoTo(p); // legacy alias — go_to plans the route
                    case "interact": return ToolInteract(p);
                    case "grab": return ToolGrab(p);
                    case "drop": return ToolDrop();
                case "radar":
                case "sonar": return ToolRadar(p);
                case "report_issue":
                case "bug_report":
                case "complain":
                case "report": return ToolReportIssue(p);
                case "throw":
                case "toss":
                case "activate": return ToolThrow(p);
                    case "say": return ToolSay(p);
                    case "emote": return ToolEmote(p.S("text", p.S("say", p.S("message", ""))));
                    case "status": return ToolStatus();
                    case "none": return new { ok = true };
                    default:
                        // Module tools (BodyControl, Farming, Identity, ...) register
                        // via ModuleRegistry — dispatch before declaring unknown.
                        {
                            object moduleResult;
                            if (ModuleRegistry.TryTool(name, this, p, out moduleResult))
                                return moduleResult;
                        }
                        // Model was asked for act= but produced a legacy/unknown name.
                        SetModelError("Invalid action '" + name + "'. Valid actions: go_to, walk, follow, stop, survey, look_around, look, exit_station, crouch, interact, grab, drop, say, emote, buy, remember, forget, set_goal, complete_goal, drop_goal, ask, status, none, " + string.Join(", ", ModuleRegistry.ModuleToolNames()) + ".");
                        Logger.LogWarning("unknown action: " + name);
                        return new { ok = false, reason = "unknown_tool", tool = name };
                }
            }
            catch (Exception e)
            {
                // Never let Mono's "Illegal byte sequence" exception message into the
                // tool result/state — it's toxic (contains a broken code unit) and
                // would be re-serialized every tick, turning one bad LLM byte into a
                // permanent loop. Replace with a stable ASCII reason.
                string msg = e.Message;
                if (msg != null && (msg.IndexOf("Illegal byte sequence", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("String conversion", StringComparison.OrdinalIgnoreCase) >= 0))
                    msg = "bad_encoding_from_llm";
                else msg = Sanitize(msg);
                return new { ok = false, reason = msg };
            }
        }

        // One mega-tool. Forcing tool_choice = act means the model can never
        // "just reply with text" — every tick yields a structured action.
        // Raw JSON schema for the act-args object — sent as response_format so any
        // model (not just tool-calling ones) is constrained to emit exactly this shape.
        private Dictionary<string, object> ActSchema()
        {
            var stepProps = ActionParamProps();
            stepProps["thought"] = Str("thought", "current goal summary, <15 words");
            stepProps["wait"] = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            var planItems = new Dictionary<string, object> { ["type"] = "object", ["properties"] = stepProps, ["required"] = new object[] { "action" }, ["additionalProperties"] = false };

            // Root response shape: reasoning fields FIRST so the model works out loud
            // before naming an action, then the action, then the optional plan.
            var props = new Dictionary<string, object>
            {
                ["progress"] = Str("progress", "one word: what happened with your previous goal (done|blocked|ongoing|changed)"),
                ["why"] = Str("why", "one short sentence justifying this action, referencing what you see"),
                ["thought"] = Str("thought", "restate your current goal in <15 words"),
                ["action"] = ActionParamProps()["action"],
            };
            foreach (var kv in ActionParamProps())
                if (kv.Key != "action") props[kv.Key] = kv.Value;
            props["wait"] = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            props["plan"] = new Dictionary<string, object> { ["type"] = new object[] { "array", "null" }, ["description"] = "optional follow-up actions, in order", ["items"] = planItems, ["maxItems"] = 8 };

            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new object[] { "progress", "why", "thought", "action" },
                ["additionalProperties"] = false,
            };
        }

        private Dictionary<string, object> ActionParamProps()
        {
            // Module-registered tools extend the action enum and the param schema —
            // each module ships its own file, no edits to this method needed.
            var extra = ModuleRegistry.ModuleToolNames();
            var enumVals = new List<object> { "go_to", "walk", "follow", "stop", "survey", "remember", "look_around", "look", "exit_station", "crouch", "interact", "grab", "drop", "throw", "say", "emote", "radar", "report_issue", "status", "none" };
            foreach (var e in extra) if (!enumVals.Contains(e)) enumVals.Add(e);

            var props = new Dictionary<string, object>
            {
                // ACTION FIRST: reasoning models burn tokens on "thought" and can
                // truncate before emitting the action. Emitting action (and the
                // movement/say params) first means even a truncated call still acts.
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = enumVals.ToArray() },
                ["say"] = Str("say", "optional <10 words — posts to the real in-game chat window AND a speech bubble"),
                ["text"] = Str("text", "emote: what your body does, spoken as *action* not dialog (e.g. 'curls up by the fire')"),
                ["name"] = Str("name", "go_to: place to reach by name — 'bed' 'toilet' 'bath' 'nest' 'sex' 'seat' 'door' 'bodyswap', a PLAYER'S chat name (host or from 'people'), or any usable's name"),
                ["id"] = Num("id", "go_to/interact: the numeric 'id' of a specific object from your 'nearby' or survey result — use this to target an exact object instead of matching by name (e.g. go_to id:3, interact id:2)"),
                ["at"] = Num("at", "go_to: stop this many meters SHORT of the target (default 1 — so you reach the object, not bump it; 0 = go right up to it)"),
                ["heading_deg"] = Num("heading_deg", "survey: absolute world yaw to look toward (omit = your current facing)"),
                ["range"] = Num("range", "survey: how far to probe, 2..40m (default = your ray range)"),
                ["mem"] = Str("mem", "optional: a fact to remember for future turns (e.g. 'bed is upstairs', 'player is friendly') — stored in your long-term memory"),
                ["crouch"] = Num("crouch", "0..1 how much to crouch (0=stand, 1=full crouch) — also the state for the 'crouch' action"),
                ["speed"] = Num("speed", "walk forward -1..1"),
                ["strafe"] = Num("strafe", "walk sideways: +1=right, -1=left, -1..1 (for tight maneuvers, doorways, squeezing past furniture)"),
                ["duration"] = Num("duration", "walk: seconds to keep going, 0.1..8 (default 2, then auto-stop)"),
                ["turn_deg"] = Num("turn_deg", "walk: degrees to turn first (+right/-left); nearby.dir tells you which way"),
                ["jump"] = Bool("jump", "walk: also jump (hops down ledges / over small gaps; also exits a station if you're in one)"),
                ["on"] = Bool("on", "follow: true = stay near the host player while you keep acting, false = release"),
                ["yaw_deg"] = Num("yaw_deg", "look abs yaw"),
                ["pitch_deg"] = Num("pitch_deg", "look abs pitch"),
                ["sweep"] = Num("sweep", "look_around: degrees to sweep around (default 120)"),
                ["filter"] = Str("filter", "radar: glyphs to show in the sonar map, e.g. 'U,P' or words (wall/sill/window/usable/kobold/player) — 'all' clears; a set filter also applies to the perception sonar until cleared"),
                ["msg"] = Str("msg", "report_issue: ONE line — what is broken/frustrating and what you tried (e.g. 'go_to(name=blender) kept picking the far blender'); files a note the human maintainers read later"),
            };
            // Merge module-registered tool parameters (thrust, erection, plant, ...).
            foreach (var kv in ModuleRegistry.ModuleParamSchemas())
                if (!props.ContainsKey(kv.Key)) props[kv.Key] = kv.Value;
            return props;
        }

        private Dictionary<string, object> Num(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "number", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Bool(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "boolean", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Str(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "string", "null" }, ["description"] = d }; }
    }
}
