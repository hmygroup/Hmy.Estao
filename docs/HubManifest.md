# HMY Hub manifest

Publish **`hub-manifest.json` at the root of `hmygroup/hmy-prompt-engineer`**. It is a curated catalog, not a tool compatibility allowlist or executable installation policy. Estao no longer uses `estao-harness.json` or a bundled approval fallback for the Desktop Hub.

- [JSON Schema](hub-manifest.schema.json)
- [Starter example](hub-manifest.example.json)
- [Six-skill sample](hub-manifest.json) (publish at the source repository root)

These are authoring deliverables in the Estao repository. They have **not** been published to the HMY source repository. The example is not an exhaustive catalog. Check its paths against the current HMY tree before publishing.

## Structure

`schemaVersion` is `1`. `defaultLanguage` is a lowercase ISO 639-1 two-letter code, normally `en`, used for category-label fallback only. `categories` maps stable category IDs to translated display labels. `items` lists the curated catalog entries.

Every item has:

- `id`: unique stable lowercase slug. It can differ from the skill folder name.
- `type`: `skill`, `instruction`, or `file`. Types control handling; they are not browsing categories.
- `path`: exact repository-relative path using `/`. A skill points to its folder, **not** `SKILL.md`; its references, assets, scripts and licenses stay in the same installation group. An instruction or file points to one existing file.
- `categories`: comma-separated registered IDs, e.g. `"core,database"`. Whitespace is trimmed and duplicate values removed. Use `""` for Uncategorized. Undefined category IDs are errors.
- Optional `dependencies` on skills: required catalog skill IDs. Dependencies must exist and cannot form cycles. Required skills are included in the installation review. Instructions and files cannot request automatic dependency installation.

Entries whose source files are missing from the current repository snapshot are omitted individually, without invalidating the remaining catalog. This applies to missing skill `SKILL.md` files, instructions and generic files. Existing skills with a missing required skill dependency (including transitive dependencies) remain visible with an issue, but cannot be installed until the dependency is available. Unrelated entries can still be installed or exported normally. Invalid schema, unsafe paths, duplicate identities, unknown category/dependency IDs and dependency cycles still invalidate the catalog.

The manifest does **not** contain item `description` or `descriptions` fields, even as overrides. Remove those fields from existing catalogs; they are rejected as unknown fields. It also does not contain `harnesses`, installation destinations, shell commands, global instruction merge policies, repository commit/tree pins, or an exhaustive list of exclusions. Unknown fields and unsafe/ambiguous paths are rejected. The runtime also validates source existence, unique IDs, category-label fallbacks and dependency references; JSON Schema alone cannot express all cross-reference checks.

For example, an instruction entry can use `"type": "instruction"` and `"path": "instructions/database.md"`; a template can use `"type": "file"` and `"path": "templates/example.json"`. These paths are illustrative, not claims that those files exist in HMY. Create or select the real files before adding them to the catalog.

## Skill descriptions

Descriptions belong only in the skill's `SKILL.md` YAML frontmatter:

```yaml
---
name: batukada
description: Prepare changes and run independent reviews.
descriptions:
  en: Prepare changes and run independent reviews.
  es: Prepara cambios y ejecuta revisiones independientes.
  fr: Prépare les changements et lance des revues indépendantes.
---
```

The standard scalar `description` remains required for tool compatibility and fallback. The optional `descriptions` mapping is an Estao extension; no default-language translation is required. Estao uses the current UI language's translation when present, otherwise the standard `description` directly, regardless of the catalog's `defaultLanguage`. Existing skills without `descriptions` continue to work. This applies to curated, unlisted repository and installed local skills, including localized search and review descriptions. Source bytes are installed unchanged; Estao never rewrites frontmatter or translates the skill body.

Translation keys must be lowercase ISO 639-1 two-letter codes (such as `en`, `es`, `fr`), with at most 20 entries. Each value must be nonblank scalar text of at most 1024 characters, matching the standard description length limit. Invalid mappings are reported as metadata issues; YAML aliases remain unsupported. Instructions and generic files have no catalog description and use the UI's no-description state; their contents are not parsed as skills.

## UI behavior

- **Installed**: all discovered user/global skills for detected tools, including unmanaged skills. Matching unambiguous repository skill identities can supply catalog metadata; presence does not prove identical contents or origin. Ambiguous repository copies remain separate rather than claiming a source for local files.
- **HMY**: only entries explicitly curated by this manifest, including instructions and files.
- **Other**: unlisted repository skills plus local-only skills. Arbitrary unlisted repository files do not appear.
- **Category**: one selected category matches membership anywhere in an item's category list. All categories and Uncategorized are available. Category labels use the UI language, then the catalog default language. Skill descriptions use the frontmatter translation for the UI language, then the standard frontmatter description.
- **Type**: All, Skills, Instructions, Files. Search and tool/status filters combine with category/type filters.

Only detected tools have logo indicators. For skills, a normal checked logo means a local occurrence was found; a muted logo means not installed; scan errors are unknown, not absent. Being uncurated is not an incompatibility warning. Instruction/file entries are not presented as unsupported skills or installed tool configuration.

## Actions and safety

Valid repository skills, including those in Other, can be installed through the same explicit review and conflict-preserving transaction. Install all / per-tool catalog review select curated skills only. A single-item review includes only that skill and its dependencies, not the rest of the library.

Instructions and generic files offer **Export…** with an explicit native Save As destination. Export copies the source bytes without executing scripts or automatically modifying global/project tool configuration. Existing content is replaced only after the user's Save As confirmation. Destination changes detected before the final replacement abort the export and preserve that content. This is optimistic concurrency followed by atomic file replacement, not isolation from a process changing the path between filesystem calls; use a new filename when another application is actively writing the destination.

Instruction files may include optional YAML frontmatter with `name`, `description` and `descriptions` (language-to-text mapping), just like skill metadata. Hub reads this metadata for display, without requiring the name to match the filename. Plain Markdown instructions remain valid. A malformed header disables installation of that item without invalidating the rest of the catalog. Descriptions are still not fields in `hub-manifest.json`.

Instructions also offer **Install instruction**, which installs the instruction body, without its YAML header, into every detected tool's personal instructions: Codex `<CODEX_HOME>/AGENTS.md` (or an active `AGENTS.override.md`), Claude `<CLAUDE_CONFIG_DIR>/CLAUDE.md`, Copilot `<COPILOT_HOME>/copilot-instructions.md`, and OpenCode `<OPENCODE_CONFIG_DIR>/AGENTS.md`, honoring default profile paths and overrides. This is an explicit action, never triggered by catalog discovery or Export. Existing text is preserved and the new guidance is appended in an ID-tagged block. Reinstalling identical content is a no-op; a different block with the same ID is a conflict requiring manual resolution, not an automatic overwrite. Existing bytes are backed up under `<Estao data>/instruction-backups`. Per-tool failures are reported independently. Project instruction files and skills are not modified. Start a new conversation to ensure each tool loads the new guidance.

Manifest and payloads are read from the same immutable GitHub snapshot. The review pins that snapshot without requiring a maintained commit hash inside the catalog. Integrity, local path checks, limits, conflict preservation and recoverable skill installation remain in force.

If the manifest is missing or invalid, HMY shows one catalog-level error and no curated items. Independently discovered skills remain available in Other; local inventory remains available offline. There is no hidden fallback that makes outdated catalog entries look approved, and no per-skill “unsupported” badge merely because curation metadata is absent.
