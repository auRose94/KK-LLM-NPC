// Unit tests for the training-data recorder (pure DataRecord + Json).
// Run via run_tests.sh (suite "datarecord").
using System;
using System.Collections.Generic;
using System.IO;
using KKLLMNPC;

internal static class TestDataRecord
{
    private static int _failed;

    private static void Assert(bool cond, string what)
    {
        if (!cond)
        {
            _failed++;
            Console.WriteLine("FAIL " + what);
        }
    }

    private static Dictionary<string, object> LineAsDict(string line)
    {
        return Json.Parse(line) as Dictionary<string, object>;
    }

    private static int LineCount(string file)
    {
        return File.Exists(file) ? File.ReadAllLines(file).Length : 0;
    }

    private static string DataUri(byte[] bytes, string mime)
    {
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }

    private static Dictionary<string, object> ImageMsg(object url)
    {
        return new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = new object[]
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = "look" },
                new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = url } },
            },
        };
    }

    public static void Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "kkllmnpc-dr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        string file = Path.Combine(tmp, "turns.jsonl");

        // ---- disabled: nothing records, null path refused ----
        DataRecord.Configure(false, 10L * 1024 * 1024, null);
        Assert(!DataRecord.Enabled, "null path -> disabled");
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        Assert(DataRecord.Flush(1000), "flush on disabled");
        Assert(!File.Exists(file), "null path -> no file");

        DataRecord.Configure(false, 10L * 1024 * 1024, file);
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        DataRecord.Flush(1000);
        Assert(!File.Exists(file), "disabled -> no file");

        // ---- RecordableMessages (disabled: pure pass-through of binary parts) ----
        var tinyUri = "data:image/jpeg;base64,AAAA"; // decodes to 3 bytes — too small, even live
        var msgs = new object[]
        {
            new Dictionary<string, object> { ["role"] = "system", ["content"] = "SYS PROMPT" },
            new Dictionary<string, object> { ["role"] = "user", ["content"] = "hello" },
            ImageMsg(tinyUri),
        };
        var clean = DataRecord.RecordableMessages(msgs);
        Assert(clean != null && clean.Length == 3, "message count kept");
        Assert(DataRecord.HashOfString("SYS PROMPT").Length == 16, "hash is 16 hex chars");
        var sys = clean[0] as Dictionary<string, object>;
        object href = null;
        Assert(sys != null && sys.TryGetValue("sys_ref", out href) && (href as string) == DataRecord.HashOfString("SYS PROMPT"), "system -> sys_ref");
        // Disabled: binary parts pass through untouched (nothing would be recorded anyway).
        var asst = clean[2] as Dictionary<string, object>;
        object vc = null;
        Assert(asst != null && asst.TryGetValue("content", out vc), "assistant message kept");
        Assert(Json.Write(clean).Contains("base64"), "disabled -> binary parts pass through as-is");

        // ---- live recording: vocab once, turn envelope, salvage counter ----
        DataRecord.Configure(true, 0, file);
        Assert(DataRecord.Enabled && !DataRecord.OverCap, "enabled, no cap");
        Assert(DataRecord.MediaDir != null && DataRecord.MediaDir == Path.Combine(Path.GetDirectoryName(file), "kkllmnpc_turns_media"), "media dir is the JSONL's sibling");
        DataRecord.Turn(new Dictionary<string, object>
        {
            ["mode"] = "console",
            ["npc"] = "Ember",
            ["model"] = "m:1",
            ["msgs"] = DataRecord.RecordableMessages(msgs),
            ["resp"] = new Dictionary<string, object> { ["content"] = "ls\nwhoami" },
        });
        DataRecord.MarkSalvage();
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "console", ["npc"] = "Ember" });
        // A repeat of the same system prompt emits no second vocab record.
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "console", ["msgs"] = DataRecord.RecordableMessages(msgs) });
        Assert(DataRecord.Flush(5000), "flush drains");
        Assert(File.Exists(file), "file written");
        string[] lines = File.ReadAllLines(file);
        Assert(lines.Length == 4, "sys vocab + 3 turns (vocab deduped), got " + lines.Length);
        var v0 = LineAsDict(lines[0]);
        object vt = null;
        Assert(v0 != null && v0.TryGetValue("type", out vt) && (vt as string) == "sys", "first record is the sys vocab");
        object vtext = null;
        Assert(v0.TryGetValue("text", out vtext) && (vtext as string) == "SYS PROMPT", "vocab holds the full text once");
        var t1 = LineAsDict(lines[1]);
        object sv1 = null;
        Assert(t1 != null && !t1.TryGetValue("salvage", out sv1), "no salvage on a clean turn");
        object session = null, ts = null;
        Assert(t1.TryGetValue("session", out session) && (session as string) != null && ((string)session).Length == 10, "envelope session");
        Assert(t1.TryGetValue("ts", out ts) && Convert.ToDouble(ts) > 1500000000.0, "envelope ts");
        var t2 = LineAsDict(lines[2]);
        object sv2 = null;
        Assert(t2 != null && t2.TryGetValue("salvage", out sv2) && Convert.ToInt32(sv2) == 1, "salvage count carried on the next record");
        object trv = null;
        Assert(t1.TryGetValue("resp", out trv) && Json.Write(trv).Contains("whoami"), "response body recorded");
        var t3 = LineAsDict(lines[3]);
        object m3 = null;
        Assert(t3 != null && t3.TryGetValue("msgs", out m3) && Json.Write(m3).Contains("sys_ref"), "messages rewritten to sys_ref");
        // A too-small data URI degrades to omit (live), and emits no media file.
        string liveStr = Json.Write(m3);
        Assert(liveStr.Contains("\"omit\""), "tiny data URI -> omit part");
        Assert(!liveStr.Contains("base64"), "tiny data URI leaves no base64 in the JSONL");
        Assert(!Directory.Exists(DataRecord.MediaDir) || Directory.GetFiles(DataRecord.MediaDir).Length == 0, "tiny URI wrote no frame");

        // ---- tools vocab: recorded once per hash ----
        var tools = new List<object> { new Dictionary<string, object> { ["name"] = "ls" } };
        string th = DataRecord.HashOf(tools);
        Assert(th != null && th.Length == 16, "tools hash present");
        DataRecord.VocabOnce("tools", th, tools);
        DataRecord.VocabOnce("tools", th, tools); // duplicate ignored
        DataRecord.Flush(5000);
        Assert(LineCount(file) == 5, "tools vocab added exactly once, got " + LineCount(file));

        // ---- frame materialization: data-URI -> img_ref + one media file ----
        var frame1 = new byte[256];
        for (int i = 0; i < frame1.Length; i++) frame1[i] = (byte)(i * 7 + 3);
        var frameMsgs = new object[] { ImageMsg(DataUri(frame1, "image/jpeg")) };
        int n0 = LineCount(file);
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "console", ["npc"] = "Ember", ["msgs"] = DataRecord.RecordableMessages(frameMsgs) });
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "console", ["npc"] = "Ember", ["msgs"] = DataRecord.RecordableMessages(frameMsgs) }); // same frame again
        Assert(DataRecord.Flush(5000), "flush frame turns");
        string[] flines = File.ReadAllLines(file);
        Assert(flines.Length == n0 + 2, "two frame turns recorded, got " + flines.Length);
        string f0 = flines[n0], f1 = flines[n0 + 1];
        Assert(f0.Contains("img_ref") && f1.Contains("img_ref"), "img_ref in records");
        Assert(!f0.Contains("base64") && !f1.Contains("base64"), "no base64 in the JSONL");
        string[] media = Directory.GetFiles(DataRecord.MediaDir);
        Assert(media.Length == 1, "identical frames dedupe to one file, got " + media.Length);
        byte[] onDisk = File.ReadAllBytes(media[0]);
        Assert(onDisk.Length == frame1.Length, "frame file byte-length matches");
        bool same = true;
        for (int i = 0; i < onDisk.Length; i++) if (onDisk[i] != frame1[i]) { same = false; break; }
        Assert(same, "frame file content matches");
        var fd = LineAsDict(f0);
        object fm = null;
        Assert(fd != null && fd.TryGetValue("msgs", out fm), "msgs present");
        string fmJson = Json.Write(fm);
        int iref = fmJson.IndexOf("img_ref\":\"");
        Assert(iref > 0, "img_ref value present");
        string refName = fmJson.Substring(iref + "img_ref\":\"".Length).Split('"')[0];
        Assert(refName == Path.GetFileName(media[0]), "img_ref points at the written file name, got '" + refName + "'");
        Assert(refName.EndsWith(".jpg"), "jpeg mime -> .jpg file");

        // ---- mime variants + dedupe key includes extension ----
        var frame2 = new byte[300];
        for (int i = 0; i < frame2.Length; i++) frame2[i] = (byte)(i * 13 + 1);
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "act", ["npc"] = "Ember", ["msgs"] = DataRecord.RecordableMessages(new object[] { ImageMsg(DataUri(frame2, "image/png")) }) });
        Assert(DataRecord.Flush(5000), "flush png frame");
        media = Directory.GetFiles(DataRecord.MediaDir);
        bool hasJpg = false, hasPng = false;
        foreach (string f in media) { if (f.EndsWith(".jpg")) hasJpg = true; if (f.EndsWith(".png")) hasPng = true; }
        Assert(media.Length == 2 && hasJpg && hasPng, "png frame kept under its own extension, got " + media.Length);

        // ---- record-only frames: MaterializeDataUri + ConversationHasImage ----
        var textOnly = new object[]
        {
            new Dictionary<string, object> { ["role"] = "system", ["content"] = "SYS PROMPT" },
            new Dictionary<string, object> { ["role"] = "user", ["content"] = "hello" },
        };
        Assert(!DataRecord.ConversationHasImage(textOnly), "text-only conversation: no image");
        Assert(DataRecord.ConversationHasImage(frameMsgs), "frame conversation: image detected");
        Assert(!DataRecord.ConversationHasImage(null), "null conversation treated as text-only");
        string recOnly = DataRecord.MaterializeDataUri(DataUri(frame2, "image/jpeg")); // same bytes as the png, jpeg ext → its own file
        Assert(recOnly != null && recOnly.EndsWith(".jpg"), "record-only materialize returns the name");
        DataRecord.Flush(5000);
        Assert(Directory.GetFiles(DataRecord.MediaDir).Length == 3, "dedupe key is hash+ext (jpg twin of the png), got " + Directory.GetFiles(DataRecord.MediaDir).Length);
        Assert(DataRecord.MaterializeDataUri(DataUri(frame2, "image/jpeg")) != null, "rematerialize queued");
        DataRecord.Flush(5000);
        Assert(Directory.GetFiles(DataRecord.MediaDir).Length == 3, "same-uri rematerialize dedupes");
        Assert(DataRecord.MaterializeDataUri("http://example/x.jpg") == null, "remote url refused");
        Assert(DataRecord.MaterializeDataUri(null) == null, "null refused");

        // ---- omit fallback: undecodable data URI still stripped, no file ----
        var badMsgs = new object[] { ImageMsg("data:image/jpeg;base64,!!not-base64!!") };
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "act", ["npc"] = "Ember", ["msgs"] = DataRecord.RecordableMessages(badMsgs) });
        Assert(DataRecord.Flush(5000), "flush bad-uri turn");
        string lastNow = File.ReadAllLines(file)[LineCount(file) - 1];
        Assert(lastNow.Contains("\"omit\"") && !lastNow.Contains("base64") && !lastNow.Contains("not-base64"), "undecodable URI -> omit, nothing leaks");

        // ---- over-cap: refuses to record until reconfigure ----
        File.WriteAllText(file, new string('x', 100) + "\n");
        DataRecord.Configure(true, 8, file); // 8-byte cap, file already 101 bytes
        Assert(DataRecord.Enabled && DataRecord.OverCap, "cap exceeded at configure");
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        Assert(DataRecord.Flush(5000), "flush after cap");
        Assert(LineCount(file) == 1, "nothing recorded past the cap, got " + LineCount(file));
        int filesBefore = Directory.GetFiles(DataRecord.MediaDir).Length;
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x", ["msgs"] = DataRecord.RecordableMessages(frameMsgs) });
        Assert(DataRecord.Flush(5000), "flush frame under cap");
        Assert(Directory.GetFiles(DataRecord.MediaDir).Length == filesBefore, "no frames written past the cap");
        // Reconfigure with room resumes recording (and the offset re-syncs to disk).
        DataRecord.Configure(true, 0, file);
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        Assert(DataRecord.Flush(5000), "flush after resume");
        Assert(LineCount(file) == 2, "recording resumed after reconfigure, got " + LineCount(file));

        // ---- thread safety sanity: marks are thread-local ----
        var other = new List<int> { 0 };
        var th2 = new System.Threading.Thread(delegate()
        {
            DataRecord.MarkSalvage();
            DataRecord.MarkSalvage();
            other[0] = 1; // finished marking on the other thread
        });
        th2.IsBackground = true;
        th2.Start();
        while (other[0] == 0) System.Threading.Thread.Sleep(5);
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        DataRecord.Flush(5000);
        string lastLine = File.ReadAllLines(file)[LineCount(file) - 1];
        var last = LineAsDict(lastLine);
        object lsv = null;
        Assert(last != null && !last.TryGetValue("salvage", out lsv), "marks from another thread do not leak");
        // ...and this thread's own marks still land:
        DataRecord.MarkSalvage();
        DataRecord.Turn(new Dictionary<string, object> { ["mode"] = "x" });
        DataRecord.Flush(5000);
        var mine = LineAsDict(File.ReadAllLines(file)[LineCount(file) - 1]);
        object msv = null;
        Assert(mine != null && mine.TryGetValue("salvage", out msv) && Convert.ToInt32(msv) == 1, "own-thread mark lands");

        try { Directory.Delete(tmp, true); } catch (Exception) { }

        Console.WriteLine(_failed == 0 ? "test_datarecord: ALL PASS" : ("test_datarecord: " + _failed + " FAIL(S)"));
        if (_failed > 0) Environment.Exit(1);
    }
}