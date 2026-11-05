//! Single-instance guard (named mutex).
//!
//! Wave 5 contract: a Win32 named mutex ("Estao-SingleInstance"); if
//! already held, the second launch focuses the existing instance (or simply
//! exits) instead of running a duplicate tray app.

use std::ffi::c_void;

use windows::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE};

/// Name of the process-wide mutex shared by every Estao launch in the session.
const MUTEX_NAME: &str = "Estao-SingleInstance";

// `CreateMutexW` is declared here instead of using `windows::Win32::System::
// Threading::CreateMutexW` because that binding is gated behind the
// `Win32_Security` feature, which the crate intentionally does not enable.
#[allow(non_snake_case)]
#[link(name = "kernel32")]
extern "system" {
    fn CreateMutexW(
        lp_mutex_attributes: *const c_void,
        b_initial_owner: i32,
        lp_name: *const u16,
    ) -> *mut c_void;
}

/// Owns the named mutex for as long as the process runs.
///
/// Dropping it releases the mutex and allows a later launch to become the
/// primary instance again.
pub struct SingleInstance {
    handle: HANDLE,
}

impl SingleInstance {
    /// Acquires the Estao mutex.
    ///
    /// Returns `None` when another process already owns it (the second
    /// instance case) or when the mutex could not be created. Callers should
    /// print a short message and exit in both cases.
    pub fn acquire() -> Option<Self> {
        Self::acquire_named(MUTEX_NAME)
    }

    /// Acquires a mutex with an explicit name (used by tests to avoid
    /// colliding with a running Estao instance).
    fn acquire_named(name: &str) -> Option<Self> {
        let name: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();

        let raw = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if raw.is_null() {
            return None;
        }

        let handle = HANDLE(raw);
        let already_exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        if already_exists {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return None;
        }

        Some(Self { handle })
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.handle);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_second_acquire_is_rejected() {
        let name = format!("Estao-SingleInstance-test-{}", std::process::id());
        // The first guard stays alive for the duration of the test.
        let first = SingleInstance::acquire_named(&name).expect("the first launch owns the mutex");
        assert!(SingleInstance::acquire_named(&name).is_none());
        drop(first);
    }
}
