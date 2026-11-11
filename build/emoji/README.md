# Fluent emoji font (`Assets\Emoji\fluent.ttf`)

Modern-TDLib builds bundle `fluent.ttf` as the default emoji font: the **Flat** style of
[Microsoft Fluent Emoji](https://github.com/microsoft/fluentui-emoji) (MIT, see
`Assets\Emoji\fluent-LICENSE.txt`) converted to COLRv0 layered vector glyphs, which
Windows composites natively. Windows 10 Mobile's system Segoe UI Emoji predates many
emoji. The CBDT/CBLC colour-bitmap sets (`apple.ttf` and the Telegram cloud sets) also
render on Windows 10 Mobile (confirmed on device with stable 26.11.6040) and can be
selected under Settings > Appearance > Emoji Set from 26.11.6299.0 on. An earlier
assumption that they could not render was never device-verified.

The font deliberately covers no ordinary text (letters, digits, `#`, `*`, space,
`©`, `®`, `™`, arrows, and text-default symbols below U+2600 or in U+3000-32FF), because
`EmojiThemeFontFamily` is applied to whole names and messages. Fluent ships no country
flags or keycaps; those fall back to the system font. Vertical metrics copy Segoe UI so
line heights do not change.

## Rebuild

Pinned source: `microsoft/fluentui-emoji` commit `1ffb34c752ecf5d402f04cfb4b392c77f57c54bc`.
Unicode data: `emoji-data.txt` from Unicode 16.0. Tools: Python 3.12, `nanoemoji==0.16.0`,
`picosvg==0.23.0` (`fontTools` comes with nanoemoji). Work outside the repository; do not
commit the upstream SVGs.

```powershell
python -m venv venv; .\venv\Scripts\pip install nanoemoji==0.16.0 picosvg==0.23.0
git clone --filter=blob:none --sparse https://github.com/microsoft/fluentui-emoji src
git -C src checkout 1ffb34c752ecf5d402f04cfb4b392c77f57c54bc
git -C src sparse-checkout set --no-cone "assets/**/Flat/*.svg" "assets/**/metadata.json" LICENSE
curl -o emoji-data.txt https://www.unicode.org/Public/16.0.0/ucd/emoji/emoji-data.txt

.\venv\Scripts\python stage.py src\assets emoji-data.txt staged stage-report.json
.\venv\Scripts\nanoemoji --config config.toml        # -> build\UnigramEmoji-raw.ttf
.\venv\Scripts\python postprocess.py build\UnigramEmoji-raw.ttf stage-report.json fluent.ttf 1ffb34c752ecf5d402f04cfb4b392c77f57c54bc
```

- `stage.py` picks the Flat SVG (Default skin tone plus each tone variant), names it
  `emoji_u<codepoints without FE0F>.svg`, and skips sequences that start with or depend on
  text-default characters the font must not claim (keycaps, ©/®/™, arrows, etc.).
- `postprocess.py` removes nanoemoji's em-wide space, zero-widths components without art
  (ZWJ, skin-tone and hair modifiers), maps FE0F/FE0E to empty glyphs, adds FE0F-qualified
  ligature variants, sets Segoe UI vertical metrics, and writes the MIT copyright/licence
  name records.

Validate with HarfBuzz (each emoji sequence must shape to one COLR glyph; text must map
to `.notdef`) and a headless Edge `@font-face` page before copying `fluent.ttf` into
`Unigram\Unigram\Assets\Emoji\`.
