// Data-dump module (vision-cortex training data).
//
// Writes per-frame training data for the future vision model:
//   <dumpDir>/<frame_id>/frame.png   1024² 4-quadrant composite (L/R eye RGB + L/R eye depth)
//   <dumpDir>/<frame_id>/meta.json   engine ground truth (camera state + projected entity labels)
//
// Phase 1 = COLOR only: TL/TR = eye RGB, BL/BR = a flat gray depth placeholder
// (depth quadrants arrive in Phase 2). meta.json carries the camera state and the
// projected bounding boxes of every nearby entity — the labeled data.
//
// Registered through ModuleRegistry like every other module: a static Register()
// wires a physics-tick hook (main thread — Camera.Render/ReadPixels are main-thread
// only). Config is bound lazily so this file never touches the big config block.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KKLLMNPC
{
    internal static class DataDumpModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            ModuleRegistry.PhysicsTick((n, dt) => n.DumpDataTick(dt));
        }
    }

    internal partial class NPCInstance
    {
        // ---- config (bound lazily on first tick) ----
        private bool _dumpCfgBound;
        private ConfigEntry<bool> _cfgDump;
        private ConfigEntry<int> _cfgDumpEvery;
        private ConfigEntry<int> _cfgDumpSize;
        private ConfigEntry<string> _cfgDumpDir;

        // ---- per-frame capture state ----
        private Texture2D _dumpTexL;
        private Texture2D _dumpTexR;
        private Texture2D _dumpComposite;
        private RenderTexture _dumpRT;
        private int _dumpLastTick = -999;
        private int _dumpFrameCount;
        private Vector3 _dumpPrevPos;
        private float _dumpPrevTime;
        private bool _dumpHavePrev;

        private void EnsureDumpConfig()
        {
            if (_dumpCfgBound) return;
            var cfg = Plugin.Config;
            _cfgDump = cfg.Bind("DataDump", "Enabled", false,
                "Write vision-cortex training frames (frame.png + meta.json) for the future vision model. OFF by default.");
            _cfgDumpEvery = cfg.Bind("DataDump", "EveryTicks", 60,
                "Capture a frame every N physics ticks (default 60 ≈ 1 Hz at 60 Hz).");
            _cfgDumpSize = cfg.Bind("DataDump", "EyeSize", 512,
                "Per-eye render size in px; the composite is 2× this (512 → 1024²).");
            _cfgDumpDir = cfg.Bind("DataDump", "Dir", "KKLLMNPC_vision_data",
                "Output directory (relative to the game root; absolute paths also accepted).");
            _dumpCfgBound = true;
        }

        // Physics tick (main thread). Throttled by _cfgDumpEvery; renders on demand
        // so there is zero GPU cost between samples.
        internal void DumpDataTick(float dt)
        {
            if (_dumpCfgBound && _cfgDump != null && !_cfgDump.Value) return;
            EnsureDumpConfig();
            if (_cfgDump == null || !_cfgDump.Value) return;
            if (!IsAlive(_kobold) || !IsAlive(_cam)) return;

            int every = _cfgDumpEvery != null && _cfgDumpEvery.Value > 0 ? _cfgDumpEvery.Value : 60;
            if (_dumpLastTick < 0 || (_tick - _dumpLastTick) < every) return;
            _dumpLastTick = _tick;

            try { DumpData(); }
            catch (Exception e) { Logger.LogWarning("data dump: " + e.Message); }
        }

        // One capture: render both eyes, build the composite, gather entity ground
        // truth, write frame.png + meta.json under the dump dir.
        private void DumpData()
        {
            int eye = _cfgDumpSize != null && _cfgDumpSize.Value > 0 ? _cfgDumpSize.Value : 512;
            int comp = eye * 2;
            bool stereo = _cfgStereo != null && _cfgStereo.Value && _camR != null && _rtR != null && RtCreated(_rtR);

            // ---- render the LEFT eye (always) ----
            if (!RenderEyeToTex(_cam, eye, ref _dumpTexL)) return;

            // ---- render the RIGHT eye (stereo only) ----
            if (stereo) RenderEyeToTex(_camR, eye, ref _dumpTexR);

            // ---- composite into one 1024² texture ----
            if (_dumpComposite == null || _dumpComposite.width != comp || _dumpComposite.height != comp)
            {
                if (_dumpComposite != null) Destroy(_dumpComposite);
                _dumpComposite = new Texture2D(comp, comp, TextureFormat.RGB24, false);
            }
            var outPx = _dumpComposite.GetPixels32();
            var tl = _dumpTexL.GetPixels32();
            var tr = stereo ? _dumpTexR.GetPixels32() : tl;
            for (int y = 0; y < eye; y++)
            {
                // top row: L | R
                Array.Copy(tl, y * eye, outPx, y * comp, eye);
                Array.Copy(tr, y * eye, outPx, y * comp + eye, eye);
                // bottom row: flat gray (depth placeholder for Phase 2)
                for (int x = 0; x < eye; x++)
                {
                    outPx[(eye + y) * comp + x] = new Color32(128, 128, 128, 255);
                    outPx[(eye + y) * comp + eye + x] = new Color32(128, 128, 128, 255);
                }
            }
            _dumpComposite.SetPixels32(outPx);
            _dumpComposite.Apply();

            // ---- frame id + write the image ----
            string frameId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + _dumpFrameCount.ToString("D4", CultureInfo.InvariantCulture);
            _dumpFrameCount++;
            string dir = ResolveDumpDir();
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            string framePath = Path.Combine(dir, frameId);
            byte[] jpg = ImageConversion.EncodeToJPG(_dumpComposite, 90);
            File.WriteAllBytes(framePath + ".png", jpg);

            // ---- meta.json ----
            var meta = BuildDumpMeta(eye, stereo);
            File.WriteAllText(framePath + ".meta.json", Json.Write(meta));

            Logger.LogInfo($"[data dump] frame {frameId} — {jpg.Length / 1024}KB, entities={meta["entities"] is List<object> l ? l.Count : 0}");
        }

        // Render one eye camera to a pooled Texture2D (reusing the read path from the
        // vision pass). Renders once; returns false if the camera couldn't render.
        private bool RenderEyeToTex(Camera cam, int size, ref Texture2D tex)
        {
            if (cam == null || !IsAlive(cam)) return false;
            // Point the camera at the agent's current look so the frame matches what
            // the NPC is seeing, not a stale transform. Save + restore the camera's
            // own transform/RT (the mod owns them for the live vision pass).
            Quaternion savedRot = cam.transform.rotation;
            RenderTexture savedRT = cam.targetTexture;
            cam.transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
            if (_dumpRT == null || _dumpRT.width != size || _dumpRT.height != size)
            {
                if (_dumpRT != null) { _dumpRT.Release(); Destroy(_dumpRT); }
                _dumpRT = new RenderTexture(size, size, 0);
                _dumpRT.Create();
            }
            try
            {
                cam.targetTexture = _dumpRT;
                cam.Render();
                if (tex == null || tex.width != size || tex.height != size)
                {
                    if (tex != null) Destroy(tex);
                    tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                }
                var prev = RenderTexture.active;
                RenderTexture.active = _dumpRT;
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                tex.Apply();
                RenderTexture.active = prev;
            }
            catch (Exception e) { Logger.LogWarning("data dump render: " + e.Message); return false; }
            finally
            {
                cam.targetTexture = savedRT;
                cam.transform.rotation = savedRot;
            }
            return true;
        }

        // The output directory: absolute path if configured, else relative to the
        // game root (so it lands beside the plugin, not in some random CWD).
        private string ResolveDumpDir()
        {
            string raw = _cfgDumpDir != null ? _cfgDumpDir.Value : null;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            if (Path.IsPathRooted(raw)) return raw;
            try
            {
                // game root = parent of KoboldKare_Data
                string data = Application.dataPath; // .../KoboldKare/KoboldKare_Data
                string gameRoot = Path.GetDirectoryName(data);
                return Path.Combine(gameRoot, raw);
            }
            catch (Exception) { return null; }
        }

        // Build the meta.json object: camera state + projected entity labels.
        private Dictionary<string, object> BuildDumpMeta(int eye, bool stereo)
        {
            var meta = new Dictionary<string, object>
            {
                ["frame_id"] = _dumpFrameCount.ToString(CultureInfo.InvariantCulture),
                ["source"] = "agent",
                ["stereo"] = stereo,
                ["agent"] = AgentBlock(),
                ["camera"] = CameraBlock(stereo),
                ["entities"] = ProjectedEntities(eye),
                ["scene"] = SceneBlock(),
                ["depth"] = new Dictionary<string, object>
                {
                    ["available"] = false, // Phase 1 = flat gray placeholder
                    ["near"] = _cfgCamNearClip != null ? _cfgCamNearClip.Value : 0.1f,
                    ["far"] = _cfgCamFarClip != null ? _cfgCamFarClip.Value : 80f,
                },
            };
            return meta;
        }

        private Dictionary<string, object> AgentBlock()
        {
            string species = "kobold";
            string name = MyName();
            try { species = CleanName(_kobold.name); if (string.IsNullOrEmpty(species)) species = "kobold"; } catch (Exception) { }
            return new Dictionary<string, object>
            {
                ["id"] = _currentKoboldId,
                ["name"] = name,
                ["class"] = species,
                ["is_player"] = IsPlayerKobold(_kobold),
                ["claimed_by_llm"] = LLMNPCPlugin.IsClaimedByAnyLLM(_kobold.GetInstanceID()),
            };
        }

        private Dictionary<string, object> CameraBlock(bool stereo)
        {
            var cam = new Dictionary<string, object>
            {
                ["pos"] = Vec(_cam.transform.position),
                ["forward"] = Vec(_cam.transform.forward),
                ["up"] = Vec(_cam.transform.up),
                ["fov"] = _cam.fieldOfView,
                ["near"] = _cfgCamNearClip != null ? _cfgCamNearClip.Value : 0.1f,
                ["far"] = _cfgCamFarClip != null ? _cfgCamFarClip.Value : 80f,
                ["yaw"] = _yawDeg,
                ["pitch"] = _pitchDeg,
                ["vel"] = Vel(),
            };
            var block = new Dictionary<string, object> { ["L"] = cam };
            if (stereo)
            {
                var rc = new Dictionary<string, object>(cam) { ["pos"] = Vec(_camR.transform.position) };
                block["R"] = rc;
                block["ipd"] = _cfgStereoIPD != null ? _cfgStereoIPD.Value : 0.063f;
            }
            return block;
        }

        // Project every nearby entity into the eye's viewport, then map to the
        // quadrant (x,y = top-left of bbox in the quadrant; w,h = size). This is the
        // labeled data: the model learns to read the entity from the image + this box.
        private List<object> ProjectedEntities(int eye)
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            try
            {
                var nearby = DescribeNearby(); // world pos + bounds + facing + dev_name + class
                if (nearby == null) return list;
                foreach (var raw in nearby)
                {
                    var d = raw as Dictionary<string, object>;
                    if (d == null) continue;
                    if (!TryVec3(d, "x", "y", "z", out Vector3 world)) continue;

                    // Project into BOTH eyes (the entity appears in both quadrants).
                    var bboxL = ProjectBbox(_cam, world, d, eye);
                    var bboxR = (stereoOn()) ? ProjectBbox(_camR, world, d, eye) : null;
                    if (bboxL == null && bboxR == null) continue;

                    list.Add(new Dictionary<string, object>
                    {
                        ["natural_name"] = d.ContainsKey("n") ? d["n"] : null,
                        ["natural_class"] = ClassOf(d),
                        ["category"] = CategoryOf(d),
                        ["dev_name"] = d.ContainsKey("dev_name") ? d["dev_name"] : null,
                        ["instance_id"] = d.ContainsKey("instance_id") ? d["instance_id"] : 0,
                        ["world_pos"] = Vec(world),
                        ["distance"] = d.ContainsKey("d") ? d["d"] : null,
                        ["bbox_L"] = bboxL,
                        ["bbox_R"] = bboxR,
                    });
                }
            }
            catch (Exception e) { Logger.LogWarning("data dump project: " + e.Message); }
            return list;
        }

        // Project a world-space box into a quadrant's normalized (0..1) bbox.
        // Returns null if the entity is behind the camera or off-screen entirely.
        private object ProjectBbox(Camera cam, Vector3 world, Dictionary<string, object> d, int eye)
        {
            if (cam == null || !IsAlive(cam)) return null;
            // sample the 8 corners of the entity's AABB
            Vector3 s = new Vector3(
                d.ContainsKey("w") ? ToF(d["w"]) * 0.5f : 0.5f,
                d.ContainsKey("ht") ? ToF(d["ht"]) * 0.5f : 0.5f,
                d.ContainsKey("l") ? ToF(d["l"]) * 0.5f : 0.5f);
            Vector3[] corners =
            {
                world + new Vector3(-s.x, -s.y, -s.z), world + new Vector3(s.x, -s.y, -s.z),
                world + new Vector3(-s.x, s.y, -s.z),  world + new Vector3(s.x, s.y, -s.z),
                world + new Vector3(-s.x, -s.y, s.z),  world + new Vector3(s.x, -s.y, s.z),
                world + new Vector3(-s.x, s.y, s.z),   world + new Vector3(s.x, s.y, s.z),
            };
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool anyInFront = false;
            foreach (var c in corners)
            {
                Vector3 vp = cam.WorldToViewportPoint(c);
                if (vp.z <= 0f) continue; // behind camera
                anyInFront = true;
                if (vp.x < minX) minX = vp.x;
                if (vp.x > maxX) maxX = vp.x;
                if (vp.y < minY) minY = vp.y;
                if (vp.y > maxY) maxY = vp.y;
            }
            if (!anyInFront) return null;
            // The eye occupies one quadrant; the viewport (0..1) maps to that quadrant.
            // Left eye = left half (x 0..0.5), right eye = right half (x 0.5..1).
            float xOff = cam == _cam ? 0f : 0.5f;
            float qx = xOff + minX * 0.5f, qy = minY * 0.5f;
            float qw = (maxX - minX) * 0.5f, qh = (maxY - minY) * 0.5f;
            return new Dictionary<string, object>
            {
                ["x"] = F(qx), ["y"] = F(qy), ["w"] = F(qw), ["h"] = F(qh),
            };
        }

        private bool stereoOn() => _cfgStereo != null && _cfgStereo.Value && _camR != null;

        // The entity's human class (n/k) and a coarse category, mirroring DescribeNearby.
        private static string ClassOf(Dictionary<string, object> d)
        {
            try { return d.ContainsKey("n") ? (string)d["n"] : null; } catch (Exception) { return null; }
        }
        private static string CategoryOf(Dictionary<string, object> d)
        {
            try
            {
                string k = d.ContainsKey("k") ? (string)d["k"] : null;
                if (k == null) return "usable";
                if (k.Contains("player")) return "player";
                if (k.Contains("agent")) return "kobold-agent";
                if (k.Contains("kobold")) return "kobold";
                return "usable";
            }
            catch (Exception) { return "usable"; }
        }

        private Dictionary<string, object> SceneBlock()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name; } catch (Exception) { }
            string room = "";
            try { if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null) room = PhotonNetwork.CurrentRoom.Name; } catch (Exception) { }
            return new Dictionary<string, object> { ["scene"] = scene, ["room"] = room };
        }

        // ---- small helpers ----
        private List<float> Vel()
        {
            // velocity from the character controller's rigidbody if present, else
            // derived from the body's world-position delta across ticks.
            try
            {
                var rb = _controller != null ? _controller.GetComponent<Rigidbody>() : null;
                if (rb != null) return Vec(rb.velocity);
            }
            catch (Exception) { }
            if (!_dumpHavePrev) { _dumpPrevPos = _kobold.transform.position; _dumpPrevTime = Time.unscaledTime; _dumpHavePrev = true; return new List<float> { 0f, 0f, 0f }; }
            float dt = Time.unscaledTime - _dumpPrevTime;
            if (dt <= 0f) return new List<float> { 0f, 0f, 0f };
            Vector3 v = (_kobold.transform.position - _dumpPrevPos) / dt;
            _dumpPrevPos = _kobold.transform.position;
            _dumpPrevTime = Time.unscaledTime;
            return Vec(v);
        }

        private bool TryVec3(Dictionary<string, object> d, string kx, string ky, string kz, out Vector3 v)
        {
            v = Vector3.zero;
            try
            {
                float x = ToF(d[kx]), y = ToF(d[ky]), z = ToF(d[kz]);
                v = new Vector3(x, y, z);
                return true;
            }
            catch (Exception) { return false; }
        }
        private static float ToF(object o) { try { return Convert.ToSingle(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0f; } }
        private List<float> Vec(Vector3 v) => new List<float> { v.x, v.y, v.z };
    }
}
