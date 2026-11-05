//! Single-instance guard (named mutex).
//!
//! Wave 5 contract: a Win32 named mutex ("Estao-SingleInstance"); if
//! already held, the second launch focuses the existing instance (or simply
//! exits) instead of running a duplicate tray app.
