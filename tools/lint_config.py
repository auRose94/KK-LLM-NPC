#!/usr/bin/env python3
"""lint_config.py — validate a BepInEx .cfg against docs/CONFIG.md.

The mod's real support burden is config mistakes the docs already answer:
values outside documented ranges, or keys the docs no longer mention (stale
docs are a bug too — this catches both directions). Pure stdlib, no game
install needed.

Usage:
  tools/lint_config.py                          # lint the game's com.kk.llmnpc.cfg
                                                #   (path from KOBOLDKARE_DIR, else the
                                                #    usual Steam library fallbacks)
  tools/lint_config.py BepInEx/config/x.cfg     # lint a specific file
  tools/lint_config.py --config-md PATH         # override docs/CONFIG.md location
  tools/lint_config.py --strict                 # unknown keys are errors, not warnings
  tools/lint_config.py --selftest               # run the parser fixtures (CI gate)

Exit 0 = clean (warnings allowed unless --strict); exit 1 = problems found.
Range text the parser doesn't understand is skipped, never guessed at — a
linter that false-positives gets ignored faster than no linter at all.
"""
import os
import re
import sys

# --------------------------------------------------------------------------
# CONFIG.md parsing
# --------------------------------------------------------------------------

# A table row: | `Key` | default | range | purpose |
ROW_RE = re.compile(r"^\|\s*`([A-Za-z0-9_]+)`\s*\|([^|]*)\|([^|]*)\|")
HEADING_RE = re.compile(r"^##\s*\[([A-Za-z0-9_]+)\]")
FENCE_SECTION_RE = re.compile(r"^\[([A-Za-z0-9_]+)\]\s*$")


def parse_config_md(text):
    """Walk CONFIG.md and yield {section: {key: (default, range)}}.

    Section comes from the last ``## [Name]`` heading; ini code-fences also
    set it (the [Ollama] table hangs under a plain ``###`` subheading but its
    ini block names the section).
    """
    sections = {}
    current = None
    in_fence = False
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fence = not in_fence
            continue
        if not in_fence:
            m = HEADING_RE.match(line)
            if m:
                current = m.group(1)
                sections.setdefault(current, {})
                continue
        else:
            m = FENCE_SECTION_RE.match(stripped)
            if m:
                current = m.group(1)
                sections.setdefault(current, {})
                continue
        m = ROW_RE.match(line)
        if m and current and m.group(1) != "Key":  # skip table headers
            key, default, rng = m.group(1), m.group(2).strip().strip("`*"), m.group(3).strip()
            sections[current][key] = (default, rng)
    return sections


# --------------------------------------------------------------------------
# range parsing — deliberately conservative
# --------------------------------------------------------------------------

def parse_range(rng):
    """Return a checker for a Range cell, or None when unrecognized.

    Recognized: 'bool'; '—'/'-'/'(empty)' (no constraint); 'a-b', 'a–b',
    'a..b' (inclusive numeric); '≥ n' / '>= n' / '≤ n' / '<= n'; '> n' / '< n';
    comma lists '0, 1, 2' (membership). En-dash included — the docs use it.
    """
    r = rng.strip().strip("`*")
    if r in ("", "—", "-", "—", "bool"):
        return ("bool",) if r == "bool" else None
    m = re.fullmatch(r"\s*([0-9]+(?:\.[0-9]+)?)\s*[–\-.]{1,2}\s*([0-9]+(?:\.[0-9]+)?)\s*", r)
    if m:
        return ("range", float(m.group(1)), float(m.group(2)))
    m = re.fullmatch(r"\s*(?:≥|>=)\s*([0-9]+(?:\.[0-9]+)?)\s*", r)
    if m:
        return ("min", float(m.group(1)))
    m = re.fullmatch(r"\s*(?:≤|<=)\s*([0-9]+(?:\.[0-9]+)?)\s*", r)
    if m:
        return ("max", float(m.group(1)))
    m = re.fullmatch(r"\s*([<>])\s*([0-9]+(?:\.[0-9]+)?)\s*", r)
    if m:
        return ("gt" if m.group(1) == ">" else "lt", float(m.group(2)))
    if re.fullmatch(r"\s*[0-9]+(?:\.[0-9]+)?(\s*,\s*[0-9]+(?:\.[0-9]+)?)+\s*", r):
        vals = [float(x.strip()) for x in r.split(",")]
        return ("oneof", vals)
    return None


