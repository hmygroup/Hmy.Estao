//! Light/dark theme tokens for all Estao windows.
//!
//! Wave 5 contract: a `Theme` enum (`Dark` | `Light`, defaulting from
//! `EstaoConfig.theme`) with a palette of gpui colors (background, surface,
//! border, text, muted text, accent, warning, critical) used by the
//! settings window, usage popover, and taskbar overlay.

use gpui::{rgb, Rgba};

/// The UI theme selected in the config file.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum Theme {
    /// Neutral dark "Graphite" look, the .NET default.
    #[default]
    Dark,
    /// Clean neutral light look.
    Light,
}

impl Theme {
    /// Parses the `theme` config value, defaulting to [`Theme::Dark`].
    pub fn from_name(name: &str) -> Self {
        match name.trim().to_ascii_lowercase().as_str() {
            "light" => Self::Light,
            _ => Self::Dark,
        }
    }

    /// The value persisted in `EstaoConfig.theme`.
    pub fn name(self) -> &'static str {
        match self {
            Self::Dark => "dark",
            Self::Light => "light",
        }
    }

    /// The color palette for this theme.
    pub fn palette(self) -> Palette {
        match self {
            Self::Dark => Palette::dark(),
            Self::Light => Palette::light(),
        }
    }
}

/// The gpui colors shared by every Estao window.
///
/// Later waves (settings, popover, overlay) consume the remaining tokens, so
/// the struct is intentionally complete even though the Wave 5 placeholders
/// only paint the background, surface, border, text and accent.
#[allow(dead_code)]
#[derive(Debug, Clone, Copy)]
pub struct Palette {
    /// Window background.
    pub background: Rgba,
    /// Raised card or panel background.
    pub surface: Rgba,
    /// Hovered/selected surface.
    pub surface_hover: Rgba,
    /// Hairline borders and separators.
    pub border: Rgba,
    /// Primary text.
    pub text: Rgba,
    /// Secondary or disabled text.
    pub text_muted: Rgba,
    /// Interactive accent (links, focus rings, primary actions).
    pub accent: Rgba,
    /// Warning state, e.g. usage above the warning threshold.
    pub warning: Rgba,
    /// Critical state, e.g. usage above the critical threshold.
    pub critical: Rgba,
}

impl Palette {
    /// The neutral dark "Graphite" palette used by the .NET tray app.
    pub fn dark() -> Self {
        Self {
            background: rgb(0x1E1E1E),
            surface: rgb(0x262626),
            surface_hover: rgb(0x2F2F2F),
            border: rgb(0x3A3A3A),
            text: rgb(0xF5F5F5),
            text_muted: rgb(0xA3A3A3),
            accent: rgb(0x4C8DFF),
            warning: rgb(0xF59E0B),
            critical: rgb(0xEF4444),
        }
    }

    /// A clean neutral light palette.
    pub fn light() -> Self {
        Self {
            background: rgb(0xF7F7F7),
            surface: rgb(0xFFFFFF),
            surface_hover: rgb(0xECECEC),
            border: rgb(0xD8D8D8),
            text: rgb(0x1F1F1F),
            text_muted: rgb(0x616161),
            accent: rgb(0x2563EB),
            warning: rgb(0xD97706),
            critical: rgb(0xDC2626),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_configured_theme_name() {
        assert_eq!(Theme::from_name("light"), Theme::Light);
        assert_eq!(Theme::from_name(" LIGHT "), Theme::Light);
        assert_eq!(Theme::from_name("dark"), Theme::Dark);
        assert_eq!(Theme::from_name("graphite"), Theme::Dark);
        assert_eq!(Theme::default(), Theme::Dark);
    }

    #[test]
    fn round_trips_the_persisted_names() {
        assert_eq!(Theme::from_name(Theme::Dark.name()), Theme::Dark);
        assert_eq!(Theme::from_name(Theme::Light.name()), Theme::Light);
    }
}
