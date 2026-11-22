# ModHooks — the public event API for other mods (v1.13.0)

KKLLMNPC exposes an in-process C# event bus so other BepInEx plugins can react
to what the AI agents (and the mod) do, with the signals passed **honestly** —
vore is never labeled sex, and every fire site is a real state transition, not
narration spam. Built for Rose's VR mod (spectate perspective hand-off) and
anyone else who needs the same plumbing.

## Subscribing

Reference `KKLLMNPC.dll` from your plugin and subscribe (do it on load;
unsubscribe on teardown):

```csharp
// A VR rig that takes over the spectate camera when an agent eats the player:
KKLLMNPC.ModHooks.SpectateStarted += e =>
{
    // e.Player  — the local player's parked body (inside the eater)
    // e.Eater   — the body whose perspective the player now watches through
    // e.EaterLabel — display name of the eater
    // e.Pov     — the built-in camera's POV (0 first-person, 1 third)
    KKLLMNPC.ModHooks.ExternalSpectateCamera = true;  // claim the camera — the built-in writer stands down
    KKLLMNPC.ModHooks.ExternalPlayerControl  = true;  // claim input too — the component lock + per-tick suppression stand down
    // attach your own VR camera/rig to e.Eater here
};
KKLLMNPC.ModHooks.SpectateEnded   += e => { /* e.Cause: hatch|abort|eater-lost|scene-leave|restart — release your rig */ };
KKLLMNPC.ModHooks.SpectateHatched += e => { /* the player woke grown at the egg's hatch spot */ };
KKLLMNPC.ModHooks.SpectateEggLaid += e => { /* the rebirth egg exists; e.Fingerprint matches its genes */ };
// VR HUD + interactions during the watch:
//   ModHooks.SpectateHudLine()      — the HUD text (render it on your own overlay)
//   ModHooks.SpectateStruggle01()   — escape meter 0-1 for a gauge
//   ModHooks.SubmitPlayerStruggle() — map a controller gesture to the SPACE-mash escape
//   ModHooks.SubmitPreyPoint(pos, name) — controller-ray pointing, same route as flat clicks
// Before patching engine methods, check ModHooks.PatchOwnerIds for conflicts (see below).
```

`ExternalSpectateCamera` is the hand-off flag: while set, the mod's built-in
last-writer spectate camera does not touch the game camera at all (checked on
every pre-cull, before any write), so your rig and the built-in writer never
fight for the same frame. The manager clears the flag itself on SpectateEnded /
SpectateHatched so a forgotten claim can't strand the next watch. Polling
alternative (survives scene loads, no missed events):
`ModHooks.PlayerSpectating` and `ModHooks.EaterOfPlayer`.

## Events

### Vore lifecycle — `VoreHookEvent`

| Event | Fires when |
|---|---|
| `VoreStarted` | a swallow session begins (the pull-in starts) |
| `VoreFullyInside` | the meal is all the way in; the conversion begins |
| `VoreEnded` | the session is over — `Outcome` names the ending |

`Outcome` values: `thrown_up`, `poured_out`, `hold_ended` (Endo release),
`interrupted_victim_lost`, `launch_brake_release`, `aborted`, and
`converted_<fate>` where fate is one of `egg`, `bellyegg`, `grow`, `destroy`,
`milk`, `cum`, `milkheld`, `cumheld`, `endo`.

Payload: `Eater`, `Victim` (Kobold refs), `VictimIsPlayer` (the local player is
the meal — spectate runs), `EaterName`, `VictimName`, `EaterId`, `VictimId`
(stable instance ids), `Hole` (entry hole label).

### Spectate — `SpectateHookEvent`

| Event | Fires when |
|---|---|
| `SpectateStarted` | the local player was eaten by an agent; the perspective hand-off begins |
| `SpectateEggLaid` | the conversion produced the rebirth egg (`Fingerprint` = its gene fingerprint) |
| `SpectateEnded` | the watch ends (`Cause` = `hatch`/`abort`/`eater-lost`/`scene-leave`/`restart`) |
| `SpectateHatched` | the hatch path: the player was revived grown at the egg's hatch spot with the egg's genes |

Note the split: the abort/lost/scene-leave/restart endings fire **only**
`SpectateEnded`; the happy ending fires **only** `SpectateHatched`. Pair
start↔(end|hatch) in your subscriber.

### Sex — `SexHookEvent` (REAL sex only)

