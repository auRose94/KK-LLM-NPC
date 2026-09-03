# KoboldKare base-game AI & body control — full map (reverse-engineered from Assembly-CSharp IL)

Everything below is from `KoboldKare_Data/Managed/Assembly-CSharp.dll` IL,
Unity Engine 2021.3.45, KoboldKare current build. Cross-checked against the game's
own player path. Read this before touching our controller code again.

## The components on a kobold body

| Component | Role |
|---|---|
| `Kobold` (root) | Genes, energy, stimulation, `body: Rigidbody`, `bellyContainer: GenericReagentContainer`, `penetratables: List<PenetrableSet>`, `activeDicks`. Drives the physics body. |
| `KoboldCharacterController` | Quake-style walk physics. Owns `inputDir`/`inputJump`/`inputWalking`/`SetInputCrouched` and reads them in its own `FixedUpdate` → calls `Accelerate(wishdir, speed, accel)` then `body.velocity = velocity` when `photonView.IsMine || !InRoom`. |
| `KoboldAIPossession` | AI wander controller — drives the same `KoboldCharacterController` via its own fixed update. (We `Destroy()` it on possess.) |
| `KoboldSeeker` | NavMeshAgent-following wander; drives the body through NavMesh paths. (We `Destroy()` too.) |
| `CharacterControllerAnimator` | The pose/rotation/state machine. Owns `eyeRot`, `facingRot`, `hipVector(+Velocity SmoothDamp)`, station coroutines, the LookAtHandler link. |
| `LookAtHandler` (separate class) | Actually rotates head + hips toward `SetLookPosition`/`SetWeight` driven by `eyeRot`. |

## HOW facing actually works (the thing that kept fighting us)

`CharacterControllerAnimator.Update()` runs every frame. Key logic:

```
facingRot = SignedAngle(body.forward, eyeRot-forward, Vector3.up)

if ((inputShouldFaceEye || inputActivate || inputGrabbing) && !inputWalking):
    facingRot = eyeRot.x       # body snaps to look direction

hipVector = SmoothDamp(hipVector, desiredHipVector, hipVectorVelocity, 0.05)
# (hipVector applied via LookAtHandler to the rig's hips; drives the visible turn)
```

Then `hipVector` is what the animator's `Update` applies to the rig through the
`LookAtHandler` — not the rigidbody. That's why it looked like the body rotated on its
own: our `MoveRotation` wrote the collider, and the *armature* was being told by
`facingRot→hipVector` a totally different source.

**The coroutines** the animator spawns:
- `AnimationRoutine` (on station entry) — lerps `body.rotation` from `<startRotation>` →
  the station's transform over blendDuration. Writes `Rigidbody.rotation` directly.
- `StopAnimationRoutine` (on station exit) — lerps `body.rotation` back to
  `<startRotation>` captured at entry. Writes `Rigidbody.rotation` directly.

Both write the body rotation outside the normal controller pathway. That's also the
mechanism behind "they rotate back after we walk": any station enter/exit (or animator
state change) fires these coroutines and they *resurrect* a stale rotation from the
saved `<startRotation>5__4`.

## The other gotchas

- `KoboldCharacterController.inputWalking = true` → *walking mode* (slow speed).
  `false` → run/normal. We set `inputWalking = !run` correctly.
- Controller's `Friction()` is only invoked when `grounded`. Air: no friction.
- Controller writes `body.velocity = velocity` only when `photonView.IsMine` or not in
  room — ownership *must* be held for our drive to stick, which is why we keep the
  `TransferOwnership` watchdog alive every 3s.
- `inputDir` magnitude 0 → controller calls `Accelerate(forward, 0, effectiveAccel)` —
  its own decelerate-along-own-forward. Combined with the animator's hip drag that's
  why the body pulled itself back toward whatever facing `eyeRot` last encoded.
- `PlayerPossession.Update` is the human player's path. It reads `OrbitCamera.
  GetPlayerIntendedRotation()` (= `Euler(-aim.y, aim.x, 0)`), drives the controller
  through it, and also feeds hip/eye. By possessing via the controller directly we
  bypass all of this — which is fine, but we must either fully imitate it or not
  imitate it at all. Half-imitating is what caused every "fight" we saw.

## What our plugin SHOULD do (correct integration points)

- Drive `KoboldCharacterController.inputDir/inputJump/inputWalking/SetInputCrouched`
  only — the controller handles all walk physics, turning-by-walk, step-up, collision.
- Never write `Rigidbody.rotation`/`transform.rotation` on the body ourselves.
- Drive look via `CharacterControllerAnimator.SetEyeRot(absoluteWorldYaw, -pitch)`.
  - While walking: `eyeRot.x = movement direction yaw` (so hips pull the body into the
    walk direction through the game's own system).
  - While idle: `eyeRot.x = body's current yaw` (so hips have nothing to chase).
  - Never let eyeRot drift from the body yaw *while walk just ended* — that's the drift.
- To prevent the *station coroutines* from reapplying stale rotations mid-our-control,
  patch `AnimationRoutine.MoveNext` / `StopAnimationRoutine.MoveNext` to rewrite
  `<startRotation>5__4` (on the coroutine state box) to the body's *current* rotation
  when the body is claimed by us — already in `Patches.cs`.
- `inputShouldFaceEye=true` would make the body constantly chase eyeRot — set it only
  when we *want* the body to follow the gaze (e.g. station entry/exit) and false
  otherwise. (It's public.)

## What we do now (post-fix, current code)

- No manual rotation writes anywhere in Movement.cs.
- LookAtHandler drives the visible head + hips via `eyeRot` as above.
- Station animations still mount, and their coroutine body-writes no longer snap-back
  because of the Harmony patch.
- All other body state (reagents, penetration listeners, equipment, in-station detection)
  flows through subscriptions, not writes.

## If you go further

- To *fully* own the body orientation (e.g. for a NavMesh pathing agent later), the right
  move isn't more rotation writes — it's a small patch on `CharacterControllerAnimator.
  Update` that replaces the `facingRot` computation with our target yaw (single source
  of truth), and let everything downstream (hips → rig) consume it normally. Then the
  entire game pipeline (including stations) sees a coherent facing.

That's the full reverse-engineering. Everything we keep fighting is a subsystem we didn't
know was running; the current patch set (Patches.cs) addresses all the ones we verified.
