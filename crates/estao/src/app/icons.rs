//! Asset icons: the Estao tray icon and provider icons.
//!
//! Wave 5 contract: rasterize `assets/estao-icon.svg` with resvg into an
//! RGBA buffer for `tray_icon::Icon`. Later waves expose provider SVGs
//! (`assets/ProviderIcons/*.svg`) for in-window rendering.

use std::path::PathBuf;
use std::sync::Arc;

use anyhow::{anyhow, bail, Context, Result};
use estao_core::catalog;
use gpui::RenderImage;

/// The SVG embedded in the binary so the tray icon works from any directory.
const ESTAO_ICON_SVG: &str = include_str!("../../../../assets/estao-icon.svg");

/// Edge length of the tray icon, in physical pixels.
pub const TRAY_ICON_SIZE: u32 = 32;

/// Edge length of an in-window provider icon at 1x, in physical pixels.
///
/// Callers rasterize larger than the displayed logical size so the icon stays
/// crisp on high-DPI displays.
pub const PROVIDER_ICON_SIZE: u32 = 32;

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

/// Rasterizes a bundled provider SVG into straight (premultiplied) RGBA bytes.
///
/// Returns `Ok(None)` when the provider has no bundled icon. The image is
/// scaled uniformly and centered, so providers with non-square view boxes
/// (for example OpenCode's 54x42 mark) keep their aspect ratio.
pub fn provider_icon_rgba(id: &str, size: u32) -> Result<Option<(Vec<u8>, u32, u32)>> {
    let Some(path) = provider_icon_path(id) else {
        return Ok(None);
    };

    let data = std::fs::read(&path)
        .with_context(|| format!("failed to read the provider icon '{}'", path.display()))?;
    let svg = String::from_utf8(data)
        .with_context(|| format!("the provider icon '{}' is not UTF-8", path.display()))?;
    let tree = resvg::usvg::Tree::from_str(&svg, &resvg::usvg::Options::default())
        .with_context(|| format!("failed to parse the provider icon '{}'", path.display()))?;

    let source = tree.size();
    if source.width() <= 0.0 || source.height() <= 0.0 {
        bail!(
            "the provider icon '{}' has invalid dimensions ({}x{})",
            path.display(),
            source.width(),
            source.height()
        );
    }

    let size = size.max(1);
    let mut pixmap = resvg::tiny_skia::Pixmap::new(size, size)
        .with_context(|| format!("failed to allocate the provider icon '{}'", path.display()))?;
    let scale = (size as f32 / source.width()).min(size as f32 / source.height());
    let dx = (size as f32 - source.width() * scale) / 2.0;
    let dy = (size as f32 - source.height() * scale) / 2.0;
    let transform = resvg::tiny_skia::Transform::from_row(scale, 0.0, 0.0, scale, dx, dy);
    resvg::render(&tree, transform, &mut pixmap.as_mut());

    Ok(Some((pixmap.data().to_vec(), size, size)))
}

/// Rasterizes a provider icon into a gpui image for [`gpui::img`].
///
/// `tray_icon::Icon` and `gpui::RenderImage` share the same pixel data, but
/// gpui expects BGRA byte order, so the red and blue channels are swapped
/// after rasterizing. Returns `None` for providers without a bundled icon.
pub fn provider_icon_image(id: &str, size: u32) -> Option<Arc<RenderImage>> {
    let (rgba, width, height) = provider_icon_rgba(id, size).ok().flatten()?;

    let mut bgra = rgba;
    let (pixels, _) = bgra.as_chunks_mut::<4>();
    for pixel in pixels {
        pixel.swap(0, 2);
    }
    let buffer = image::ImageBuffer::from_raw(width, height, bgra)?;
    Some(Arc::new(RenderImage::new(vec![image::Frame::new(buffer)])))
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

    #[test]
    fn rasterizes_provider_icons_into_gpui_images() {
        for id in catalog::INITIAL_PROVIDER_IDS {
            let (rgba, width, height) = provider_icon_rgba(id, PROVIDER_ICON_SIZE)
                .unwrap()
                .unwrap_or_else(|| panic!("missing provider icon for {id}"));
            assert_eq!((width, height), (PROVIDER_ICON_SIZE, PROVIDER_ICON_SIZE));
            assert_eq!(
                rgba.len(),
                (PROVIDER_ICON_SIZE * PROVIDER_ICON_SIZE * 4) as usize
            );
            assert!(
                rgba.iter().skip(3).step_by(4).any(|&alpha| alpha != 0),
                "{id} rasterized to an empty bitmap"
            );

            let image = provider_icon_image(id, PROVIDER_ICON_SIZE)
                .unwrap_or_else(|| panic!("missing gpui image for {id}"));
            assert_eq!(image.frame_count(), 1);
            assert_eq!(
                image.size(0),
                gpui::size(
                    gpui::DevicePixels(PROVIDER_ICON_SIZE as i32),
                    gpui::DevicePixels(PROVIDER_ICON_SIZE as i32)
                )
            );
        }

        assert!(provider_icon_rgba("unknown-provider", PROVIDER_ICON_SIZE)
            .unwrap()
            .is_none());
        assert!(provider_icon_image("unknown-provider", PROVIDER_ICON_SIZE).is_none());
    }
}
