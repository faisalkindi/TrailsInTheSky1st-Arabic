# FDK Bitmap Font Spec — Trails in the Sky 1st (verified, not guessed)

## Files
- **Atlas:** `asset/dx11/image/font_0.dds` — **4096×4096, BC7_UNORM (DXGI 98), DX10 header (148 B)**,
  16,777,216 data bytes (1 B/px). RGBA. (`number_fonts.dds` = 1024² for digits.)
- **Metrics:** `asset/common/font/font_0.fnt` inside `pac/steam/asset_common_font.pac` (FPAC, 1 entry).

## .fnt format (FCV/FLTI) — from KuroTools `font/font.py`, confirmed
- Header 0x28 B: `'FCV\0'`, u16, u16, **u32 char_count (7555)**, u16×4, u32×3, `'FLTI'`, u32 flti_size.
- Records start 0x28, **24 B each**, `flti_size/24` of them:
  `u32 code` (Unicode), `u32 int0`, `u16 GNF_X`, `u16 GNF_Y`, `u16 Width`, `u16 Height`,
  `u16 flag`, `u16 half3`, `u16 half4`, `u16 half5`.

## ★ Channel/coverage model (THE key to clean rendering) — measured from vanilla glyphs
- The atlas is a **dual-channel coverage atlas**: each glyph's ink is stored in **ONE** of R/G, so two
  glyphs share the same pixels. **Alpha=255 everywhere, Blue=0.** The engine samples the glyph's
  assigned channel as the ink mask × text color (black in that channel = no ink).
- **flag `0x100` → coverage in GREEN.  flag `0x200` → coverage in RED.**
- Verified: `a` (U+0061, flag 0x200) → R = glyph shape, **G = 0** (clean single-channel). `A` (flag 0x100)
  → G = its shape; R may hold a *different* co-located glyph. Vanilla flag split: 3831×0x200, 3724×0x100.

## Why asmar's atlas has black-box artifacts (verified)
asmar mapped Arabic to flag 0x200 (Red) but **painted glyphs into BOTH channels (yellow)** over a band
that held CJK glyphs, leaving **dirty/opaque cells + red bleed** from neighbors. Result: boxes/fragments.

## ★★★ THE OUTLINE-BOX ROOT CAUSE + FIX (solved by disassembling the shader)
The black-box artifact on Arabic in outlined UI (title version/copyright, control-help) was the
**`ui_bold` text outline shader**, NOT the atlas/position/layout.
- Shaders live in `pac/steam/shader.pac` (FPAC) as `.fxo` (DXBC). UI text shaders: `ui_basic_*`
  (plain, no outline → dialog/menus, always clean), `ui_bold_*` / `ui_bold_depth_alpha_*` (outlined).
  Disassemble: `fxc /nologo /dumpbin ui_bold_p.fxo` (run via **PowerShell**, not git-bash).
- `ui_bold_p` builds the outline by edge-detecting over a **ring of ~30 offset samples**, and **clamps
  each ring sample to the glyph's own UV rect** (`v4` = the .fnt cell): `max …v4.xxyx ; min …v4.zzwz`.
  → If the glyph **ink touches its cell edge**, the clamped edge-sample makes a **false edge all around
  the cell → solid black box**. Vanilla clean glyphs (a/o/e/'.'/kanji) have **empty (0) cell edges**.
- **FIX:** give every glyph **transparent padding INSIDE its cell** (edges=0), `PAD_IN≈6px` (≥ ring
  radius). `W/H = ink + 2·PAD_IN`, ink centered; `.fnt` `h4 -= PAD_IN`, `h3 -= PAD_IN`. Then the outline
  ring clamps onto empty padding → clean thin outline, no box. Implemented in `build_arabic_atlas.py`.
- Because the shader clamps the ring to the cell, **glyph position/neighbours are irrelevant** once
  edges are padded (the band y≥2110 is fine).

## ★ Our clean atlas recipe (Layer 2)
1. Pick a **free atlas band** (asmar used y≥2100; confirm truly-unused cells or claim a fresh region).
2. Rasterize **SST-Arabic** presentation forms (U+FE70–FEFF + lam-alef ligatures) as **white coverage,
   tight bbox**, into **ONE channel** matching the glyph's flag (use **0x200 → Red**; set Green=0,
   Blue=0, Alpha=255 in those cells). Do **not** disturb other cells.
3. Write a `.fnt` record per form: `code`=presentation cp, `GNF_X/Y`=atlas pos, `W/H`=tight glyph size,
   `flag`=0x200, copy `int0`/half fields from a vanilla glyph template.
