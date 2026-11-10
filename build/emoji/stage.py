"""Stage Fluent Emoji Flat SVGs as nanoemoji inputs (emoji_u<seq>.svg) and record policy."""
import json, os, re, shutil, sys

root, emoji_data, out_dir, report = sys.argv[1:5]

TONES = {"Light": "1f3fb", "Medium-Light": "1f3fc", "Medium": "1f3fd", "Medium-Dark": "1f3fe", "Dark": "1f3ff"}
STRUCTURAL = {0x200D, 0xFE0F, 0x1F3FB, 0x1F3FC, 0x1F3FD, 0x1F3FE, 0x1F3FF}

presentation = set()
for line in open(emoji_data, encoding="utf-8"):
    line = line.split("#", 1)[0].strip()
    if not line:
        continue
    rng, prop = [s.strip() for s in line.split(";")]
    if prop != "Emoji_Presentation":
        continue
    a, _, b = rng.partition("..")
    presentation.update(range(int(a, 16), int(b or a, 16) + 1))


def excluded_reason(seq):
    if 0x20E3 in seq:
        return "keycap"
    for cp in seq:
        if cp in STRUCTURAL or cp in presentation:
            continue
        # Text-default symbols that commonly appear in ordinary names/messages (arrows, (c), (R),
        # TM, geometric shapes, CJK marks) stay with the system font so plain text is unchanged.
        if cp < 0x2600 or 0x3000 <= cp < 0x3300:
            return "text_default_%04x" % cp
    return None


def parse(s):
    return [int(x, 16) for x in s.split()]


if os.path.isdir(out_dir):
    shutil.rmtree(out_dir)
os.makedirs(out_dir)

staged, qualified, skipped = {}, [], []
for d in sorted(os.listdir(root)):
    p = os.path.join(root, d)
    md = json.load(open(os.path.join(p, "metadata.json"), encoding="utf-8"))
    entries = []
    if "unicodeSkintones" in md:
        tones = md["unicodeSkintones"]
        for folder in ["Default"] + list(TONES):
            if folder == "Default":
                seq = md["unicode"]
            else:
                matches = [t for t in tones if TONES[folder] in t.split()]
                if len(matches) != 1:
                    raise SystemExit("ambiguous tone %s %s %s" % (d, folder, matches))
                seq = matches[0]
            entries.append((seq, os.path.join(p, folder, "Flat")))
    else:
        entries.append((md["unicode"], os.path.join(p, "Flat")))

    for seq_text, flat in entries:
        svgs = [f for f in os.listdir(flat) if f.endswith(".svg")]
        if len(svgs) != 1:
            raise SystemExit("expected one svg in %s: %s" % (flat, svgs))
        q = parse(seq_text)
        reason = excluded_reason(q)
        if reason:
            skipped.append((d, seq_text, reason))
            continue
        canon = tuple(cp for cp in q if cp != 0xFE0F)
        if canon in staged:
            raise SystemExit("duplicate sequence %s (%s, %s)" % (seq_text, d, staged[canon]))
        staged[canon] = d
        name = "emoji_u" + "_".join("%04x" % cp for cp in canon) + ".svg"
        shutil.copyfile(os.path.join(flat, svgs[0]), os.path.join(out_dir, name))
        if 0xFE0F in q:
            qualified.append(q)

json.dump(
    {
        "staged": len(staged),
        "qualified": [[("%04x" % c) for c in q] for q in qualified],
        "skipped": skipped,
    },
    open(report, "w", encoding="utf-8"),
    indent=1,
)
print("staged", len(staged), "qualified_with_fe0f", len(qualified), "skipped", len(skipped))
for s in skipped:
    print("  skip", s)
