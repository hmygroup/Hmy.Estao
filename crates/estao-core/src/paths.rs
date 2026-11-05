//! Filesystem paths used by Estao, ported from `EstaoPaths.cs`.

use std::env;
use std::path::{Path, PathBuf};

use crate::constants;

/// Returns the path of the raw JSON config file.
///
/// The `HMY_ESTAO_CONFIG` environment variable overrides the default
/// `%APPDATA%\Hmy.Estao\config.json` location.
pub fn resolve_config_path() -> PathBuf {
    if let Some(override_path) = env::var_os(constants::CONFIG_ENVIRONMENT_VARIABLE) {
        let trimmed = override_path.to_string_lossy().trim().to_string();
        if !trimmed.is_empty() {
            return PathBuf::from(expand_home(&trimmed));
        }
    }

    let app_data = app_data_directory();
    app_data
        .join(constants::APP_DATA_FOLDER_NAME)
        .join(constants::CONFIG_FILE_NAME)
}

/// Returns the directory that holds the config and all other Estao data.
pub fn resolve_data_directory() -> PathBuf {
    resolve_config_path()
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| env::current_dir().unwrap_or_else(|_| PathBuf::from(".")))
}

/// Expands `~`, `~/`, `~\` and `%VAR%` references in `path`.
pub fn expand_home(path: &str) -> String {
    if path == "~" {
        return user_home().to_string_lossy().into_owned();
    }

    if let Some(rest) = path
        .strip_prefix("~/")
        .or_else(|| path.strip_prefix("~\\"))
    {
        return user_home()
            .join(rest)
            .to_string_lossy()
            .into_owned();
    }

    expand_environment_variables(path)
}

/// The current user's home directory.
pub fn user_home() -> PathBuf {
    env::var_os("USERPROFILE")
        .or_else(|| env::var_os("HOME"))
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
}

fn app_data_directory() -> PathBuf {
    if let Some(app_data) = env::var_os("APPDATA") {
        if !app_data.is_empty() {
            return PathBuf::from(app_data);
        }
    }

    user_home().join("AppData").join("Roaming")
}

/// Replaces `%VAR%` occurrences with their environment variable values,
/// mirroring `Environment.ExpandEnvironmentVariables`.
fn expand_environment_variables(path: &str) -> String {
    if !path.contains('%') {
        return path.to_string();
    }

    let mut result = String::with_capacity(path.len());
    let mut rest = path;
    while let Some(start) = rest.find('%') {
        result.push_str(&rest[..start]);
        let after_start = &rest[start + 1..];
        if let Some(end) = after_start.find('%') {
            let name = &after_start[..end];
            if name.is_empty() {
                result.push('%');
                rest = &after_start[end + 1..];
                continue;
            }

            match env::var(name) {
                Ok(value) => result.push_str(&value),
                Err(_) => {
                    result.push('%');
                    result.push_str(name);
                    result.push('%');
                }
            }
            rest = &after_start[end + 1..];
        } else {
            result.push_str(after_start);
            return result;
        }
    }

    result.push_str(rest);
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn expand_home_maps_bare_tilde_to_user_home() {
        assert_eq!(Path::new(&expand_home("~")), user_home());
    }

    #[test]
    fn expand_home_joins_tilde_slash_paths() {
        let expanded = PathBuf::from(expand_home("~/.codex"));
        assert_eq!(expanded, user_home().join(".codex"));
    }

    #[test]
    fn expand_home_keeps_plain_paths() {
        assert_eq!(expand_home("C:\\some\\path"), "C:\\some\\path");
    }

    #[test]
    fn expand_home_expands_environment_variables() {
        let expanded = expand_home("%USERPROFILE%\\.codex");
        assert_eq!(PathBuf::from(expanded), user_home().join(".codex"));
    }
}