`SexStarted` / `SexEnded` fire when a real penetrator enters / leaves a hole on
an agent's body. The swallow system's **labeled vore insert** (the swallowed
meal's body acting as the shaft — `vore-insert:` entries) deliberately never
fires these; that's the whole point of the phantom-sex signaling fix this API
ships with. Payload: `PenetratorOwner` (null = ownerless toy), `PenetratedBody`
(null = non-kobold target), their ids, `Hole`, `Model` (the shaft's model name,
e.g. `KDEFuta`, `dild 1`).

### Possession — `PossessionHookEvent`

`PossessionStarted` (an agent mind takes a body; `AgentName` = the NPC mind's
name) / `PossessionEnded` (the body is released back to the world).

## Threading + safety contract

- **Every event fires on the Unity main thread.** Fire sites are the swallow
  tick, the render pre-cull gate, PenetrationTech callbacks and possession
  wiring — all main-thread. Subscribers may touch Unity objects but must be
  fast and must not block.
- **Exception isolation** (`ModHooksCore`, unit-tested): a throwing subscriber
  is counted (`ModHooksCore.Errors`, `LastError`) and skipped; the remaining
  subscribers still run and the plugin lives. One broken mod cannot take the
  plugin down.
- **No spam**: events are state transitions (start/fully-in/end per session),
  not per-frame ticks. Nothing fires every frame.

## What the payload types look like

```csharp
public sealed class VoreHookEvent      { Kobold Eater; Kobold Victim; bool VictimIsPlayer;
                                         string EaterName; string VictimName; int EaterId; int VictimId;
                                         string Hole; string Outcome; }
public sealed class SpectateHookEvent  { Kobold Player; Kobold Eater; string EaterLabel; int Pov;
                                         string Cause; string Fingerprint; }
public sealed class SexHookEvent       { Kobold PenetratorOwner; Kobold PenetratedBody;
                                         int PenetratorOwnerId; int PenetratedBodyId; string Hole; string Model; }
public sealed class PossessionHookEvent{ Kobold Body; int BodyId; string AgentName; }
```

All in the `KKLLMNPC` namespace. Kobold is the game's own type (Assembly-CSharp)
— you already reference it as a KoboldKare mod.

## Coexistence with controls / VR mods

This mod drives bodies (agents) and takes over the player during a spectate
watch. A VR rig with its own locomotion, configs and Harmony patches needs to
know every place the two could fight — in BOTH directions. This section is the
contract; keep it two-way: if you need a yield that isn't here, ask — the
claim-flag pattern below is the established mechanism.

### Claim flags (we yield; you own)

| Flag | While set, we stop… | Set it on | We clear it on |
|---|---|---|---|
| `ModHooks.ExternalSpectateCamera` | writing the spectate camera (checked per pre-cull before ANY write — the built-in last-writer stands down entirely) | `SpectateStarted` | release/hatch |
| `ModHooks.ExternalPlayerControl` | locking the player's input: `PlayerPossession`/`CameraSwitcher` stay enabled, the per-tick input-field zeroing (`SetMovementEnabled(false)`, grabbing/rotating/hip-tracking/switchedMode via reflection) is skipped | any time before or during the watch (`VoreStarted` fires before `SpectateStarted`, so early claims work; a claim inside your `SpectateStarted` handler is also honored — the lock taken before your handler ran is undone) | release/hatch |

What a claim does NOT yield: the spectate machinery still repositions the
parked player body to the eater's belly every tick (`BellyOf(eater)`) — that
is the honest world state (the player IS inside someone), not a control
fight. Move the eater, not the meal; the parking follows.

If you claim input control, suppressing the flat game's input (so a swallowed
player can't WASD around) is YOUR responsibility — in VR there is usually
nothing to suppress, which is exactly why the claim exists.

### Data feeds (we computed it; read it, don't re-implement)

| Member | What |
|---|---|
| `ModHooks.SpectateHudLine()` | the current spectate HUD line in words (null = not spectating) — host label, hatch promise, struggle note |
| `ModHooks.SpectateStruggle01()` | escape meter 0-1 (the SPACE-mash mini-game's meter) |
| `ModHooks.SubmitPlayerStruggle()` | ONE struggle press from any device — same meter gain/narration/vomit-threshold/remote-abort as the flat SPACE poll; returns whether a watch consumed it |
| `ModHooks.SubmitPreyPoint(Vector3 worldPos, string hitName)` | the v1.11.0 prey click-to-point from any device: a world position (your controller ray's hit) becomes the guest's pointing event for the host agent — bearing/distance/world coords; returns a feedback line for your own HUD |

The flat HUD (OnGUI box) and mouse click handler both stay — on a desktop
session they work; in VR you render your own from the feeds above. The flat
click and `SubmitPreyPoint` share ONE routing path, so both can never drift.

### Patch inventory (we patched; coordinate before you patch the same)

All KKLLMNPC Harmony patches share the owner id **`com.kk.llmnpc.patches`**
(exposed as `ModHooks.PatchOwnerIds`). Detect conflicts before patching:

```csharp
var info = Harmony.GetPatchInfo(someEngineMethod);
bool ours = info != null && info.Owners != null
            && System.Linq.Enumerable.Any(info.Owners, o => o == "com.kk.llmnpc.patches");
```

| Target (Assembly-CSharp) | Kind | Why | VR-relevant? |
|---|---|---|---|
| `CharacterControllerAnimator.<AnimationRoutine>d__79.MoveNext` | prefix | skips the station-entry rigidbody.rotation write **for LLM-driven bodies only** (`SkipRotationIfOurs` guard) | only if you patch the same coroutine — our prefix passes non-agent bodies through untouched |
| `CharacterControllerAnimator.<StopAnimationRoutine>…MoveNext` | prefix | same for station exit | same |
| `User.SortGrabbables` | finalizer | swallows the per-physics-frame `BreedingStation.CanUse` NRE (null `station.info` after a body dies mid-mount) with a throttled log | no — crash containment, transparent |
| `JiggleSkin.UpdateMesh` | finalizer | NRE guard (per-frame mesh update crash, v1.7.0) | VR body meshes jiggle too — this only CONTAINS exceptions; the original method still runs |
| `JiggleSkin.OnDisable` | finalizer | NRE guard (the "player model resets every restart" bug, v1.7.0) | no |
| `MailMachine.LocalUse` | prefix | sell-machine narration (body-gone legibility) | no |
| `Kobold.OnBellyContentsChanged` | postfix | belly-bulge meal term (visual) | maybe — a VR rig rendering belly size should read the same inflated size |
| `GenericReagentContainer.AddMixRPC` | postfix | reagent awareness (drink/spray events for agents) | no |
| `Grabber.LateUpdate` | prefix | AI-body carry machinery (ModGrab) | agent bodies only — check its guard if you patch Grabber |
| `Grabber.TryGrab` | prefix | grab gating (a digesting eater can't grab) | agent bodies only |
| `Grabber.TryDrop` | prefix | THE VORE TRIGGER: a release gesture while looking up starts a swallow (when vore is armed). If your rig maps "release held item" to TryDrop, a VR look-up + release = eating someone. Coordinate: either keep the trigger (it's the design) or arm/disarm via the mod's vore config | YES |

### Per-frame behaviors to know

- **Agent bodies** (LLM-driven): driven by our `Movement` every physics frame
  (rigidbody steering), Photon ownership re-asserted periodically (other mods
  can steal it), and the game's station-animation rotation writes are
  neutralized for them. Treat an agent body as a driven object — a VR rig
  attached to one is a VIEWER (that's the spectate case), never a driver.
- **Spectate watch (flat session)**: player body parked at the eater's belly
  per tick; input suppressed per tick; camera written per pre-cull. All three
  yield per the claim flags above except the parking (by design).
- **A swallowed body's renderers** are disabled once fully swallowed (the
  vanilla meshes don't scale cleanly); the parked player body is the
  exception set restored on release/hatch.

### Config keys relevant to a VR rig

| Key | Section | Meaning |
|---|---|---|
| `SwallowAgentEatPOV` | `[LittleTreats]` | built-in spectate POV (FirstPerson/ThirdPerson) — moot when you claim the camera, but it decides `SpectateHookEvent.Pov` |
| `CameraNearClip` | `[Senses]` | the built-in FP spectate near clip — irrelevant once you own the camera |
| `SwallowEnabled` / `SwallowAgentEat` / `SwallowPlayerVictims` | `[LittleTreats]` | the consent gates — spectate can only begin when these arm it; read them to know whether a watch can even happen |

BepInEx only writes a key on first launch, so a fresh config file may not
contain keys the player never touched — bind-and-read via your own
`Config.Bind` mirroring is not possible cross-plugin; use the claim flags and
feeds above instead of reading our config object.

### If you need more

The pattern is established: an event on this page + a claim flag + a polling
feed. Ask (Rose runs both projects) — e.g. exposing the digest-progress
percent, the eater's live `feel` state, or agent-body "who is possessed"
listing are all small additions on this end.
