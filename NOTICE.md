# Notice

This is an unofficial community utility and is not affiliated with or endorsed by Re-Logic.

The repository and release archives contain **no** Terraria executable, game asset, world save,
player save, or map exploration file. The planning half of this tool opens a selected `.wld` file
**read-only** and never writes to it. The in-game half installs a reversible patch into a local
Terraria installation that the user already owns; it never redistributes game binaries.

## Prior work reused

- `Terraria-Biome-Containment-Analyzer` (same author) — the `.wld` section/version layout, the
  corruption/crimson/hallow tile id sets, the hardmode conversion sets, and the plant/vine bridge
  reach constants are ported from that project so that containment verdicts stay consistent with it.
  Its own reference to vanilla spread semantics is preserved here in `docs/research/`.
- `TingYu` / `Chaite` (same author) — the reversible Mono.Cecil injection skeleton, the
  install/restore hashing discipline, and the WinForms UI smoke-test pattern.

Nothing in this repository is derived from Re-Logic source, decompiled or otherwise, beyond the
publicly documented save format and tile identifier numbers needed to read a world the user owns.
