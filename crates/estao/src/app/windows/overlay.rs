//! The taskbar usage overlay window.
//!
//! Wave 8 contract: a small borderless always-on-top no-activate window
//! painted over the taskbar's unused area: provider icons with percentage,
//! donut, and reset time; hides when the taskbar cannot accommodate it;
//! draggable with persisted position; hovering a provider opens the usage
//! popover for it. Win32 (Shell_TrayWnd/APPBAR geometry, SetWindowPos).
//! Ported from `src/Hmy.Estao.App/Controls/Zarpa/TaskbarUsageOverlay.cs`.
