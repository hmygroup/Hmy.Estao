//! Window creation helpers shared by all Estao windows.
//!
//! Wave 5 contract: builders returning `gpui::WindowOptions` for the three
//! window kinds: a normal titled window (settings), a borderless
//! no-activate popup near an anchor point (usage popover), and a borderless
//! always-on-top no-activate popup pinned over the taskbar (overlay).
