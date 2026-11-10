"""Post-process the nanoemoji COLRv0 font so it is safe as the app-wide emoji font family."""
import itertools, json, sys
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString

src, report_path, out, fluent_commit = sys.argv[1:5]
report = json.load(open(report_path, encoding="utf-8"))

f = TTFont(src)
glyf, hmtx = f["glyf"], f["hmtx"]
colr = f["COLR"].ColorLayers
assert f["COLR"].version == 0

# nanoemoji maps U+0020 to an em-wide glyph; leave spaces to the text font.
for t in f["cmap"].tables:
    t.cmap.pop(0x20, None)

# Component-only code points (ZWJ, skin-tone modifiers without standalone art) get em-wide
# blank placeholders from nanoemoji. Make them zero-width so an unknown sequence does not
# leave visible gaps.
zeroed = []
for cp, g in sorted(f.getBestCmap().items()):
    if g not in colr:
        hmtx[g] = (0, 0)
        zeroed.append("%04X" % cp)

# Variation selectors: keep them inside the emoji run (no fallback split) as invisible glyphs.
for name, cp in (("uniFE0F", 0xFE0F), ("uniFE0E", 0xFE0E)):
    g = Glyph()
    g.numberOfContours = 0
    glyf[name] = g
    hmtx[name] = (0, 0)
    for t in f["cmap"].tables:
        if t.isUnicode():
            t.cmap[cp] = name
if "uniFE0F" not in f.getGlyphOrder():
    raise SystemExit("glyph order not updated")

# Rebuild GSUB: nanoemoji's FE0F-free ligatures plus fully-/partially-qualified variants.
ligs = {}
for lookup in f["GSUB"].table.LookupList.Lookup:
    for st in lookup.SubTable:
        for first, entries in st.ligatures.items():
            for lig in entries:
                ligs[tuple([first] + lig.Component)] = lig.LigGlyph
base_count = len(ligs)

cmap = f.getBestCmap()
added = 0
for q in report["qualified"]:
    q = [int(c, 16) for c in q]
    canon = [c for c in q if c != 0xFE0F]
    if len(canon) < 2:
        continue
    key = tuple(cmap[c] for c in canon)
    target = ligs.get(key)
    if target is None:
        raise SystemExit("missing ligature for %s" % q)
    # Positions (in canon) after which an FE0F follows in the fully-qualified form.
    slots, i = [], -1
    for c in q:
        if c == 0xFE0F:
            slots.append(i)
        else:
            i += 1
    for n in range(1, len(slots) + 1):
        for chosen in itertools.combinations(slots, n):
            seq = []
            for idx, c in enumerate(canon):
                seq.append(cmap[c])
                if idx in chosen:
                    seq.append("uniFE0F")
            if tuple(seq) not in ligs:
                ligs[tuple(seq)] = target
                added += 1

fea = ["languagesystem DFLT dflt;", "languagesystem latn dflt;", "feature ccmp {"]
for comps, target in sorted(ligs.items(), key=lambda kv: (-len(kv[0]), kv[0])):
    fea.append("  sub %s by %s;" % (" ".join(comps), target))
fea.append("} ccmp;")
del f["GSUB"]
addOpenTypeFeaturesFromString(f, "\n".join(fea), tables=["GSUB"])

# Match Segoe UI / the historical bundled emoji font so line heights do not change.
hh, os2 = f["hhea"], f["OS/2"]
hh.ascent, hh.descent, hh.lineGap = 2210, -514, 0
os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap = 1491, -431, 269
os2.usWinAscent, os2.usWinDescent = 2210, 514
os2.fsSelection = 0x40

name = f["name"]
copyright_text = "Emoji artwork Copyright (c) Microsoft Corporation (Fluent Emoji, flat style)."
license_text = (
    "Artwork from microsoft/fluentui-emoji commit %s, licensed under the MIT License. "
    "Converted to COLRv0 for Unigram Mobile with nanoemoji." % fluent_commit
)
for nid, text in ((0, copyright_text), (13, license_text), (14, "https://github.com/microsoft/fluentui-emoji/blob/main/LICENSE")):
    name.setName(text, nid, 3, 1, 0x409)
    name.setName(text, nid, 1, 0, 0)

f["post"].formatType = 3.0
f.save(out)
print("ligatures", base_count, "+fe0f_variants", added, "zero_width", zeroed)
