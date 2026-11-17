# HMY Hub manifest

Publish **`hub-manifest.json` at the root of `hmygroup/hmy-prompt-engineer`**. It is a curated catalog, not a tool compatibility allowlist or executable installation policy. Estao no longer uses `estao-harness.json` or a bundled approval fallback for the Desktop Hub.

- [JSON Schema](hub-manifest.schema.json)
- [Starter example](hub-manifest.example.json)
- [Skill sample](hub-manifest.json) (publish at the source repository root)

These are authoring deliverables in the Estao repository. They have **not** been published to the HMY source repository. The example is not an exhaustive catalog. Check its paths against the current HMY tree before publishing.

## Structure

`schemaVersion` is `2`. `defaultLanguage` is a lowercase ISO 639-1 two-letter code, normally `en`, used for category-label fallback only. `categories` maps stable category IDs to translated display labels. Separate `skills`, `instructions`, `files`, `mcps` and `clis` arrays declare entries; omitted arrays mean empty. The array determines the internal item type. Schema version 1, `items` and explicit `type` fields are rejected; there is no migration reader or legacy fallback.

MCP/CLI entries support per-user installation, updates and removal. Entries require `id`, `path` and `categories`; MCPs and CLIs additionally require a nonblank English `description` of at most 1024 characters. Only skills accept `dependencies`. The combined catalog limit is 500 entries.

Every item has:

- `id`: globally unique stable lowercase slug across every section. It can differ from the skill folder name.
- `path`: for skills, instructions and files, an exact repository-relative path using `/`. A skill points to its folder, **not** `SKILL.md`; its references, assets, scripts and licenses stay in the same installation group. An instruction or file points to one existing file. MCP/CLI entries instead use a Windows UNC network-folder path. Discovery validates its syntax without contacting the share and keeps the entry visible if the share is unreachable. Installation reads the folder only after the user chooses Install/Update. Network paths do not participate in repository path-overlap checks.
- `categories`: comma-separated registered IDs, e.g. `"core,database"`. Whitespace is trimmed and duplicate values removed. Use `""` for Uncategorized. Undefined category IDs are errors.
- Optional `dependencies` on skills: required catalog skill IDs. Dependencies must exist and cannot form cycles. Required skills are included in the installation review. Instructions and files cannot request automatic dependency installation.

Entries whose source files are missing from the current repository snapshot are omitted individually, without invalidating the remaining catalog. This applies to missing skill `SKILL.md` files, instructions and generic files. Existing skills with a missing required skill dependency (including transitive dependencies) remain visible with an issue, but cannot be installed until the dependency is available. Unrelated entries can still be installed or exported normally. Invalid schema, unsafe paths, duplicate identities, unknown category/dependency IDs and dependency cycles still invalidate the catalog.

Only MCP/CLI entries contain a manifest `description`; they do not accept `descriptions` translations. Their English text is displayed unchanged in every UI language (authors are responsible for writing English). Skills, instructions and files still reject manifest description overrides. The manifest does not contain `harnesses`, installation destinations, shell commands, global instruction merge policies, repository commit/tree pins, or an exhaustive list of exclusions. Unknown fields and unsafe/ambiguous paths are rejected. The runtime also validates repository source existence, unique IDs, category-label fallbacks and dependency references; JSON Schema alone cannot express all cross-reference checks.

Example MCP entry (the same structure applies to `clis`; backslashes must be JSON-escaped):

```json
{"id":"example-mcp","path":"\\\\server\\share\\mcps\\example","categories":"","description":"Provides access to internal engineering tools."}
```

The `id` is the visible name. Network payloads are not downloaded from GitHub and are not read during catalog discovery.

For example, place `{"id":"database-guide","path":"instructions/database.md","categories":"database"}` in `instructions`, or `{"id":"template","path":"templates/example.json","categories":""}` in `files`. These paths are illustrative, not claims that those files exist in HMY. Create or select the real files before adding them to the catalog.

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
- **Type**: All, Skills, Instructions, Files, MCPs, CLIs. MCP/CLI catalog entries appear in HMY, and detected installations also appear in Installed. MCPs have per-tool installed indicators and target selection, like skills. CLIs have one user-global installed location, no tool selector, and remain visible without any harness installed. A tool-specific filter excludes CLIs because they are not bound to a tool. Both support Install/Update and Uninstall.

Only detected tools have logo indicators. For skills, a normal checked logo means a local occurrence was found; a muted logo means not installed; scan errors are unknown, not absent. Being uncurated is not an incompatibility warning. Instruction/file entries are not presented as unsupported skills or installed tool configuration.

## Actions and safety