def check_value(checker, raw):
    """Return None when ok, else a complaint string. Unparseable value skips."""
    v = raw.strip().strip('"')
    if checker[0] == "bool":
        if v.lower() in ("true", "false"):
            return None
        return "expected true/false, got '%s'" % raw.strip()
    try:
        num = float(v)
    except ValueError:
        return None  # string-ish value; ranges don't apply
    kind = checker[0]
    if kind == "range" and not (checker[1] <= num <= checker[2]):
        return "%s outside documented range %s..%s" % (num, checker[1], checker[2])
    if kind == "min" and num < checker[1]:
        return "%s below documented minimum %s" % (num, checker[1])
    if kind == "max" and num > checker[1]:
        return "%s above documented maximum %s" % (num, checker[1])
    if kind == "gt" and num <= checker[1]:
        return "%s not above %s" % (num, checker[1])
    if kind == "lt" and num >= checker[1]:
        return "%s not below %s" % (num, checker[1])
    if kind == "oneof" and num not in checker[1]:
        return "%s not one of %s" % (num, ", ".join(str(x) for x in checker[1]))
    return None


# --------------------------------------------------------------------------
# BepInEx .cfg parsing
# --------------------------------------------------------------------------

SECTION_RE = re.compile(r"^\[([A-Za-z0-9_]+)\]\s*$")
KEYVAL_RE = re.compile(r"^([A-Za-z0-9_]+)\s*=\s*(.*)$")


def parse_cfg(text):
    """Yield (section, key, value) from a BepInEx .cfg (comments ignored)."""
    section = None
    for line in text.splitlines():
        line = line.rstrip()
        if not line or line.lstrip().startswith("#") or line.lstrip().startswith(";"):
            continue
        m = SECTION_RE.match(line)
        if m:
            section = m.group(1)
            continue
        m = KEYVAL_RE.match(line)
        if m and section:
            yield section, m.group(1), m.group(2).strip()


# --------------------------------------------------------------------------
# lint
# --------------------------------------------------------------------------

def find_default_cfg():
    env = os.environ.get("KOBOLDKARE_DIR")
    roots = [env] if env else [
        "/mnt/Matrix/SteamLibrary/steamapps/common/KoboldKare",
        os.path.expanduser("~/.steam/steam/steamapps/common/KoboldKare"),
        "/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare",
    ]
    for root in roots:
        p = os.path.join(root, "BepInEx", "config", "com.kk.llmnpc.cfg")
        if os.path.isfile(p):
            return p
    return None


def lint(cfg_path, sections, strict=False):
    errors, warnings = [], []
    with open(cfg_path, "r", encoding="utf-8-sig") as f:
        entries = list(parse_cfg(f.read()))
    for section, key, value in entries:
        doc = sections.get(section, {}).get(key)
        if doc is None:
            warnings.append("[%s] %s = %s — not in docs/CONFIG.md (stale doc, renamed key, or typo)"
                            % (section, key, value))
            continue
        checker = parse_range(doc[1])
        if checker is None:
            continue
        complaint = check_value(checker, value)
        if complaint:
            errors.append("[%s] %s: %s" % (section, key, complaint))
    return errors, warnings


# --------------------------------------------------------------------------
# selftest
# --------------------------------------------------------------------------

MD_FIXTURE = """# fixture
## [Alpha] — one

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Num` | `4` | 0–8 | a range with en-dash |
| `AtLeast` | `1` | ≥ 1 | minimum |
| `AtMost` | `0.9` | ≤ 0.9 | maximum |
| `Flag` | `true` | bool | a bool |
| `Modes` | `2` | 0, 1, 2 | membership |
| `Dotted` | `0.5` | 0..1 | double-dot range |
| `Free` | `x` | — | unconstrained |
| `Wordy` | `y` | whatever prose | unrecognized → skipped |

## [Beta] — two
### External host (Odd)

```ini
[Odd]
Keep = 30m
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Keep` | `30m` | — | from the ini fence section |
"""

