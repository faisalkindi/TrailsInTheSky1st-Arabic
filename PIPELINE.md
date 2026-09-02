# Pipeline notes (for contributors)

Working files, not needed to play. Everything player-facing is on the Releases page.

## Engine (Falcom FDK — not Unreal)

- All content ships in `FPAC` containers: `table_en.pac` (UI/items/skills/names/quests, ~6.3 MB), `script_en.pac` (scena dialogue scripts, ~50 MB), `image_en.pac` (localized UI textures), `asset_common_font.pac` (glyph metrics: codepoint → atlas cell + advance).
- The engine mounts loose files over packs: files under `table_en\`, `script_en\scena\`, `asset\dx11\image\` **override** the packed `.pac` — no repack, no encryption.
- Tables: KuroTools `tbl2json.py` / FalcomTBLTool. Scena: **Ingert** (decompile/recompile `.dat` ⇄ `.ing`, perfect roundtrip).
- Font: `asset\dx11\image\font_0.dds` is a 4096×4096 single-channel glyph atlas; `number_fonts.dds` 1024×1024.

## Arabic on a shaper-less engine

The engine has no text shaper and no bidi. Arabic must be pre-shaped before injection:

1. `arabic_reshaper` → join letters into **presentation forms** (U+FB50–FDFF / U+FE70–FEFF).
2. `python-bidi` `get_display` → visual reorder. Zero bidi control marks in the final text.
3. Custom atlas: SST Arabic glyphs (all needed presentation forms) rasterized into `font_0.dds`, with matching metrics added to `asset_common_font.pac`. Coverage verified 100% against the reshaper output.

## Layout

| Path | What |
|---|---|
| `t_name.json`, `t_skill.json`, `t_help.json` | Translation tables (names, skills, help text) |
| `FONT_SPEC.md` | Atlas and metrics format notes |
| `work/INSTALLER_107/` | Installer source (Python 3.12 + PySide6, PyInstaller one-file) — built v1.1.0 |
| `work/INSTALLER_CS/` | Earlier C# WinForms installer source |

## Dialogue injection

Full-game dialogue: 46,489 bubbles across 154 scenes injected and recompiled with 0 failures. Hard-won lesson: escape `\` and `"` in injected Arabic — quoted names/speech inside `.ing` string syntax broke 848 bubbles before the fix.

## Installer (v1.1.0)

`work/INSTALLER_107/installer.py`: detects the Steam install, gates on game version/file layout via `data/manifest.json` (built against game v1.07), backs originals up to `_arabic_mod_backup`, writes with elevation when needed, restores on uninstall. Build with PyInstaller using `TrailsAr_Arabic_Installer_107.spec`; the `data/` payload (patched pacs + image entries + vanilla hashes) is not committed — regenerate from a patched install.
