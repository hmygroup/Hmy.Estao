//! Windows startup registration (registry Run key).
//!
//! Wave 5 contract: `enable()`, `disable()`, `is_enabled()` against
//! `Software\Microsoft\Windows\CurrentVersion\Run` under HKCU, value name
//! "Estao", executable path = current exe. Ported from
//! `src/Hmy.Estao.App/Services/StartupManager.cs`.