4. Encode atlas → BC7 with **texconv** (`-f BC7_UNORM`), keep 4096², splice/replace `font_0.dds`.
5. Repack `asset_common_font.pac` (FPAC) with the new `.fnt`, or ship loose.
- Coverage set needed = the codepoints `arabic_reshaper` can emit (≈146 forms; superset of asmar's 144).


## ★ Arabic punctuation glyphs (added) + RTL reveal limitation
- Our atlas originally held only LETTER presentation forms (FE70-FEFF + FB50-FDFF). Arabic
  punctuation U+060C(،) U+061B(؛) U+061F(؟) had NO glyph -> engine drew a Latin-style fallback
  ('?' looked flipped). FIX: build_arabic_atlas.py AR_PUNCT=[0x060C,0x061B,0x061F] added to the
  candidate set (same RED-channel + PAD_IN recipe). ASCII . ! ... share Arabic shapes -> left as-is.
- TYPEWRITER REVEAL DIRECTION: FDK has no bidi/RTL mode. The mes system reveals glyphs in STORAGE
  order; our text is stored VISUAL (reshaped+reversed), so the reveal runs logical-end->start =
  appears LTR. NOT fixable in text data while keeping correct static layout. True fix = patch the
  mes reveal in the EXE (cf. EdraHor zero.exe binpatch for Zero/Azure) -> BLOCKED by Denuvo.
  Verified workaround: Config > Message > Text Speed > INSTANT (values exist in t_text:
  Slow/Normal/Fast/Instant) -> whole bubble shown at once, no reveal, static RTL correct.


## ★★★ RTL text-reveal direction — DEFINITIVE RE verdict (RenderDoc, frame captures)
Goal: make cutscene/dialogue text reveal R->L. Outcome: NOT achievable via loose mods.

Evidence (RenderDoc 1.44 replay of in-game dialogue frames; qrenderdoc --python + pyrenderdoc):
- Text = INSTANCED draws. Glyph data in StructuredBuffer instances_g @ t15 (stride 192,
  InstanceParam: world(64) prevWorld(64) color@128 uv@144 param@160 boneAddress@176 param2@180).
- Vertex shader index = SV_InstanceID + cb_instance.x (instanceOffset_g = per-LINE base in t15).
  A dialogue line's glyphs are packed at [base, base+len); a 2nd line follows (base=0 then 30).
  Each visible line is drawn TWICE (main + drop-shadow) -> equal-count pairs (30,30)/(34,34).
- Reveal mechanism = CPU INSTANCE-COUNT: numInstances = currently-revealed glyph count, grows
  over time (captured same line at 15 then 36). (Red pixel-probe that ignored per-glyph alpha
  v2.w still typed on -> rules out alpha-ramp reveal.)
- To flip reveal R->L in-shader you must read t15[base + (TOTAL-1 - i)], which needs TOTAL
  (full line length). TOTAL is NEVER exposed to the shader: only instanceOffset_g and
  maxBoneCount_g(=0) are in cb_instance; param/param2 are constant; no count anywhere.
  Engine knows TOTAL only CPU-side (in the Denuvo-encrypted EXE).
Conclusion: R->L reveal would require patching the engine's text-submission (EXE, Denuvo-blocked)
or a custom D3D11 DrawInstanced-hook DLL that tracks max(numInstances) per line to derive TOTAL
and re-issues reversed (large, fragile, separate project). NOT doable via loose shader/font/data.
Practical resolution: Config > Message > Text Speed > INSTANT (whole line at once, static RTL
text already correct). Static layout + punctuation are fixed and shipping.
RenderDoc capture method that WORKED despite Denuvo: Steam Launch Options =
  "C:\Program Files\RenderDoc\renderdoccmd.exe" capture --opt-hook-children -c "<path>" %command%


## ★★★ HARAKAT (تشكيل / Arabic diacritics) ON FDK — SOLVED (spike verified in-game)
FDK has no shaper, so combining marks can't stack natively (unlike Elliot's UE5 HarfBuzz). But the
bitmap atlas CAN fake them with ZERO-ADVANCE OVERLAPPING GLYPHS — confirmed rendering perfectly:
- Add a glyph per COMBINING haraka codepoint (064E fatha,064F damma,0650 kasra,0651 shadda,0652 sukun,
  064B/064C/064D tanwin). Render the mark by (tatweel+mark) MINUS (tatweel) to isolate the mark shape
  (SST has the combining marks in cmap but NOT the FE7x isolated forms).
- .fnt record per mark: flag=0x200(red), **h5 advance = 0** (mark must not consume horizontal space),
  **h3 bearing = 0** (in visual order the NSM precedes its base letter and both anchor at the SAME pen X,
  so 0 centres the padded mark over the following letter), **h4 (half4) = 2 for above-marks / 56 for
  kasra+kasratan (below baseline ~59)**. Implemented in build_arabic_atlas.py (HK block).
- Reshaper: set delete_harakat=**False** in ALL inject scripts (scena_dialogue/inject_items/inject_pilot/
  table_functional) so the harakat survive to render. arabic_reshaper keeps them on the presentation forms;
  get_display (python-bidi) places each NSM right before its base in the visual string.
- Result: full تشكيل renders correctly in-engine (user: "looks perfect"). Matches the Elliot quality bar.
- Possible refinements (not yet needed): per-letter-width centring; shadda+vowel stacks (two marks on one
  letter overlap at h3=0). Tune h3/h4 per mark only if a screenshot shows offset.