Valid repository skills, including those in Other, can be installed through the same explicit review and conflict-preserving transaction. Install all / per-tool catalog review select curated skills only. A single-item review includes only that skill and its dependencies, not the rest of the library.

Instructions and generic files offer **Export…** with an explicit native Save As destination. Export copies the source bytes without executing scripts or automatically modifying global/project tool configuration. Existing content is replaced only after the user's Save As confirmation. Destination changes detected before the final replacement abort the export and preserve that content. This is optimistic concurrency followed by atomic file replacement, not isolation from a process changing the path between filesystem calls; use a new filename when another application is actively writing the destination.

Instruction files may include optional YAML frontmatter with `name`, `description` and `descriptions` (language-to-text mapping), just like skill metadata. Hub reads this metadata for display, without requiring the name to match the filename. Plain Markdown instructions remain valid. A malformed header disables installation of that item without invalidating the rest of the catalog. Instruction descriptions are still not fields in `hub-manifest.json`.

Instructions also offer **Install instruction**, which installs the instruction body, without its YAML header, into every detected tool's personal instructions: Codex `<CODEX_HOME>/AGENTS.md` (or an active `AGENTS.override.md`), Claude `<CLAUDE_CONFIG_DIR>/CLAUDE.md`, Copilot `<COPILOT_HOME>/copilot-instructions.md`, and OpenCode `<OPENCODE_CONFIG_DIR>/AGENTS.md`, honoring default profile paths and overrides. This is an explicit action, never triggered by catalog discovery or Export. Existing text is preserved and the new guidance is appended in an ID-tagged block. Reinstalling identical content is a no-op; a different block with the same ID is a conflict requiring manual resolution, not an automatic overwrite. Existing bytes are backed up under `<Estao data>/instruction-backups`. Per-tool failures are reported independently. Project instruction files and skills are not modified. Start a new conversation to ensure each tool loads the new guidance.

For repository skills, the manifest and payloads are read from the same immutable GitHub snapshot. The review pins that snapshot without requiring a maintained commit hash inside the catalog. Integrity, local path checks, limits, conflict preservation and recoverable skill installation remain in force. Network MCP/CLI payloads instead use the folder contents available when Install/Update is chosen; they are not pinned by the GitHub commit.

### MCP and CLI packages

Each network folder must contain exactly one top-level `.exe`; the complete directory tree is copied to a private versioned folder under `<Estao data>/hub-packages`. Published .NET applications must include their dependencies, satellite resources, `appsettings.json`, `.deps.json` and `.runtimeconfig.json`. The corresponding .NET runtime must be installed; Estao checks this prerequisite before activating the package. The network share itself is never modified and package scripts are not executed during installation.

CLI command identity is the catalog ID with an optional `-cli` suffix removed: `tengu-cli` detects and installs `tengu.exe`. CLI packages must use that executable name. Installation adds the local package directory to the Windows **user** PATH and the current Estao process PATH, never machine PATH. PATH changes are broadcast to Windows; existing terminals and tools may need reopening. MCP installations write one named server to each selected tool's native configuration (Codex TOML, Claude/Copilot JSON, OpenCode JSON/JSONC), preserving unrelated settings and existing environment values. OpenCode uses `mcp.servers` for new configurations. Local .NET content roots point to the package directory so its application settings can be found.

Install becomes **Update** when the named MCP registration or CLI command already exists. Detection and management do not distinguish manual installations from Estao installations. Updates replace the selected registration/command with the new local package. Old MCP binaries are retained only when another server registration still references them. Uninstall asks for confirmation and removes the selected registrations and application files, including manually installed files. Packages in use must be stopped before updating or uninstalling. Shared command directories keep unrelated commands and their PATH entries. Published .NET package directories include dependency removal; otherwise only the command and same-stem sidecars are removed. An executable path hidden behind a package-manager/shell command must be made explicit before its application files can be removed; shared runtimes themselves are never removed.

Configuration originals and removed application files are backed up privately under `<Estao data>/network-backups`, with an indexed recovery map for removed files. Failed operations restore configuration/PATH and moved files where safe; concurrent configuration edits are never overwritten during rollback. This is not an OS-level transaction or automatic crash recovery. JSON/JSONC and TOML formatting may be normalized; original bytes remain in the backup. Symbolic links/reparse points, system roots, ambiguous configurations and unsafe paths are rejected. Network payloads have depth, file-count and size limits.

If the manifest is missing or invalid, HMY shows one catalog-level error and no curated items. Independently discovered skills remain available in Other; local inventory remains available offline. There is no hidden fallback that makes outdated catalog entries look approved, and no per-skill “unsupported” badge merely because curation metadata is absent.
