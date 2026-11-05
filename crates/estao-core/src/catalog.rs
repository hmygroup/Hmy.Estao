//! The catalog of usage providers supported by Estao, ported from
//! `ProviderCatalog.cs`.

/// Provider ids enabled by default in a fresh config.
pub const INITIAL_PROVIDER_IDS: [&str; 5] = ["codex", "claude", "copilot", "opencode", "zai"];

/// Normalizes a provider id or one of its known aliases.
pub fn normalize_id(id: &str) -> String {
    let normalized = id.trim().to_ascii_lowercase();
    match normalized.as_str() {
        "githubcopilot" | "github-copilot" => "copilot".to_string(),
        "open-code" => "opencode".to_string(),
        "z-ai" | "z.ai" | "zai-coding-plan" | "zaicodingplan" | "glm" | "glm-coding-plan"
        | "zhipu" | "zhipuai" => "zai".to_string(),
        _ => normalized,
    }
}

/// Whether `id` (after normalization) is a provider Estao supports.
pub fn is_supported(id: &str) -> bool {
    INITIAL_PROVIDER_IDS.contains(&normalize_id(id).as_str())
}

/// The user-facing display name for a provider id.
pub fn display_name(id: &str) -> String {
    match normalize_id(id).as_str() {
        "codex" => "Codex".to_string(),
        "claude" => "Claude".to_string(),
        "copilot" => "GitHub Copilot".to_string(),
        "opencode" => "OpenCode".to_string(),
        "zai" => "Z.ai".to_string(),
        other => other.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from ProviderCatalogTests.normalizes_supported_aliases.
    #[test]
    fn normalizes_supported_aliases() {
        assert_eq!(normalize_id("github-copilot"), "copilot");
        assert_eq!(normalize_id("open-code"), "opencode");
        assert_eq!(normalize_id("Claude"), "claude");
    }

    #[test]
    fn recognizes_all_initial_providers() {
        for id in INITIAL_PROVIDER_IDS {
            assert!(is_supported(id), "{id} should be supported");
        }
        assert!(is_supported("z.ai"));
        assert!(!is_supported("unknown-provider"));
    }

    #[test]
    fn display_names_match_catalog() {
        assert_eq!(display_name("copilot"), "GitHub Copilot");
        assert_eq!(display_name("zai"), "Z.ai");
        assert_eq!(display_name("unknown"), "unknown");
    }
}
