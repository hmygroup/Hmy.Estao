# HMY Hub manifest

Publish **`hub-manifest.json` at the root of `hmygroup/hmy-prompt-engineer`**. It is a curated catalog, not a tool compatibility allowlist or executable installation policy. Estao no longer uses `estao-harness.json` or a bundled approval fallback for the Desktop Hub.

- [JSON Schema](hub-manifest.schema.json)
- [Starter example](hub-manifest.example.json)

These are authoring deliverables in the Estao repository. They have **not** been published to the HMY source repository. The example is not an exhaustive catalog. Check its paths against the current HMY tree before publishing.

## Structure

`schemaVersion` is `1`. `defaultLanguage` is a lowercase ISO 639-1 two-letter code, normally `en`. `categories` maps stable category IDs to translated display labels. `items` lists the curated catalog entries.

Every item has:

- `id`: unique stable lowercase slug. It can differ from the skill folder name.
- `type`: `skill`, `instruction`, or `file`. Types control handling; they are not browsing categories.
- `path`: exact repository-relative path using `/`. A skill points to its folder, **not** `SKILL.md`; its references, assets, scripts and licenses stay in the same installation group. An instruction or file points to one existing file.
- `description`: language-to-text dictionary, e.g. `{ "en": "Database guidance", "es": "Guía de bases de datos" }`. Include the default-language description. Translations are display metadata; Estao never rewrites source file contents or skill frontmatter to translate them.
- `categories`: comma-separated registered IDs, e.g. `"core,database"`. Whitespace is trimmed and duplicate values removed. Use `""` for Uncategorized. Undefined category IDs are errors.
- Optional `dependencies` on skills: required catalog skill IDs. Dependencies must exist and cannot form cycles. Required skills are included in the installation review. Instructions and files cannot request automatic dependency installation.

The manifest does **not** contain `harnesses`, installation destinations, shell commands, global instruction merge policies, repository commit/tree pins, or an exhaustive list of exclusions. Unknown fields and unsafe/ambiguous paths are rejected. The runtime also validates source existence, unique IDs, translation fallbacks and dependency references; JSON Schema alone cannot express all cross-reference checks.

For example, an instruction entry can use `"type": "instruction"` and `"path": "instructions/database.md"`; a template can use `"type": "file"` and `"path": "templates/example.json"`. These paths are illustrative, not claims that those files exist in HMY. Create or select the real files before adding them to the catalog.

## UI behavior

- **Installed**: all discovered user/global skills for detected tools, including unmanaged skills. Matching unambiguous repository skill identities can supply catalog metadata; presence does not prove identical contents or origin. Ambiguous repository copies remain separate rather than claiming a source for local files.
- **HMY**: only entries explicitly curated by this manifest, including instructions and files.
- **Other**: unlisted repository skills plus local-only skills. Arbitrary unlisted repository files do not appear.
- **Category**: one selected category matches membership anywhere in an item's category list. All categories and Uncategorized are available. Category labels and descriptions use the UI language, then the catalog default language. Source skill descriptions remain the fallback for unlisted skills.
- **Type**: All, Skills, Instructions, Files. Search and tool/status filters combine with category/type filters.

Only detected tools have logo indicators. For skills, a normal checked logo means a local occurrence was found; a muted logo means not installed; scan errors are unknown, not absent. Being uncurated is not an incompatibility warning. Instruction/file entries are not presented as unsupported skills or installed tool configuration.

## Actions and safety

Valid repository skills, including those in Other, can be installed through the same explicit review and conflict-preserving transaction. Install all / per-tool catalog review select curated skills only. A single-item review includes only that skill and its dependencies, not the rest of the library.

Instructions and generic files use **Export…** with an explicit native Save As destination. Export copies the source bytes without executing scripts or automatically modifying global/project tool configuration. Existing content is replaced only after the user's Save As confirmation. Destination changes detected before the final replacement abort the export and preserve that content. This is optimistic concurrency followed by atomic file replacement, not isolation from a process changing the path between filesystem calls; use a new filename when another application is actively writing the destination. Automatic instruction registration/merging needs a separate scope and merge design and is not implied by adding an instruction to this catalog.

Manifest and payloads are read from the same immutable GitHub snapshot. The review pins that snapshot without requiring a maintained commit hash inside the catalog. Integrity, local path checks, limits, conflict preservation and recoverable skill installation remain in force.

If the manifest is missing or invalid, HMY shows one catalog-level error and no curated items. Independently discovered skills remain available in Other; local inventory remains available offline. There is no hidden fallback that makes outdated catalog entries look approved, and no per-skill “unsupported” badge merely because curation metadata is absent.
