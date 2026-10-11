# Roadmap

Version 0.2 is an experimental, locally tested starting point. These are proposed
priorities, not claims of implemented support.

## Reliability first

- Expose operation IDs and completion/recovery status so a client can resolve a
  timeout without repeating a mutation or searching backup directories.
- Add explicit snapshot/restore tools with a report of which base-game and mod
  fields were checked. Keep recovery cards when restoration is incomplete.
- Identify mod assets by stable Sideloader metadata where available; resolve
  runtime IDs on each launch and report missing dependencies clearly.
- Expand live coverage across hair/clothes slots, female characters, card-load
  toggles and common mod combinations. Keep exact compatibility notes.

## Character design workflow

- Eye/iris, eyebrow and skin settings, with the same validation and backup model.
- Accessory selection, transforms and colors; active-coordinate selection.
- Named camera presets and consistent multi-angle captures for comparisons.
- Optional ABMX and Material Editor adapters, discovered by installed plugin
  version and disabled when unsupported. Preserve extended card data explicitly.

## Distribution

- A tagged release with verified installation instructions and suitable
  distributable plugin binaries, retaining third-party license notices.
- Separate read-only diagnostics and opt-in live tests; no proprietary game
  binaries, private cards or runtime credentials in CI or releases.
- A minimal example workflow: save baseline, query assets, edit, capture,
  compare, save a new card, restore.
