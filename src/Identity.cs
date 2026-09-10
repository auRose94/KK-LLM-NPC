// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Identity module: per-NPC identity block (orientation, trans status, persona),
// rename tool, and mailbox/ATM awareness perception.
//
// Registers via ModuleRegistry in the static constructor — no shared-file edits needed.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KKLLMNPC
{
    // ------------------------------------------------------------------
    // Identity module: static constructor registers tool + perception hooks.
    // The rename tool is registered without schema (prompt_extras/identity.txt
    // documents the parameters). Schema registration has an overload
    // resolution conflict in mcs between params string[] and Dictionary.
    // ------------------------------------------------------------------
    internal static class IdentityModule
    {
        static IdentityModule()
        {
            ModuleRegistry.Tool("rename", (n, p) => n.ToolRename(p));
            ModuleRegistry.Perception((n, dict) =>
            {
                dict["identity"] = n.IdentityBlock();
                dict["mail_atm"] = n.MailAtmPerception();
            });
        }
    }

    internal partial class NPCInstance
    {
        // Identity block — returned by perception as a JSON-serializable object.
        // The model uses this to understand WHO it is and how it relates to others.
        // ------------------------------------------------------------------
        internal object IdentityBlock()
        {
            // Build identity block from the NPC's persona prompt if available.
            string orient = null, trans = null, persona = null;
            if (_persona != null)
            {
                string lower = _persona.ToLowerInvariant();
                if (string.IsNullOrEmpty(orient))
                {
                    if (lower.Contains("gay")) orient = "gay";
                    else if (lower.Contains("bi")) orient = "bi";
                    else if (lower.Contains("pan")) orient = "pan";
                    else if (lower.Contains("straight")) orient = "straight";
                }
                if (string.IsNullOrEmpty(trans))
                {
                    if (lower.Contains("trans"))
                    {
                        if (lower.Contains("mf") || lower.Contains("female to male")) trans = "trans_mf";
                        else if (lower.Contains("fm") || lower.Contains("male to female")) trans = "trans_fm";
                        else trans = "trans";
                    }
                    else if (lower.Contains("nonbinary") || lower.Contains("non-binary")) trans = "nonbinary";
                    else trans = "cis";
                }
                if (string.IsNullOrEmpty(persona))
                {
                    if (lower.Contains("feminine")) persona = "feminine";
                    else if (lower.Contains("masculine")) persona = "masculine";
                    else if (lower.Contains("androgynous")) persona = "androgynous";
                    else if (lower.Contains("flamboyant")) persona = "flamboyant";
                }
            }

            return new
            {
                orientation = string.IsNullOrEmpty(orient) ? null : orient,
                trans = string.IsNullOrEmpty(trans) ? null : trans,
                persona = string.IsNullOrEmpty(persona) ? null : persona,
                my_name = MyName(),
                gender = InferGender(),
                pronouns = InferPronouns(),
            };
        }

        // ------------------------------------------------------------------
        // Rename tool: change the NPC's display name.
        // Validates uniqueness via NameRegistry, updates _npcName, broadcasts
        // a chat line announcing the change, and updates the identity bot's
        // nickname if one is active.
        // ------------------------------------------------------------------
        internal object ToolRename(JsonObj p)
        {
            string newName = p.S("name", "").Trim();
            if (string.IsNullOrEmpty(newName))
                return new { ok = false, reason = "empty_name", hint = "Provide a non-empty name." };

            // Validate: name must be printable ASCII, no control chars.
            foreach (char c in newName)
            {
                if (c < 32 || c > 126)
                    return new { ok = false, reason = "invalid_name", hint = "Name must contain only printable ASCII characters (a-z, A-Z, 0-9, spaces, punctuation)." };
            }

            // Check uniqueness via NameRegistry.
            if (NameRegistry.IsTaken(newName))
                return new { ok = false, reason = "name_taken", hint = "That name is already taken. Try a different one." };

            // Validate length.
            if (newName.Length > 30)
                return new { ok = false, reason = "name_too_long", hint = "Name must be 30 characters or fewer." };

            // Reserve the name.
            if (!NameRegistry.TryReserve(newName))
                return new { ok = false, reason = "name_taken", hint = "Could not reserve name — it may have been taken between checks." };

            // Update the NPC's display name.
            string oldName = MyName();
            _npcName = newName;

            // If the identity bot is active, update its nickname so future chat
            // renders under the new name. Must be on the main thread since the
            // identity bot touches Photon objects.
            if (_identityBot != null)
            {
                string room = _identityBot.RoomName;
                RunOnMainThreadAsync(() =>
                {
                    try { _identityBot.EnsureStarted(room, newName); }
                    catch (Exception e) { Logger.LogWarning("rename: identity bot update failed: " + e.Message); }
                });
            }

            // Update the identity bot's NickName on the Photon client directly
            // so it takes effect immediately.
            if (_identityBot != null && _identityBot.InRoom)
            {
                RunOnMainThreadAsync(() =>
                {
                    try
                    {
                        var field = typeof(NpcIdentityBot).GetField("_nickName", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null) field.SetValue(_identityBot, newName);
                    }
                    catch (Exception) { }
                });
            }

            // Broadcast the name change via chat.
            string broadcastMsg = "I'm going by " + newName + " now.";
            RunOnMainThreadAsync(() =>
            {
                try { ToolSay(new TextArgs(broadcastMsg)); }
                catch (Exception e) { Logger.LogWarning("rename: broadcast failed: " + e.Message); }
            });

            Logger.LogInfo("[" + oldName + "] renamed to " + newName);
            return new { ok = true, old_name = oldName, new_name = newName, broadcast = broadcastMsg };
        }

        // ------------------------------------------------------------------
        // Mailbox/ATM perception: discover nearby MailboxUsable and MailMachine
        // objects and report their state. Mail = selling/trading kobolds;
        // the model should check mail status before assuming a kobold is "gone".
        // ------------------------------------------------------------------
        internal object MailAtmPerception()
        {
            if (!IsAlive(_kobold)) return null;

            var list = new List<object>();
            try
            {
                var colliders = Physics.OverlapSphere(_kobold.transform.position, 14f, ~0, QueryTriggerInteraction.Collide);
                if (colliders == null) return null;

                var seen = new HashSet<int>();

                foreach (var c in colliders)
                {
                    if (c == null) continue;

                    // Try MailboxUsable first.
                    MailboxUsable mailbox = null;
                    try { mailbox = c.GetComponent<MailboxUsable>(); } catch (Exception) { }
                    if (mailbox != null)
                    {
                        int id = c.GetInstanceID();
                        if (!seen.Add(id)) continue;

                        float dist = (c.transform.position - _kobold.transform.position).magnitude;
                        string dir = RelBearing(c.transform.position);
                        string dirDeg = F(RelBearingDeg(c.transform.position));

                        // Check for mail waiting via the mailWaiting field.
                        string mailState = "no mail";
                        try
                        {
                            var mailField = typeof(MailboxUsable).GetField("mailWaiting", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mailField != null)
                            {
                                var val = mailField.GetValue(mailbox);
                                mailState = val != null ? "mail waiting" : "no mail";
                            }
                        }
                        catch (Exception) { /* leave default */ }

                        list.Add(new
                        {
                            type = "mailbox",
                            name = CleanName(c.gameObject.name),
                            d = F(dist),
                            dir = dir,
                            dir_deg = dirDeg,
                            state = mailState,
                        });
                        continue;
                    }

                    // Try MailMachine.
                    MailMachine machine = null;
                    try { machine = c.GetComponent<MailMachine>(); } catch (Exception) { }
                    if (machine != null)
                    {
                        int id = c.GetInstanceID();
                        if (!seen.Add(id)) continue;

                        float dist = (c.transform.position - _kobold.transform.position).magnitude;
                        string dir = RelBearing(c.transform.position);
                        string dirDeg = F(RelBearingDeg(c.transform.position));

                        // Check stations count (how many kobolds are queued/sold).
                        string stationInfo = "unknown";
                        try
                        {
                            var stationsField = typeof(MailMachine).GetField("stations", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (stationsField != null)
                            {
                                var val = stationsField.GetValue(machine);
                                if (val is System.Collections.IEnumerable)
                                {
                                    int count = 0;
                                    foreach (var _ in (System.Collections.IEnumerable)val) count++;
                                    stationInfo = count + " kobold(s) in queue";
                                }
                                else
                                {
                                    stationInfo = val != null ? "active" : "inactive";
                                }
                            }
                        }
                        catch (Exception) { /* leave default */ }

                        list.Add(new
                        {
                            type = "mail_machine",
                            name = CleanName(c.gameObject.name),
                            d = F(dist),
                            dir = dir,
                            dir_deg = dirDeg,
                            state = stationInfo,
                        });
                        continue;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug("mail_atm perception: " + e.Message);
                return null;
            }

            return list.Count > 0 ? list : null;
        }
    }
}
