//! Asset icons: the Estao tray icon and provider icons.
//!
//! Wave 5 contract: rasterize `assets/estao-icon.svg` with resvg into an
//! RGBA buffer for `tray_icon::Icon`. Later waves expose provider SVGs
//! (`assets/ProviderIcons/*.svg`) for in-window rendering.

use std::path::PathBuf;

use anyhow::{anyhow, bail, Context, Result};
use estao_core::catalog;

/// The SVG embedded in the binary so the tray icon works from any directory.
const ESTAO_ICON_SVG: &str = include_str!("../../../../assets/estao-icon.svg");

/// Edge length of the tray icon, in physical pixels.
pub const TRAY_ICON_SIZE: u32 = 32;

/// Rasterizes the Estao SVG at [`TRAY_ICON_SIZE`] into straight RGBA bytes.
pub fn estao_icon_rgba() -> Result<(Vec<u8>, u32, u32)> {
    let tree = resvg::usvg::Tree::from_str(ESTAO_ICON_SVG, &resvg::usvg::Options::default())
        .map_err(|error| anyhow!("failed to parse the Estao icon SVG: {error}"))?;

    let source = tree.size();
    if source.width() <= 0.0 || source.height() <= 0.0 {
        bail!(
            "the Estao icon has invalid dimensions ({}x{})",
            source.width(),
            source.height()
        );
    }

    let mut pixmap = resvg::tiny_skia::Pixmap::new(TRAY_ICON_SIZE, TRAY_ICON_SIZE)
        .context("failed to allocate the Estao icon bitmap")?;
    let transform = resvg::tiny_skia::Transform::from_scale(
        TRAY_ICON_SIZE as f32 / source.width(),
        TRAY_ICON_SIZE as f32 / source.height(),
    );
    resvg::render(&tree, transform, &mut pixmap.as_mut());

    Ok((pixmap.data().to_vec(), TRAY_ICON_SIZE, TRAY_ICON_SIZE))
}

/// Builds the `tray_icon::Icon` used by the Windows notification area.
pub fn tray_icon() -> Result<tray_icon::Icon> {
    let (rgba, width, height) = estao_icon_rgba()?;
    tray_icon::Icon::from_rgba(rgba, width, height).context("failed to create the Estao tray icon")
}

/// Locates the bundled SVG for a provider.
///
/// The packaged app looks next to the executable first; a development build
/// falls back to the repository's `assets/ProviderIcons` directory. Returns
/// `None` for unsupported providers or when no SVG can be found.
#[allow(dead_code)] // Consumed by the settings/popover/overlay waves.
pub fn provider_icon_path(id: &str) -> Option<PathBuf> {
    let id = catalog::normalize_id(id);
    if !catalog::is_supported(&id) {
        return None;
    }

    let file_name = format!("{id}.svg");
    let mut candidates = Vec::new();

    if let Ok(executable) = std::env::current_exe() {
        if let Some(directory) = executable.parent() {
            candidates.push(
                directory
                    .join("assets")
                    .join("ProviderIcons")
                    .join(&file_name),
            );
            candidates.push(directory.join("ProviderIcons").join(&file_name));
        }
    }

    candidates.push(
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../assets/ProviderIcons")
            .join(&file_name),
    );

    candidates.into_iter().find(|path| path.is_file())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rasterizes_the_tray_icon_at_the_expected_size() {
        let (rgba, width, height) = estao_icon_rgba().unwrap();
        assert_eq!((width, height), (TRAY_ICON_SIZE, TRAY_ICON_SIZE));
        assert_eq!(rgba.len(), (TRAY_ICON_SIZE * TRAY_ICON_SIZE * 4) as usize);
        // The icon has visible pixels; an all-transparent bitmap usually means
        // the render transform or the SVG failed silently.
        assert!(rgba.iter().skip(3).step_by(4).any(|&alpha| alpha != 0));
    }

    #[test]
    fn resolves_bundled_provider_icons() {
        for id in catalog::INITIAL_PROVIDER_IDS {
            assert!(
                provider_icon_path(id).is_some(),
                "missing provider icon for {id}"
            );
        }
        assert!(provider_icon_path("unknown-provider").is_none());
        assert!(provider_icon_path("github-copilot").is_some());
    }
}
