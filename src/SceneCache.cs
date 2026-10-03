// SceneCache — shared TTL cache over FindObjectsOfType. Scene-wide scans used to
// be repeated per module, per tool call and even per NPC (Farming/Cooking scanned
// plants/blenders several times per perception build, each NPC's Body scanned
// kobolds independently). One entry per type, refreshed at most every TTL
// seconds — the same pattern CarrySense's mail-machine cache pioneered.
//
// MAIN THREAD ONLY: FindObjectsOfType and Time reads. Perception builds and
// tool handlers already run inside RunOnMainThread marshals; Reconcile/pooling
// paths run in plugin Update. Call SceneCache.Clear() on world reload.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class SceneCache
    {
        private sealed class Entry
        {
            public UnityEngine.Object[] Items;
            public float At;
        }

        private static readonly Dictionary<Type, Entry> _cache = new Dictionary<Type, Entry>();

        // Returns the cached scan for T (maybe stale up to ttlSeconds), refreshing
        // when expired. Do NOT mutate the returned array — it is shared.
        public static T[] Find<T>(float ttlSeconds) where T : UnityEngine.Object
        {
            lock (_cache)
            {
                Entry e;
                if (_cache.TryGetValue(typeof(T), out e) && e.Items != null
                    && Time.unscaledTime - e.At <= ttlSeconds)
                    return (T[])e.Items;
                var fresh = UnityEngine.Object.FindObjectsOfType<T>();
                _cache[typeof(T)] = new Entry { Items = fresh, At = Time.unscaledTime };
                return fresh;
            }
        }

        // World reload / scene change: old instances die, so a warmed cache could
        // hand back a destroyed object array up to the TTL later. Clear on every
        // world transition (Main already knows where WorldMap.Invalidate lives).
        public static void Clear()
        {
            lock (_cache) _cache.Clear();
        }
    }
}