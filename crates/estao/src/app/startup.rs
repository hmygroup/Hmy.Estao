//! Windows startup registration (registry Run key).
//!
//! Wave 5 contract: `enable()`, `disable()`, `is_enabled()` against
//! `Software\Microsoft\Windows\CurrentVersion\Run` under HKCU, value name
//! "Estao", executable path = current exe. Ported from
//! `src/Hmy.Estao.App/Services/StartupManager.cs`.

use estao_core::constants::DISPLAY_NAME;
use windows_registry::CURRENT_USER;

/// The per-user Run key Windows starts at sign-in.
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

/// Registers the current executable in the user's Run key.
///
/// Returns `false` on any failure, mirroring `StartupManager.EnableStartup`.
pub fn enable() -> bool {
    let Ok(executable) = std::env::current_exe() else {
        return false;
    };

    let Ok(key) = CURRENT_USER.create(RUN_KEY) else {
        return false;
    };

    key.set_string(DISPLAY_NAME, executable.to_string_lossy())
        .is_ok()
}

/// Removes the Estao value from the user's Run key.
///
/// Returns `false` on any failure, mirroring `StartupManager.DisableStartup`.
pub fn disable() -> bool {
    let Ok(key) = CURRENT_USER.open(RUN_KEY) else {
        return false;
    };

    // The .NET implementation only deletes an existing value and treats a
    // missing value as success.
    if key.get_string(DISPLAY_NAME).is_err() {
        return true;
    }

    key.remove_value(DISPLAY_NAME).is_ok()
}

/// Whether Estao is currently registered to start at sign-in.
pub fn is_enabled() -> bool {
    CURRENT_USER
        .open(RUN_KEY)
        .ok()
        .and_then(|key| key.get_string(DISPLAY_NAME).ok())
        .is_some()
}
