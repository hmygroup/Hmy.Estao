# Controls Agent Guide

These rules apply under `Zarpa.Controls/Controls/` in addition to the repository guide.

## Architecture

- `DesignSystem/ZarpaDesignSystem.cs` owns theme tokens, recursive theme application, shared painting, paint-only animation, and size animation.
- `Ribbon/` is the owner-drawn ribbon and its design-time model. `Shell/` contains form chrome, navigation, bars, menus, and workspaces.
- `Inputs/` contains composite fields and internal editors. `Feedback/` contains state, progress, toast, and dialog controls. `Data/` contains the composite grid.
- `Zarpa.Demo/Demos/Suite/` is the primary integration example. Inspect only the matching demo usage when behavior or public API is unclear.

## Control Invariants

- Preserve `IZarpaThemeAware` behavior. Composite controls that own already-themed children may need `IZarpaThemeBoundary`; inspect neighboring controls before changing traversal.
- Preserve design-time metadata and serialization: `DefaultValue`, `Category`, `DesignerSerializationVisibility`, collection ownership, `TypeConverter`, and designer notifications are behavior, not decoration.
- Keep `OnPaint` allocation-light and dispose temporary `Pen`, `Brush`, `Font`, `GraphicsPath`, `Region`, and bitmap objects deterministically. Cache only when ownership and invalidation are clear.
- Invalidate the smallest practical bounds. Avoid `Refresh`, full-control invalidation, or layout from paint/input hot paths unless required.
- Preserve focus, keyboard activation, accessible name/role, enabled state, and high-contrast behavior when changing interaction or visuals.
- Disable motion when design-time detection applies or `Theme.MotionEnabled` is false. Dispose animation timers with the owning control.

## Animation Choice

- Paint-only state such as hover, press, toggle, badges, and indicators uses elapsed time, `float` state, coalesced UI callbacks, and targeted invalidation. `Ribbon/RibbonControl.cs` is the reference.
- Width, height, docked bounds, or other layout dimensions use the UI-thread `ZarpaSizeAnimator`; do not queue `BeginInvoke` frames for layout.
- Do not assign a dimension when its rounded pixel value is unchanged. Navigation and dock collapse transitions stay at least 240 ms and use ease-out unless the design explicitly requires otherwise.

## Verification

- A build alone is insufficient for owner-drawn changes. Launch the demo and inspect the affected state visually.
- For motion, verify a visible intermediate frame and the final frame. Confirm there is no direct jump and no disappearing icon, fill, or border.
- Use the project `winforms-visual-testing` skill for interactive visual verification.