CFG_FIXTURE = """[Alpha]

## doc line
# Setting type: Int32
Num = 12
AtLeast = 0
AtMost = 0.95
Flag = maybe
Modes = 5
Dotted = 0.5
Free = anything
Wordy = z
UnknownKey = 1

[Odd]
Keep = off
"""


def selftest():
    fails = []

    def ok(cond, what):
        if not cond:
            fails.append(what)

    secs = parse_config_md(MD_FIXTURE)
    ok(set(secs.keys()) >= {"Alpha", "Beta", "Odd"}, "sections parsed: %s" % list(secs.keys()))
    ok("Num" in secs.get("Alpha", {}), "Alpha.Num present")
    ok(secs.get("Odd", {}).get("Keep", ("", ""))[0] == "30m", "ini-fence section associated")

    cases = [
        ("0–8", 9, True), ("0–8", 8, False), ("0..1", 0.5, False), ("0..1", 1.5, True),
        ("≥ 1", 0, True), ("≥ 1", 1, False), ("≤ 0.9", 0.95, True), ("0, 1, 2", 5, True),
        ("0, 1, 2", 2, False), ("bool", "true", False), ("bool", "maybe", True),
        ("—", "anything", False), ("whatever prose", "x", False),
    ]
    for rng, val, should_fail in cases:
        c = parse_range(rng)
        if c is None:
            ok(not should_fail, "unparsed range %r never fails" % rng)
            continue
        got = check_value(c, str(val)) is not None
        ok(got == should_fail, "range %r value %r -> fail=%s (want %s)" % (rng, val, got, should_fail))

    entries = list(parse_cfg(CFG_FIXTURE))
    ok(("Alpha", "Num", "12") in entries, "cfg keyval parsed")
    ok(all(k != "" for _, k, _ in entries), "no empty keys")
    # comments with '=' inside must not parse as keyvals
    ok(not any("doc line" in k for _, k, _ in entries), "comment lines skipped")

    # lint on a temp file
    import tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".cfg", delete=False) as tf:
        tf.write(CFG_FIXTURE)
        tmp = tf.name
    try:
        errors, warnings = lint(tmp, secs)
    finally:
        os.unlink(tmp)
    joined = "\n".join(errors)
    ok("Num" in joined and "outside documented range" in joined, "Num out of range flagged")
    ok("Flag" in joined and "expected true/false" in joined, "Flag bad bool flagged")
    ok("Modes" in joined, "Modes membership flagged")
    ok(any("UnknownKey" in w for w in warnings), "unknown key warned")
    ok(not any("Free" in e or "Wordy" in e for e in errors), "unconstrained/prose keys not flagged")

    if fails:
        print("lint_config selftest: %d failure(s)" % len(fails))
        for f in fails:
            print("  FAIL: " + f)
        return 1
    print("lint_config selftest: all OK")
    return 0


def main(argv):
    if "--selftest" in argv:
        return selftest()
    strict = "--strict" in argv
    cfg_path = None
    md_path = "docs/CONFIG.md"
    for a in argv:
        if a.endswith(".cfg") or (a.endswith(".txt") and os.path.isfile(a)):
            cfg_path = a
        if a == "--config-md":
            i = argv.index(a)
            md_path = argv[i + 1]
    if cfg_path is None:
        cfg_path = find_default_cfg()
    if cfg_path is None or not os.path.isfile(cfg_path):
        print("lint_config: no .cfg found (pass a path, or set KOBOLDKARE_DIR)", file=sys.stderr)
        return 2
    with open(md_path, "r", encoding="utf-8") as f:
        sections = parse_config_md(f.read())
    errors, warnings = lint(cfg_path, sections, strict)
    for w in warnings:
        print("WARN  " + w)
    for e in errors:
        print("ERROR " + e)
    print("lint_config: %d error(s), %d warning(s) — %s" % (len(errors), len(warnings), cfg_path))
    return 1 if errors or (strict and warnings) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
