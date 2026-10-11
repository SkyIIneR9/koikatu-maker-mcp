# Roadmap

Version 0.3 provides the core character-design workflow with optional mod adapters.
See TESTING.md for what was exercised on a live game. Remaining items below are
proposed work, not claims of implemented support.

## Implemented in 0.3

- Bounded operation journal, operation IDs in failures and retained-request deduplication.
- Card/camera/coordinate snapshots and restoration reports for exposed base-game fields.
- Sideloader GUID/original-slot resolution.
- Eye/eyebrow/skin/makeup/name editing, standard accessories and outfit selection.
- Optional BodyTop ABMX and Material Editor color/float adapters.
- Named cameras and multi-angle MCP images with camera restoration.

## Reliability first

- Exercise real engine-stall timeout scenarios, bounded-journal eviction and recovery after restart.
- Expand snapshot verification to more extended mod fields and missing-dependency diagnostics.
- Expand live coverage across hair/clothes slots, female characters, card-load
  toggles and common mod combinations. Keep exact compatibility notes.

## Character design workflow

- MoreAccessories slots, clothing patterns/subparts and coordinate makeup.
- Texture/overlay import and additional shader properties with typed validation.
- Optional accessory-bone ABMX support and clearer capabilities for unsupported mod APIs.

## Distribution

- A tagged release with verified installation instructions and suitable
  distributable plugin binaries, retaining third-party license notices.
- Separate read-only diagnostics and opt-in live tests; no proprietary game
  binaries, private cards or runtime credentials in CI or releases.
- A minimal example workflow: save baseline, query assets, edit, capture,
  compare, save a new card, restore.
