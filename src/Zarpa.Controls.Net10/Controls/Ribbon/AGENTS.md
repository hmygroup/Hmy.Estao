# Ribbon Agent Guide

## Context Selection

- Model or public API: read `RibbonElements.cs`, then `RibbonCollections.cs`.
- Metrics, palette, or theme bridge: read `RibbonAppearance.cs` and `RibbonTheme.cs`.
- Layout, input, painting, overflow, or animation: read only the relevant regions of `RibbonControl.cs`; do not preload the entire file without need.
- Hosted inputs or menus: read the matching `RibbonInputItems.cs`, `RibbonMenuItems.cs`, and adapter in `RibbonModernHostedControls.cs`.
- Visual Studio designer behavior: read the matching file under `Design/` and preserve `IComponentChangeService` notifications and serialization semantics.

## Invariants

- Keep the ownership chain `RibbonTab -> RibbonGroup -> RibbonItem` synchronized when collections mutate.
- Public item properties must trigger the appropriate owner layout or repaint without introducing broad invalidation.
- Hosted controls must remain synchronized with item state, bounds, visibility, enabled state, theme, and disposal.
- Reuse the existing animation clock for paint-only state; do not create a competing per-item timer.
- Verify narrow widths, overflow, keyboard/mouse activation, active-tab changes, and motion-disabled themes when those paths are affected.
