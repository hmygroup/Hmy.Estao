//! The Estao settings window.
//!
//! Wave 7 contract: a normal titled 720x640 window with a scrollable body and a
//! fixed footer. Ported from `src/Hmy.Estao.App/SettingsForm.cs`, dropping the
//! v1-only harness-manager, taskbar-overlay-customization and backdrop pages.
//!
//! The window keeps an editable copy of the loaded `EstaoConfig`. Provider
//! credentials use the same secure stores as the CLI: API keys live in the
//! config, while cookie headers go through `SecureCookieStore` and the Copilot
//! token goes through `SecureOAuthTokenStore`. Saving validates the editable
//! fields, persists through `ConfigStore`, and updates [`AppState`] so the tray
//! and popover observe the new config immediately.

use std::ops::Range;
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::sync::Arc;
use std::time::Duration;

use estao_core::catalog::{self, INITIAL_PROVIDER_IDS};
use estao_core::config::{
    ConfigStore, ConfigValidation, EstaoConfig, PacingCatalog, ProviderConfig,
    RefreshIntervalCatalog, UsageColorCatalog, UsageColorConfig,
};
use estao_core::models::ProviderAccount;
use estao_core::providers::UsageProviderFactory;
use estao_core::security::SecureCookieStore;
use gpui::{
    canvas, div, prelude::*, px, utf16_to_utf8_offset, utf8_to_utf16_offset, App, Bounds, Context,
    Div, ElementInputHandler, Entity, EntityInputHandler, FocusHandle, Focusable, FontWeight,
    KeyDownEvent, Pixels, Point, Render, SharedString, Stateful, UTF16Selection, Window,
};

use crate::app::oauth_login;
use crate::app::startup;
use crate::app::state::AppState;
use crate::app::theme::{Palette, Theme};

/// The settings window client size.
pub const SETTINGS_SIZE: (f32, f32) = (720.0, 640.0);

/// The provider kinds that support a one-click sign-in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SignInKind {
    Codex,
    Claude,
    Copilot,
}

impl SignInKind {
    fn from_provider(id: &str) -> Option<Self> {
        match catalog::normalize_id(id).as_str() {
            "codex" => Some(Self::Codex),
            "claude" => Some(Self::Claude),
            "copilot" => Some(Self::Copilot),
            _ => None,
        }
    }

    fn start_message(self) -> &'static str {
        match self {
            Self::Codex => "Opening Codex sign-in…",
            Self::Claude => "Opening Claude Code sign-in…",
            Self::Copilot => oauth_login::REQUESTING_DEVICE_CODE,
        }
    }

    fn finish_message(self) -> &'static str {
        match self {
            Self::Codex => "Finish Codex sign-in in the terminal, then refresh usage.",
            Self::Claude => "Finish Claude sign-in in the terminal, then refresh usage.",
            Self::Copilot => "Copilot connected. Refresh usage now.",
        }
    }
}

/// Whether the provider card shows an API-key editor (mirrors the .NET rows).
fn provider_uses_api_key(id: &str) -> bool {
    matches!(
        catalog::normalize_id(id).as_str(),
        "claude" | "copilot" | "zai"
    )
}

/// Whether the provider card shows a cookie editor (mirrors the .NET rows).
fn provider_uses_cookie(id: &str) -> bool {
    matches!(catalog::normalize_id(id).as_str(), "claude" | "opencode")
}

/// Resolves the zero-based account selection against the discovered accounts.
///
/// An out-of-range or negative index falls back to the first account, exactly
/// like `UsageRefreshService`'s `select_account`.
fn selected_account_index(count: usize, active: Option<i32>) -> Option<usize> {
    if count == 0 {
        return None;
    }

    let index = active.unwrap_or(0);
    if index >= 0 && (index as usize) < count {
        Some(index as usize)
    } else {
        Some(0)
    }
}

/// Formats a percentage without a trailing `.0`.
fn format_percent_value(value: f64) -> String {
    let rounded = (value * 10.0).round() / 10.0;
    if (rounded - rounded.trunc()).abs() < f64::EPSILON {
        format!("{rounded:.0}")
    } else {
        format!("{rounded:.1}")
    }
}

/// Parses a percentage field, accepting `0..=100`.
fn parse_percent(value: &str) -> Result<f64, String> {
    let value = value.trim();
    if value.is_empty() {
        return Err("enter a percentage".to_string());
    }

    let parsed: f64 = value
        .parse()
        .map_err(|_| "enter a number between 0 and 100".to_string())?;
    if !parsed.is_finite() || !(0.0..=100.0).contains(&parsed) {
        return Err("enter a number between 0 and 100".to_string());
    }

    Ok(parsed)
}

/// Normalizes a color field and reports the .NET `#RRGGBB` rule on failure.
fn normalize_color_field(value: &str) -> Result<String, String> {
    let color = value.trim();
    if color.len() == 7
        && color.starts_with('#')
        && color[1..]
            .chars()
            .all(|character| character.is_ascii_hexdigit())
    {
        Ok(color.to_ascii_uppercase())
    } else {
        Err("must be a #RRGGBB hex value".to_string())
    }
}

/// Applies the four usage-color editor fields to `colors`.
///
/// Returns one human-readable error per invalid field. Values that fail
/// validation keep their previous config value.
fn apply_usage_colors(
    display_name: &str,
    colors: &mut UsageColorConfig,
    warning_percent: &str,
    warning_color: &str,
    critical_percent: &str,
    critical_color: &str,
) -> Vec<String> {
    let mut errors = Vec::new();

    let warning = match parse_percent(warning_percent) {
        Ok(value) if value <= UsageColorCatalog::MAXIMUM_WARNING_PERCENT => {
            colors.warning_percent = value;
            Some(value)
        }
        Ok(_) => {
            errors.push(format!(
                "{display_name}: warning percent must be between 0 and {}.",
                UsageColorCatalog::MAXIMUM_WARNING_PERCENT
            ));
            None
        }
        Err(error) => {
            errors.push(format!("{display_name}: warning percent {error}."));
            None
        }
    };

    let critical = match parse_percent(critical_percent) {
        Ok(value) => {
            colors.critical_percent = value;
            Some(value)
        }
        Err(error) => {
            errors.push(format!("{display_name}: critical percent {error}."));
            None
        }
    };

    if let (Some(warning), Some(critical)) = (warning, critical) {
        if critical <= warning {
            errors.push(format!(
                "{display_name}: critical percent must be greater than the warning percent."
            ));
        }
    }

    match normalize_color_field(warning_color) {
        Ok(color) => colors.warning_color = color,
        Err(error) => errors.push(format!("{display_name}: warning color {error}.")),
    }

    match normalize_color_field(critical_color) {
        Ok(color) => colors.critical_color = color,
        Err(error) => errors.push(format!("{display_name}: critical color {error}.")),
    }

    errors
}

/// A single-line editable text field backed by an `Entity`.
///
/// GPUI 0.3 does not ship a text-input widget, so this view implements
/// [`EntityInputHandler`] and installs it during paint. Editing keys
/// (backspace/delete/arrows/home/end) are handled directly; typed characters,
/// paste and IME composition arrive through the platform input handler.
struct TextField {
    id: SharedString,
    focus_handle: FocusHandle,
    text: String,
    cursor: usize,
    placeholder: SharedString,
    masked: bool,
    palette: Palette,
}

impl TextField {
    fn new(
        id: impl Into<SharedString>,
        text: impl Into<String>,
        placeholder: impl Into<SharedString>,
        masked: bool,
        palette: Palette,
        cx: &mut Context<Self>,
    ) -> Self {
        let text = text.into();
        let cursor = text.len();
        Self {
            id: id.into(),
            focus_handle: cx.focus_handle(),
            text,
            cursor,
            placeholder: placeholder.into(),
            masked,
            palette,
        }
    }

    fn text(&self) -> &str {
        &self.text
    }

    fn emplace(&mut self, text: impl Into<String>, cx: &mut Context<Self>) {
        self.text = text.into();
        self.cursor = self.text.len();
        cx.notify();
    }

    fn set_palette(&mut self, palette: Palette, cx: &mut Context<Self>) {
        self.palette = palette;
        cx.notify();
    }

    fn previous_boundary(&self, index: usize) -> usize {
        self.text[..index]
            .char_indices()
            .last()
            .map(|(offset, _)| offset)
            .unwrap_or(0)
    }

    fn next_boundary(&self, index: usize) -> usize {
        self.text[index..]
            .char_indices()
            .nth(1)
            .map(|(offset, _)| index + offset)
            .unwrap_or(self.text.len())
    }

    fn handle_key(&mut self, event: &KeyDownEvent, cx: &mut Context<Self>) {
        match event.keystroke.key.as_str() {
            "backspace" => {
                if self.cursor > 0 {
                    let start = self.previous_boundary(self.cursor);
                    self.text.replace_range(start..self.cursor, "");
                    self.cursor = start;
                    cx.notify();
                }
            }
            "delete" => {
                if self.cursor < self.text.len() {
                    let end = self.next_boundary(self.cursor);
                    self.text.replace_range(self.cursor..end, "");
                    cx.notify();
                }
            }
            "left" => {
                self.cursor = self.previous_boundary(self.cursor);
                cx.notify();
            }
            "right" => {
                self.cursor = self.next_boundary(self.cursor);
                cx.notify();
            }
            "home" => {
                self.cursor = 0;
                cx.notify();
            }
            "end" => {
                self.cursor = self.text.len();
                cx.notify();
            }
            _ => {}
        }
    }
}

impl EntityInputHandler for TextField {
    fn text_for_range(
        &mut self,
        range: Range<usize>,
        adjusted_range: &mut Option<Range<usize>>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<String> {
        *adjusted_range = Some(range.clone());
        let start = utf16_to_utf8_offset(&self.text, range.start);
        let end = utf16_to_utf8_offset(&self.text, range.end);
        self.text.get(start..end).map(str::to_string)
    }

    fn selected_text_range(
        &mut self,
        _ignore_disabled_input: bool,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<UTF16Selection> {
        let cursor = utf8_to_utf16_offset(&self.text, self.cursor);
        Some(UTF16Selection {
            range: cursor..cursor,
            reversed: false,
        })
    }

    fn marked_text_range(
        &self,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Range<usize>> {
        None
    }

    fn unmark_text(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {}

    fn replace_text_in_range(
        &mut self,
        range: Option<Range<usize>>,
        text: &str,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let range = match range {
            Some(range) => {
                utf16_to_utf8_offset(&self.text, range.start)
                    ..utf16_to_utf8_offset(&self.text, range.end)
            }
            None => self.cursor..self.cursor,
        };

        self.text.replace_range(range.clone(), text);
        self.cursor = range.start + text.len();
        cx.notify();
    }

    fn replace_and_mark_text_in_range(
        &mut self,
        range: Option<Range<usize>>,
        new_text: &str,
        _new_selected_range: Option<Range<usize>>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.replace_text_in_range(range, new_text, window, cx);
    }

    fn bounds_for_range(
        &mut self,
        _range_utf16: Range<usize>,
        element_bounds: Bounds<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Bounds<Pixels>> {
        Some(element_bounds)
    }

    fn character_index_for_point(
        &mut self,
        _point: Point<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<usize> {
        Some(utf8_to_utf16_offset(&self.text, self.text.len()))
    }

    fn text_length_utf16(
        &mut self,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<usize> {
        Some(utf8_to_utf16_offset(&self.text, self.text.len()))
    }
}

impl Focusable for TextField {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for TextField {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let focused = self.focus_handle.is_focused(window);
        let empty = self.text.is_empty();
        let display = if self.masked {
            "•".repeat(self.text.chars().count())
        } else {
            self.text.clone()
        };
        let label = if empty {
            self.placeholder.clone()
        } else {
            SharedString::from(display)
        };

        let view = cx.entity();
        let focus_handle = self.focus_handle.clone();

        div()
            .id(self.id.clone())
            .relative()
            .w_full()
            .h(px(26.0))
            .flex()
            .items_center()
            .px(px(6.0))
            .rounded_md()
            .bg(palette.surface)
            .border_1()
            .border_color(if focused {
                palette.accent
            } else {
                palette.border
            })
            .text_size(px(11.0))
            .text_color(if empty {
                palette.text_muted
            } else {
                palette.text
            })
            .cursor_text()
            .track_focus(&self.focus_handle)
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .overflow_hidden()
                    .whitespace_nowrap()
                    .child(label),
            )
            .child(
                canvas(
                    |_, _, _| {},
                    move |bounds, _, window, cx| {
                        window.handle_input(
                            &focus_handle,
                            ElementInputHandler::new(bounds, view.clone()),
                            cx,
                        );
                    },
                )
                .absolute()
                .top_0()
                .left_0()
                .size_full(),
            )
            .on_click(cx.listener(|this, _, window, cx| {
                window.focus(&this.focus_handle, cx);
            }))
            .on_key_down(cx.listener(|this, event: &KeyDownEvent, _window, cx| {
                this.handle_key(event, cx);
            }))
    }
}

/// One provider card's editable widgets and transient status.
struct ProviderFields {
    id: String,
    accounts: Vec<ProviderAccount>,
    api_key: Option<Entity<TextField>>,
    cookie: Option<Entity<TextField>>,
    warning_percent: Entity<TextField>,
    warning_color: Entity<TextField>,
    critical_percent: Entity<TextField>,
    critical_color: Entity<TextField>,
    cookie_status: String,
    progress: Option<String>,
    feedback: Option<(String, bool)>,
    signing_in: bool,
}

impl ProviderFields {
    fn all_fields(&self) -> Vec<&Entity<TextField>> {
        let mut fields = vec![
            &self.warning_percent,
            &self.warning_color,
            &self.critical_percent,
            &self.critical_color,
        ];
        if let Some(field) = &self.api_key {
            fields.push(field);
        }
        if let Some(field) = &self.cookie {
            fields.push(field);
        }
        fields
    }
}

/// A progress/result update from a background sign-in thread.
enum SignInUpdate {
    Progress(String),
    Copilot(Result<estao_core::security::OAuthToken, String>),
    Terminal(Result<(), String>),
    Disconnected,
}

/// The real settings window view.
pub struct SettingsWindow {
    state: Entity<AppState>,
    store: Arc<ConfigStore>,
    config: EstaoConfig,
    palette: Palette,
    launch_at_signin: bool,
    provider_fields: Vec<ProviderFields>,
    open_menu: Option<String>,
    errors: Vec<String>,
    status: Option<(String, bool)>,
}

impl SettingsWindow {
    /// Builds the settings view around a freshly loaded configuration.
    pub fn new(
        state: Entity<AppState>,
        store: Arc<ConfigStore>,
        config: EstaoConfig,
        palette: Palette,
        cx: &mut Context<Self>,
    ) -> Self {
        let config = ConfigStore::normalize(config);
        let launch_at_signin = startup::is_enabled();
        let factory = UsageProviderFactory::new();
        let cookie_store = SecureCookieStore::new();

        let mut provider_fields = Vec::new();
        for id in INITIAL_PROVIDER_IDS {
            let provider = config
                .providers
                .iter()
                .find(|provider| catalog::normalize_id(&provider.id) == id)
                .cloned()
                .unwrap_or_else(|| ProviderConfig {
                    id: id.to_string(),
                    ..ProviderConfig::default()
                });

            let accounts = factory
                .create(id)
                .map(|provider_handle| provider_handle.accounts(&provider))
                .unwrap_or_default();

            let api_key = provider_uses_api_key(id).then(|| {
                let value = provider.api_key.clone().unwrap_or_default();
                cx.new(|cx| {
                    TextField::new(
                        format!("estao-{id}-api-key"),
                        value,
                        "API key or token",
                        true,
                        palette,
                        cx,
                    )
                })
            });

            let cookie = provider_uses_cookie(id).then(|| {
                cx.new(|cx| {
                    TextField::new(
                        format!("estao-{id}-cookie"),
                        String::new(),
                        "New cookie header",
                        true,
                        palette,
                        cx,
                    )
                })
            });

            let warning_percent = cx.new(|cx| {
                TextField::new(
                    format!("estao-{id}-warning-percent"),
                    format_percent_value(provider.usage_colors.warning_percent),
                    "75",
                    false,
                    palette,
                    cx,
                )
            });
            let warning_color = cx.new(|cx| {
                TextField::new(
                    format!("estao-{id}-warning-color"),
                    provider.usage_colors.warning_color.clone(),
                    "#F59E0B",
                    false,
                    palette,
                    cx,
                )
            });
            let critical_percent = cx.new(|cx| {
                TextField::new(
                    format!("estao-{id}-critical-percent"),
                    format_percent_value(provider.usage_colors.critical_percent),
                    "90",
                    false,
                    palette,
                    cx,
                )
            });
            let critical_color = cx.new(|cx| {
                TextField::new(
                    format!("estao-{id}-critical-color"),
                    provider.usage_colors.critical_color.clone(),
                    "#EF4444",
                    false,
                    palette,
                    cx,
                )
            });

            provider_fields.push(ProviderFields {
                id: id.to_string(),
                accounts,
                api_key,
                cookie,
                warning_percent,
                warning_color,
                critical_percent,
                critical_color,
                cookie_status: cookie_status(&cookie_store, id, &provider),
                progress: None,
                feedback: None,
                signing_in: false,
            });
        }

        Self {
            state,
            store,
            config,
            palette,
            launch_at_signin,
            provider_fields,
            open_menu: None,
            errors: Vec::new(),
            status: None,
        }
    }

    fn provider_config(&self, id: &str) -> Option<&ProviderConfig> {
        self.config
            .providers
            .iter()
            .find(|provider| catalog::normalize_id(&provider.id) == id)
    }

    fn provider_config_mut(&mut self, id: &str) -> Option<&mut ProviderConfig> {
        self.config
            .providers
            .iter_mut()
            .find(|provider| catalog::normalize_id(&provider.id) == id)
    }

    fn toggle_menu(&mut self, id: &str) {
        self.open_menu = if self.open_menu.as_deref() == Some(id) {
            None
        } else {
            Some(id.to_string())
        };
    }

    fn preview_theme(&mut self, name: &str, cx: &mut Context<Self>) {
        self.config.theme = name.to_string();
        self.palette = Theme::from_name(name).palette();
        self.update_field_palettes(cx);
        cx.notify();
    }

    fn update_field_palettes(&self, cx: &mut Context<Self>) {
        let palette = self.palette;
        for fields in &self.provider_fields {
            for field in fields.all_fields() {
                field.update(cx, |field, cx| field.set_palette(palette, cx));
            }
        }
    }

    fn sync_fields_from_config(&mut self, cx: &mut Context<Self>) {
        for fields in &mut self.provider_fields {
            let Some(provider) = self
                .config
                .providers
                .iter()
                .find(|provider| catalog::normalize_id(&provider.id) == fields.id)
                .cloned()
            else {
                continue;
            };

            if let Some(field) = &fields.api_key {
                let value = provider.api_key.clone().unwrap_or_default();
                field.update(cx, |field, cx| field.emplace(value, cx));
            }
            if let Some(field) = &fields.cookie {
                field.update(cx, |field, cx| field.emplace(String::new(), cx));
            }
            fields.warning_percent.update(cx, |field, cx| {
                field.emplace(
                    format_percent_value(provider.usage_colors.warning_percent),
                    cx,
                );
            });
            fields.warning_color.update(cx, |field, cx| {
                field.emplace(provider.usage_colors.warning_color.clone(), cx);
            });
            fields.critical_percent.update(cx, |field, cx| {
                field.emplace(
                    format_percent_value(provider.usage_colors.critical_percent),
                    cx,
                );
            });
            fields.critical_color.update(cx, |field, cx| {
                field.emplace(provider.usage_colors.critical_color.clone(), cx);
            });
        }
    }

    fn refresh_accounts(&mut self, index: usize) {
        let Some(fields) = self.provider_fields.get(index) else {
            return;
        };
        let id = fields.id.clone();
        let Some(provider) = self.provider_config(&id).cloned() else {
            return;
        };
        let factory = UsageProviderFactory::new();
        let accounts = factory
            .create(&id)
            .map(|provider_handle| provider_handle.accounts(&provider))
            .unwrap_or_default();
        self.provider_fields[index].accounts = accounts;
    }

    fn refresh_cookie_status(&mut self, index: usize) {
        let Some(fields) = self.provider_fields.get(index) else {
            return;
        };
        let id = fields.id.clone();
        let Some(provider) = self.provider_config(&id) else {
            return;
        };
        let status = cookie_status(&SecureCookieStore::new(), &id, provider);
        self.provider_fields[index].cookie_status = status;
    }

    fn set_feedback(
        &mut self,
        index: usize,
        message: impl Into<String>,
        is_error: bool,
        cx: &mut Context<Self>,
    ) {
        if let Some(fields) = self.provider_fields.get_mut(index) {
            fields.feedback = Some((message.into(), is_error));
        }
        cx.notify();
    }

    /// Persists one provider-level change without discarding other unsaved
    /// edits from the window's copy.
    fn mutate_provider<F>(
        &mut self,
        provider_id: &str,
        mutate: F,
        cx: &mut Context<Self>,
    ) -> Result<(), String>
    where
        F: Fn(&mut ProviderConfig),
    {
        let mut disk = self.store.load().map_err(|error| format!("{error:#}"))?;
        let Some(provider) = disk
            .providers
            .iter_mut()
            .find(|provider| catalog::normalize_id(&provider.id) == provider_id)
        else {
            return Err(format!("provider '{provider_id}' is not in the config"));
        };
        mutate(provider);

        self.state
            .update(cx, |state, cx| state.replace_config(disk, cx))
            .map_err(|error| format!("{error:#}"))?;

        if let Some(provider) = self
            .config
            .providers
            .iter_mut()
            .find(|provider| catalog::normalize_id(&provider.id) == provider_id)
        {
            mutate(provider);
        }

        Ok(())
    }

    fn save_api_key(&mut self, index: usize, cx: &mut Context<Self>) {
        let Some(field) = self
            .provider_fields
            .get(index)
            .and_then(|fields| fields.api_key.clone())
        else {
            return;
        };
        let id = self.provider_fields[index].id.clone();
        let value = field.read(cx).text().trim().to_string();
        if value.is_empty() {
            self.set_feedback(index, "Enter an API key first.", true, cx);
            return;
        }

        match self.mutate_provider(&id, |provider| provider.api_key = Some(value.clone()), cx) {
            Ok(()) => {
                field.update(cx, |field, cx| field.emplace(value, cx));
                self.set_feedback(index, "Saved", false, cx);
            }
            Err(error) => self.set_feedback(
                index,
                format!("Could not save the API key: {error}"),
                true,
                cx,
            ),
        }
    }

    fn clear_api_key(&mut self, index: usize, cx: &mut Context<Self>) {
        let Some(field) = self
            .provider_fields
            .get(index)
            .and_then(|fields| fields.api_key.clone())
        else {
            return;
        };
        let id = self.provider_fields[index].id.clone();

        match self.mutate_provider(&id, |provider| provider.api_key = None, cx) {
            Ok(()) => {
                field.update(cx, |field, cx| field.emplace(String::new(), cx));
                self.set_feedback(index, "Cleared", false, cx);
            }
            Err(error) => self.set_feedback(
                index,
                format!("Could not clear the API key: {error}"),
                true,
                cx,
            ),
        }
    }

    fn save_cookie(&mut self, index: usize, cx: &mut Context<Self>) {
        let Some(field) = self
            .provider_fields
            .get(index)
            .and_then(|fields| fields.cookie.clone())
        else {
            return;
        };
        let id = self.provider_fields[index].id.clone();
        let value = field.read(cx).text().trim().to_string();
        if value.is_empty() {
            self.set_feedback(index, "Enter a cookie header first.", true, cx);
            return;
        }

        let store = SecureCookieStore::new();
        if let Err(error) = store.save_cookie_header(&id, &value) {
            self.set_feedback(
                index,
                format!("Could not save the cookie: {error:#}"),
                true,
                cx,
            );
            return;
        }

        match self.mutate_provider(
            &id,
            |provider| {
                provider.cookie_source = Some("manual".to_string());
                provider.cookie_header = None;
            },
            cx,
        ) {
            Ok(()) => {
                field.update(cx, |field, cx| field.emplace(String::new(), cx));
                self.refresh_cookie_status(index);
                self.set_feedback(index, "Saved", false, cx);
            }
            Err(error) => self.set_feedback(
                index,
                format!("Cookie saved, but the config was not: {error}"),
                true,
                cx,
            ),
        }
    }

    fn clear_cookie(&mut self, index: usize, cx: &mut Context<Self>) {
        let id = self.provider_fields[index].id.clone();

        if let Err(error) = SecureCookieStore::new().clear_cookie_header(&id) {
            self.set_feedback(
                index,
                format!("Could not clear the cookie: {error:#}"),
                true,
                cx,
            );
            return;
        }

        match self.mutate_provider(&id, |provider| provider.cookie_header = None, cx) {
            Ok(()) => {
                self.refresh_cookie_status(index);
                self.set_feedback(index, "Cleared", false, cx);
            }
            Err(error) => self.set_feedback(
                index,
                format!("Cookie cleared, but the config was not: {error}"),
                true,
                cx,
            ),
        }
    }

    fn start_sign_in(&mut self, index: usize, cx: &mut Context<Self>) {
        if self
            .provider_fields
            .get(index)
            .is_none_or(|fields| fields.signing_in)
        {
            return;
        }

        let id = self.provider_fields[index].id.clone();
        let Some(kind) = SignInKind::from_provider(&id) else {
            return;
        };

        self.provider_fields[index].signing_in = true;
        self.provider_fields[index].feedback = None;
        self.provider_fields[index].progress = Some(kind.start_message().to_string());
        cx.notify();

        let (tx, rx) = mpsc::channel();
        match kind {
            SignInKind::Copilot => {
                let progress = tx.clone();
                let finished = tx;
                let _ = oauth_login::start_copilot_sign_in(
                    move |message| {
                        let _ = progress.send(SignInUpdate::Progress(message.to_string()));
                    },
                    move |result| {
                        let _ = finished.send(SignInUpdate::Copilot(result));
                    },
                );
            }
            SignInKind::Codex => {
                std::thread::spawn(move || {
                    let result = oauth_login::open_terminal_login("codex login")
                        .map_err(|error| format!("{error:#}"));
                    let _ = tx.send(SignInUpdate::Terminal(result));
                });
            }
            SignInKind::Claude => {
                std::thread::spawn(move || {
                    let result = oauth_login::open_terminal_login("claude login")
                        .map_err(|error| format!("{error:#}"));
                    let _ = tx.send(SignInUpdate::Terminal(result));
                });
            }
        }

        self.poll_sign_in(index, rx, cx);
    }

    fn poll_sign_in(&self, index: usize, rx: Receiver<SignInUpdate>, cx: &mut Context<Self>) {
        cx.spawn(async move |this, cx| loop {
            let mut updates = Vec::new();
            let mut disconnected = false;
            loop {
                match rx.try_recv() {
                    Ok(update) => updates.push(update),
                    Err(TryRecvError::Empty) => break,
                    Err(TryRecvError::Disconnected) => {
                        disconnected = true;
                        break;
                    }
                }
            }

            let mut stop = disconnected;
            cx.update(|cx| {
                if let Some(this) = this.upgrade() {
                    this.update(cx, |this, cx| {
                        for update in updates {
                            this.apply_sign_in_update(index, update, cx);
                        }
                        if disconnected
                            && this
                                .provider_fields
                                .get(index)
                                .is_some_and(|fields| fields.signing_in)
                        {
                            this.apply_sign_in_update(index, SignInUpdate::Disconnected, cx);
                        }
                        if this
                            .provider_fields
                            .get(index)
                            .is_some_and(|fields| !fields.signing_in)
                        {
                            stop = true;
                        }
                    });
                }
            });

            if stop {
                break;
            }

            cx.background_executor()
                .timer(Duration::from_millis(100))
                .await;
        })
        .detach();
    }

    fn apply_sign_in_update(&mut self, index: usize, update: SignInUpdate, cx: &mut Context<Self>) {
        let Some(id) = self
            .provider_fields
            .get(index)
            .map(|fields| fields.id.clone())
        else {
            return;
        };

        match update {
            SignInUpdate::Progress(message) => {
                if let Some(fields) = self.provider_fields.get_mut(index) {
                    fields.progress = Some(message);
                }
                cx.notify();
            }
            SignInUpdate::Copilot(Ok(token)) => {
                let label = token.account_label.clone();
                if let Some(fields) = self.provider_fields.get_mut(index) {
                    fields.signing_in = false;
                    fields.progress = Some(SignInKind::Copilot.finish_message().to_string());
                    fields.feedback = Some(match label {
                        Some(label) => (format!("Connected as {label}"), false),
                        None => ("Connected".to_string(), false),
                    });
                }
                let _ = self.enable_provider(&id, "oauth", cx);
                self.refresh_accounts(index);
                cx.notify();
            }
            SignInUpdate::Copilot(Err(message)) => {
                if let Some(fields) = self.provider_fields.get_mut(index) {
                    fields.signing_in = false;
                    fields.progress = Some(format!("Sign-in failed: {message}"));
                    fields.feedback = Some((format!("Sign-in failed: {message}"), true));
                }
                cx.notify();
            }
            SignInUpdate::Terminal(Ok(())) => {
                if let Some(kind) = SignInKind::from_provider(&id) {
                    if let Some(fields) = self.provider_fields.get_mut(index) {
                        fields.signing_in = false;
                        fields.progress = Some(kind.finish_message().to_string());
                        fields.feedback = Some(("Terminal opened.".to_string(), false));
                    }
                }
                let _ = self.enable_provider(&id, "auto", cx);
                cx.notify();
            }
            SignInUpdate::Terminal(Err(message)) => {
                if let Some(fields) = self.provider_fields.get_mut(index) {
                    fields.signing_in = false;
                    fields.progress = Some(format!("Sign-in failed: {message}"));
                    fields.feedback = Some((format!("Sign-in failed: {message}"), true));
                }
                cx.notify();
            }
            SignInUpdate::Disconnected => {
                if let Some(fields) = self.provider_fields.get_mut(index) {
                    fields.signing_in = false;
                    if fields.progress.is_none() {
                        fields.progress = Some(
                            "Sign-in failed: the background task ended unexpectedly.".to_string(),
                        );
                    }
                }
                cx.notify();
            }
        }
    }

    fn enable_provider(
        &mut self,
        id: &str,
        source: &str,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        let source = source.to_string();
        self.mutate_provider(
            id,
            move |provider| {
                provider.enabled = Some(true);
                provider.source = Some(source.clone());
            },
            cx,
        )
    }

    fn save(&mut self, cx: &mut Context<Self>) {
        self.errors.clear();
        self.status = None;

        let mut config = self.config.clone();
        for fields in &self.provider_fields {
            let Some(provider) = config
                .providers
                .iter_mut()
                .find(|provider| catalog::normalize_id(&provider.id) == fields.id)
            else {
                continue;
            };

            if let Some(field) = &fields.api_key {
                let value = field.read(cx).text().trim().to_string();
                provider.api_key = if value.is_empty() { None } else { Some(value) };
            }

            let display_name = catalog::display_name(&fields.id);
            let errors = apply_usage_colors(
                &display_name,
                &mut provider.usage_colors,
                fields.warning_percent.read(cx).text(),
                fields.warning_color.read(cx).text(),
                fields.critical_percent.read(cx).text(),
                fields.critical_color.read(cx).text(),
            );
            self.errors.extend(errors);
        }

        self.errors.extend(ConfigValidation::validate(&config));

        if !self.errors.is_empty() {
            self.status = Some((
                "Fix the highlighted fields before saving.".to_string(),
                true,
            ));
            cx.notify();
            return;
        }

        let startup_before = startup::is_enabled();
        if self.launch_at_signin != startup_before {
            let updated = if self.launch_at_signin {
                startup::enable()
            } else {
                startup::disable()
            };
            if !updated {
                self.errors
                    .push("Could not update the Windows startup entry.".to_string());
                self.status = Some(("Settings were not saved.".to_string(), true));
                cx.notify();
                return;
            }
        }

        match self
            .state
            .update(cx, |state, cx| state.replace_config(config, cx))
        {
            Ok(()) => {
                self.config = self.state.read(cx).config().clone();
                self.sync_fields_from_config(cx);
                self.palette = Theme::from_name(&self.config.theme).palette();
                self.update_field_palettes(cx);
                self.errors.clear();
                self.status = Some(("Settings saved.".to_string(), false));
            }
            Err(error) => {
                let _ = if startup_before {
                    startup::enable()
                } else {
                    startup::disable()
                };
                self.errors
                    .push(format!("Could not save the config: {error:#}"));
                self.status = Some(("Settings were not saved.".to_string(), true));
            }
        }

        cx.notify();
    }

    fn section_header(&self, title: &str, subtitle: &str) -> Div {
        let palette = self.palette;
        div()
            .flex()
            .flex_col()
            .gap_1()
            .child(
                div()
                    .text_size(px(13.0))
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(palette.text)
                    .child(SharedString::from(title.to_string())),
            )
            .child(
                div()
                    .text_size(px(10.0))
                    .text_color(palette.text_muted)
                    .child(SharedString::from(subtitle.to_string())),
            )
    }

    fn toggle_control(&self, id: impl Into<SharedString>, value: bool) -> Stateful<Div> {
        let palette = self.palette;
        div()
            .id(id.into())
            .px(px(8.0))
            .py(px(3.0))
            .rounded_full()
            .bg(if value {
                palette.accent
            } else {
                palette.surface_hover
            })
            .text_color(if value {
                palette.background
            } else {
                palette.text_muted
            })
            .text_size(px(10.0))
            .cursor_pointer()
            .hover(|style| style.opacity(0.85))
            .child(if value { "On" } else { "Off" })
    }

    fn label(&self, text: &str) -> Div {
        div()
            .text_size(px(10.0))
            .text_color(self.palette.text_muted)
            .child(SharedString::from(text.to_string()))
    }

    fn hint(&self, text: &str) -> Div {
        div()
            .text_size(px(9.0))
            .text_color(self.palette.critical)
            .child(SharedString::from(text.to_string()))
    }

    fn action_button(
        &self,
        id: impl Into<SharedString>,
        label: &str,
        primary: bool,
    ) -> Stateful<Div> {
        let palette = self.palette;
        div()
            .id(id.into())
            .px(px(8.0))
            .py(px(3.0))
            .rounded_md()
            .bg(if primary {
                palette.accent
            } else {
                palette.surface_hover
            })
            .text_color(if primary {
                palette.background
            } else {
                palette.text
            })
            .text_size(px(10.0))
            .cursor_pointer()
            .hover(|style| style.opacity(0.85))
            .child(SharedString::from(label.to_string()))
    }

    fn dropdown_button(&self, id: impl Into<SharedString>, label: &str) -> Stateful<Div> {
        let palette = self.palette;
        div()
            .id(id.into())
            .w(px(150.0))
            .flex()
            .items_center()
            .justify_between()
            .gap_1()
            .px(px(6.0))
            .py(px(3.0))
            .rounded_md()
            .bg(palette.surface_hover)
            .text_size(px(10.0))
            .text_color(palette.text)
            .cursor_pointer()
            .hover(|style| style.opacity(0.85))
            .child(SharedString::from(label.to_string()))
            .child("v")
    }

    fn dropdown_option(
        &self,
        id: impl Into<SharedString>,
        label: &str,
        selected: bool,
    ) -> Stateful<Div> {
        let palette = self.palette;
        div()
            .id(id.into())
            .w(px(150.0))
            .px(px(6.0))
            .py(px(2.0))
            .rounded_md()
            .bg(if selected {
                palette.accent
            } else {
                palette.surface
            })
            .text_color(if selected {
                palette.background
            } else {
                palette.text
            })
            .text_size(px(10.0))
            .cursor_pointer()
            .hover(|style| style.bg(palette.surface_hover))
            .child(SharedString::from(label.to_string()))
    }

    fn general_section(&self, cx: &mut Context<Self>) -> Div {
        let palette = self.palette;
        let refresh_enabled = self.config.refresh.enabled;
        let interval = self.config.refresh.interval_minutes;
        let interval_open = self.open_menu.as_deref() == Some("refresh-interval");
        let theme = self.config.theme.clone();
        let theme_open = self.open_menu.as_deref() == Some("theme");

        let mut interval_box = div().flex().flex_col().gap_1().child(
            self.dropdown_button("estao-refresh-interval", &format!("{interval} minutes"))
                .on_click(cx.listener(|this, _, _, cx| {
                    this.toggle_menu("refresh-interval");
                    cx.notify();
                })),
        );
        if interval_open {
            for minutes in RefreshIntervalCatalog::MINUTES {
                interval_box = interval_box.child(
                    self.dropdown_option(
                        format!("estao-refresh-interval-{minutes}"),
                        &format!("{minutes} minutes"),
                        minutes == interval,
                    )
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.config.refresh.interval_minutes = minutes;
                        this.open_menu = None;
                        cx.notify();
                    })),
                );
            }
        }

        let mut theme_box = div().flex().flex_col().gap_1().child(
            self.dropdown_button("estao-theme", theme_label(&theme))
                .on_click(cx.listener(|this, _, _, cx| {
                    this.toggle_menu("theme");
                    cx.notify();
                })),
        );
        if theme_open {
            for (value, label) in [("dark", "Dark"), ("light", "Light")] {
                theme_box = theme_box.child(
                    self.dropdown_option(
                        format!("estao-theme-{value}"),
                        label,
                        theme.eq_ignore_ascii_case(value),
                    )
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.preview_theme(value, cx);
                        this.open_menu = None;
                        cx.notify();
                    })),
                );
            }
        }

        div()
            .flex()
            .flex_col()
            .gap_3()
            .p(px(12.0))
            .rounded_md()
            .bg(palette.surface)
            .border_1()
            .border_color(palette.border)
            .child(self.section_header(
                "General",
                "Background refresh, Windows start-up and the shared color theme.",
            ))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Refresh usage in the background"))
                    .child(
                        self.toggle_control("estao-refresh-enabled", refresh_enabled)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.config.refresh.enabled = !this.config.refresh.enabled;
                                cx.notify();
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Refresh interval"))
                    .child(interval_box),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Launch at sign-in"))
                    .child(
                        self.toggle_control("estao-launch-at-signin", self.launch_at_signin)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.launch_at_signin = !this.launch_at_signin;
                                cx.notify();
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Theme"))
                    .child(theme_box),
            )
    }

    fn provider_card(&self, index: usize, cx: &mut Context<Self>) -> Div {
        let palette = self.palette;
        let fields = &self.provider_fields[index];
        let id = fields.id.clone();
        let display_name = catalog::display_name(&id);
        let provider = self.provider_config(&id).cloned().unwrap_or_default();
        let enabled = provider.enabled.unwrap_or(false);
        let pacing = provider.pacing.clone();

        let header = div()
            .flex()
            .items_center()
            .justify_between()
            .gap_2()
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(2.0))
                    .child(
                        div()
                            .text_size(px(13.0))
                            .font_weight(FontWeight::SEMIBOLD)
                            .text_color(palette.text)
                            .child(SharedString::from(display_name.clone())),
                    )
                    .child(
                        div()
                            .text_size(px(9.0))
                            .text_color(palette.text_muted)
                            .child(SharedString::from(format!(
                                "{} · Pacing: {} · {} · Source: {}",
                                if enabled { "Enabled" } else { "Disabled" },
                                if pacing.enabled {
                                    format!(
                                        "{}/day",
                                        format_percent_value(pacing.daily_target_percent)
                                    )
                                } else {
                                    "off".to_string()
                                },
                                fields.cookie_status,
                                provider.source.as_deref().unwrap_or("auto")
                            ))),
                    ),
            )
            .child(
                self.toggle_control(format!("estao-provider-{id}-enabled"), enabled)
                    .on_click(cx.listener({
                        let id = id.clone();
                        move |this, _, _, cx| {
                            if let Some(provider) = this.provider_config_mut(&id) {
                                provider.enabled = Some(!provider.enabled.unwrap_or(false));
                            }
                            cx.notify();
                        }
                    })),
            );

        let mut card = div()
            .flex()
            .flex_col()
            .gap_2()
            .p(px(12.0))
            .rounded_md()
            .bg(palette.surface)
            .border_1()
            .border_color(palette.border)
            .child(header);

        if fields.accounts.len() > 1 {
            let selected =
                selected_account_index(fields.accounts.len(), provider.active_account_index)
                    .unwrap_or(0);
            let label = fields
                .accounts
                .get(selected)
                .map(|account| account.label.clone())
                .unwrap_or_else(|| "Select account".to_string());
            let menu_id = format!("estao-account-{id}");
            let open = self.open_menu.as_deref() == Some(menu_id.as_str());

            let mut account_box = div().flex().flex_col().gap_1().child(
                self.dropdown_button(format!("estao-account-{id}-button"), &label)
                    .on_click(cx.listener({
                        let menu_id = menu_id.clone();
                        move |this, _, _, cx| {
                            this.toggle_menu(&menu_id);
                            cx.notify();
                        }
                    })),
            );
            if open {
                for (account_index, account) in fields.accounts.iter().enumerate() {
                    account_box = account_box.child(
                        self.dropdown_option(
                            format!("estao-account-{id}-{account_index}"),
                            &account.label,
                            account_index == selected,
                        )
                        .on_click(cx.listener({
                            let id = id.clone();
                            move |this, _, _, cx| {
                                if let Some(provider) = this.provider_config_mut(&id) {
                                    provider.active_account_index = Some(account_index as i32);
                                }
                                this.open_menu = None;
                                cx.notify();
                            }
                        })),
                    );
                }
            }

            card = card.child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Active account"))
                    .child(account_box),
            );
        }

        let mut auth = div().flex().flex_col().gap_2();

        if let Some(field) = &fields.api_key {
            auth = auth.child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .child(self.label("API key / token"))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .child(div().flex_1().min_w_0().child(field.clone()))
                            .child(
                                self.action_button(
                                    format!("estao-provider-{id}-api-save"),
                                    "Save",
                                    false,
                                )
                                .on_click(cx.listener(
                                    move |this, _, _, cx| {
                                        this.save_api_key(index, cx);
                                    },
                                )),
                            )
                            .child(
                                self.action_button(
                                    format!("estao-provider-{id}-api-clear"),
                                    "Clear",
                                    false,
                                )
                                .on_click(cx.listener(
                                    move |this, _, _, cx| {
                                        this.clear_api_key(index, cx);
                                    },
                                )),
                            ),
                    ),
            );
        }

        if let Some(field) = &fields.cookie {
            auth = auth.child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .child(self.label("Cookie header"))
                            .child(
                                div()
                                    .text_size(px(9.0))
                                    .text_color(palette.text_muted)
                                    .child(SharedString::from(fields.cookie_status.clone())),
                            ),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .child(div().flex_1().min_w_0().child(field.clone()))
                            .child(
                                self.action_button(
                                    format!("estao-provider-{id}-cookie-save"),
                                    "Save cookie",
                                    false,
                                )
                                .on_click(cx.listener(
                                    move |this, _, _, cx| {
                                        this.save_cookie(index, cx);
                                    },
                                )),
                            )
                            .child(
                                self.action_button(
                                    format!("estao-provider-{id}-cookie-clear"),
                                    "Clear",
                                    false,
                                )
                                .on_click(cx.listener(
                                    move |this, _, _, cx| {
                                        this.clear_cookie(index, cx);
                                    },
                                )),
                            ),
                    ),
            );
        }

        if let Some(kind) = SignInKind::from_provider(&id) {
            let signing_in = fields.signing_in;
            let label = if signing_in {
                "Signing in…"
            } else {
                "Sign in"
            };
            let mut sign_in_row = div().flex().items_center().gap_2().child(
                self.action_button(format!("estao-provider-{id}-signin"), label, true)
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.start_sign_in(index, cx);
                    })),
            );

            if let Some(progress) = &fields.progress {
                sign_in_row = sign_in_row.child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .text_size(px(9.0))
                        .text_color(palette.text_muted)
                        .child(SharedString::from(progress.clone())),
                );
            } else {
                sign_in_row = sign_in_row.child(
                    div()
                        .text_size(px(9.0))
                        .text_color(palette.text_muted)
                        .child(SharedString::from(format!(
                            "Signs in with {}.",
                            match kind {
                                SignInKind::Codex => "the Codex CLI",
                                SignInKind::Claude => "the Claude Code CLI",
                                SignInKind::Copilot => "GitHub device flow",
                            }
                        ))),
                );
            }

            auth = auth.child(sign_in_row);
        }

        if let Some((message, is_error)) = &fields.feedback {
            auth = auth.child(
                div()
                    .text_size(px(9.0))
                    .text_color(if *is_error {
                        palette.critical
                    } else {
                        palette.text_muted
                    })
                    .child(SharedString::from(message.clone())),
            );
        }

        if fields.api_key.is_some()
            || fields.cookie.is_some()
            || SignInKind::from_provider(&id).is_some()
        {
            card = card.child(auth);
        }

        // Pacing.
        let pacing_menu = format!("estao-pacing-{id}");
        let pacing_open = self.open_menu.as_deref() == Some(pacing_menu.as_str());
        let mut target_box = div().flex().flex_col().gap_1().child(
            self.dropdown_button(
                format!("estao-pacing-{id}-target"),
                &format!("{}/day", format_percent_value(pacing.daily_target_percent)),
            )
            .on_click(cx.listener({
                let pacing_menu = pacing_menu.clone();
                move |this, _, _, cx| {
                    this.toggle_menu(&pacing_menu);
                    cx.notify();
                }
            })),
        );
        if pacing_open {
            for preset in pacing_presets() {
                target_box = target_box.child(
                    self.dropdown_option(
                        format!("estao-pacing-{id}-{}", format_percent_value(preset)),
                        &format!("{}/day", format_percent_value(preset)),
                        (preset - pacing.daily_target_percent).abs() < f64::EPSILON,
                    )
                    .on_click(cx.listener({
                        let id = id.clone();
                        move |this, _, _, cx| {
                            if let Some(provider) = this.provider_config_mut(&id) {
                                provider.pacing.daily_target_percent = preset;
                            }
                            this.open_menu = None;
                            cx.notify();
                        }
                    })),
                );
            }
        }

        let pacing_section = div()
            .flex()
            .flex_col()
            .gap_2()
            .pt(px(4.0))
            .border_t_1()
            .border_color(palette.border)
            .child(self.section_header("Pacing", "A daily budget for this provider's rate window."))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Enable pacing"))
                    .child(
                        self.toggle_control(format!("estao-pacing-{id}-enabled"), pacing.enabled)
                            .on_click(cx.listener({
                                let id = id.clone();
                                move |this, _, _, cx| {
                                    if let Some(provider) = this.provider_config_mut(&id) {
                                        provider.pacing.enabled = !provider.pacing.enabled;
                                    }
                                    cx.notify();
                                }
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Daily target"))
                    .child(target_box),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Notify once a day when over pace"))
                    .child(
                        self.toggle_control(
                            format!("estao-pacing-{id}-notify"),
                            pacing.notify_on_exceed,
                        )
                        .on_click(cx.listener({
                            let id = id.clone();
                            move |this, _, _, cx| {
                                if let Some(provider) = this.provider_config_mut(&id) {
                                    provider.pacing.notify_on_exceed =
                                        !provider.pacing.notify_on_exceed;
                                }
                                cx.notify();
                            }
                        })),
                    ),
            );

        // Usage colors.
        let warning_percent_text = fields.warning_percent.read(cx).text().to_string();
        let critical_percent_text = fields.critical_percent.read(cx).text().to_string();
        let warning_color_text = fields.warning_color.read(cx).text().to_string();
        let critical_color_text = fields.critical_color.read(cx).text().to_string();

        let warning_percent_parsed = parse_percent(&warning_percent_text)
            .ok()
            .filter(|value| *value <= UsageColorCatalog::MAXIMUM_WARNING_PERCENT);
        let warning_percent_hint = if let Ok(value) = parse_percent(&warning_percent_text) {
            if value > UsageColorCatalog::MAXIMUM_WARNING_PERCENT {
                Some(format!(
                    "Use 0-{}.",
                    UsageColorCatalog::MAXIMUM_WARNING_PERCENT
                ))
            } else {
                None
            }
        } else {
            Some(
                parse_percent(&warning_percent_text)
                    .err()
                    .unwrap_or_default(),
            )
        };
        let critical_percent_hint = if let Ok(value) = parse_percent(&critical_percent_text) {
            match warning_percent_parsed {
                Some(warning) if value > warning => None,
                _ => Some("Must be above the warning percent.".to_string()),
            }
        } else {
            Some(
                parse_percent(&critical_percent_text)
                    .err()
                    .unwrap_or_default(),
            )
        };
        let warning_color_hint = normalize_color_field(&warning_color_text).err();
        let critical_color_hint = normalize_color_field(&critical_color_text).err();

        let mut usage = div()
            .flex()
            .flex_col()
            .gap_2()
            .pt(px(4.0))
            .border_t_1()
            .border_color(palette.border)
            .child(self.section_header(
                "Usage colors",
                "Threshold colors used by the popover and taskbar overlay.",
            ))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .child(self.label("Enable usage colors"))
                    .child(
                        self.toggle_control(
                            format!("estao-colors-{id}-enabled"),
                            provider.usage_colors.enabled,
                        )
                        .on_click(cx.listener({
                            let id = id.clone();
                            move |this, _, _, cx| {
                                if let Some(provider) = this.provider_config_mut(&id) {
                                    provider.usage_colors.enabled = !provider.usage_colors.enabled;
                                }
                                cx.notify();
                            }
                        })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(self.label("Warning at"))
                    .child(div().w(px(62.0)).child(fields.warning_percent.clone()))
                    .child(self.label("%"))
                    .child(self.label("Color"))
                    .child(div().w(px(92.0)).child(fields.warning_color.clone())),
            );

        if let Some(hint) = warning_percent_hint {
            usage = usage.child(self.hint(&hint));
        }
        if let Some(hint) = warning_color_hint {
            usage = usage.child(self.hint(&hint));
        }

        usage = usage.child(
            div()
                .flex()
                .items_center()
                .gap_2()
                .child(self.label("Critical at"))
                .child(div().w(px(62.0)).child(fields.critical_percent.clone()))
                .child(self.label("%"))
                .child(self.label("Color"))
                .child(div().w(px(92.0)).child(fields.critical_color.clone())),
        );

        if let Some(hint) = critical_percent_hint {
            usage = usage.child(self.hint(&hint));
        }
        if let Some(hint) = critical_color_hint {
            usage = usage.child(self.hint(&hint));
        }

        card.child(pacing_section).child(usage)
    }

    fn footer(&self, cx: &mut Context<Self>) -> Div {
        let palette = self.palette;
        let status = self.status.clone();

        let mut footer = div()
            .flex_shrink_0()
            .flex()
            .flex_col()
            .gap_1()
            .px(px(16.0))
            .py(px(10.0))
            .border_t_1()
            .border_color(palette.border)
            .bg(palette.background);

        if !self.errors.is_empty() {
            footer = footer.child(
                div()
                    .id("estao-settings-errors")
                    .flex()
                    .flex_col()
                    .gap_1()
                    .max_h(px(78.0))
                    .overflow_y_scroll()
                    .children(self.errors.iter().map(|error| {
                        div()
                            .text_size(px(9.0))
                            .text_color(palette.critical)
                            .child(SharedString::from(error.clone()))
                    })),
            );
        }

        let status_text = status.map(|(message, is_error)| {
            div()
                .flex_1()
                .min_w_0()
                .text_size(px(10.0))
                .text_color(if is_error {
                    palette.critical
                } else {
                    palette.text_muted
                })
                .child(SharedString::from(message))
        });

        let mut actions = div().flex().items_center().justify_end().gap_2();
        if let Some(status_text) = status_text {
            actions = actions.child(status_text);
        }

        footer.child(
            actions
                .child(
                    self.action_button("estao-settings-close", "Close", false)
                        .on_click(cx.listener(|_this, _, window, _cx| {
                            window.remove_window();
                        })),
                )
                .child(
                    self.action_button("estao-settings-save", "Save changes", true)
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.save(cx);
                        })),
                ),
        )
    }
}

impl Render for SettingsWindow {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let general = self.general_section(cx);

        let mut cards = Vec::new();
        for index in 0..self.provider_fields.len() {
            cards.push(self.provider_card(index, cx).into_any_element());
        }

        div()
            .id("estao-settings")
            .size_full()
            .flex()
            .flex_col()
            .bg(palette.background)
            .text_color(palette.text)
            .child(
                div()
                    .flex_shrink_0()
                    .flex()
                    .flex_col()
                    .gap(px(2.0))
                    .px(px(16.0))
                    .py(px(10.0))
                    .border_b_1()
                    .border_color(palette.border)
                    .child(
                        div()
                            .text_size(px(16.0))
                            .font_weight(FontWeight::SEMIBOLD)
                            .text_color(palette.text)
                            .child("Estao settings"),
                    )
                    .child(
                        div()
                            .text_size(px(10.0))
                            .text_color(palette.text_muted)
                            .child("Refresh, providers, pacing and usage colors. Changes apply when you save."),
                    ),
            )
            .child(
                div()
                    .id("estao-settings-body")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .gap_3()
                    .p(px(16.0))
                    .child(general)
                    .children(cards),
            )
            .child(self.footer(cx))
    }
}

fn theme_label(theme: &str) -> &'static str {
    if Theme::from_name(theme) == Theme::Light {
        "Light"
    } else {
        "Dark"
    }
}

fn pacing_presets() -> [f64; PacingCatalog::PRESETS.len()] {
    PacingCatalog::PRESETS
}

fn cookie_status(store: &SecureCookieStore, id: &str, provider: &ProviderConfig) -> String {
    match store.read_cookie_header(id) {
        Ok(Some(_)) => "Encrypted credential saved".to_string(),
        _ if provider
            .cookie_header
            .as_deref()
            .is_some_and(|cookie| !cookie.trim().is_empty()) =>
        {
            "Legacy credential saved".to_string()
        }
        _ => "No cookie stored".to_string(),
    }
}

/// Test-only helpers are kept in the same module so the pure policy functions
/// above stay private to the settings surface.
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn refresh_interval_options_are_the_catalog() {
        assert_eq!(
            RefreshIntervalCatalog::MINUTES,
            [1, 5, 10, 15, 30, 60],
            "the dropdown must be wired to the catalog"
        );
    }

    #[test]
    fn pacing_presets_are_the_catalog() {
        assert_eq!(pacing_presets(), PacingCatalog::PRESETS);
        assert_eq!(pacing_presets(), [5.0, 10.0, 15.0, 20.0, 25.0, 30.0, 50.0]);
    }

    #[test]
    fn parses_percentages_with_the_config_bounds() {
        assert_eq!(parse_percent("75"), Ok(75.0));
        assert_eq!(parse_percent(" 80.5 "), Ok(80.5));
        assert_eq!(parse_percent("0"), Ok(0.0));
        assert_eq!(parse_percent("100"), Ok(100.0));
        assert!(parse_percent("").is_err());
        assert!(parse_percent("abc").is_err());
        assert!(parse_percent("-1").is_err());
        assert!(parse_percent("101").is_err());
        assert!(parse_percent("NaN").is_err());
    }

    #[test]
    fn color_fields_round_trip_through_the_catalog_normalizer() {
        let cases = ["#a1b2c3", " #A1B2C3 ", "#f59e0b", "#000000"];

        for value in cases {
            let normalized = normalize_color_field(value).unwrap();
            assert_eq!(
                normalized,
                UsageColorCatalog::normalize_color(Some(value), "#000000")
            );
        }

        for value in ["#12345", "A1B2C3", "#GGGGGG", "", "orange"] {
            assert!(normalize_color_field(value).is_err(), "{value}");
            assert_eq!(
                UsageColorCatalog::normalize_color(Some(value), "#EF4444"),
                "#EF4444"
            );
        }
    }

    #[test]
    fn validates_usage_color_fields_and_keeps_previous_values_on_error() {
        let mut colors = UsageColorConfig::default();

        let errors = apply_usage_colors("Claude", &mut colors, "75", "#f59e0b", "90", "#ef4444");
        assert!(errors.is_empty(), "{errors:?}");
        assert_eq!(colors.warning_percent, 75.0);
        assert_eq!(colors.warning_color, "#F59E0B");
        assert_eq!(colors.critical_percent, 90.0);
        assert_eq!(colors.critical_color, "#EF4444");

        let errors = apply_usage_colors(
            "Claude",
            &mut colors,
            "not-a-number",
            "orange",
            "95",
            "#00ff00",
        );
        assert_eq!(errors.len(), 2, "{errors:?}");
        assert_eq!(colors.warning_percent, 75.0);
        assert_eq!(colors.warning_color, "#F59E0B");
        assert_eq!(colors.critical_percent, 95.0);
        assert_eq!(colors.critical_color, "#00FF00");

        let errors = apply_usage_colors("Z.ai", &mut colors, "80", "#F59E0B", "80", "#EF4444");
        assert_eq!(errors.len(), 1);
        assert!(errors[0].contains("critical percent must be greater"));
    }

    #[test]
    fn account_selection_is_zero_based_and_bounded() {
        assert_eq!(selected_account_index(3, Some(1)), Some(1));
        assert_eq!(selected_account_index(3, Some(0)), Some(0));
        assert_eq!(selected_account_index(3, Some(5)), Some(0));
        assert_eq!(selected_account_index(3, Some(-1)), Some(0));
        assert_eq!(selected_account_index(3, None), Some(0));
        assert_eq!(selected_account_index(0, Some(0)), None);
    }

    #[test]
    fn provider_auth_rows_match_the_dotnet_pages() {
        assert!(provider_uses_api_key("claude"));
        assert!(provider_uses_api_key("copilot"));
        assert!(provider_uses_api_key("zai"));
        assert!(provider_uses_api_key("z.ai"));
        assert!(!provider_uses_api_key("codex"));
        assert!(!provider_uses_api_key("opencode"));

        assert!(provider_uses_cookie("claude"));
        assert!(provider_uses_cookie("opencode"));
        assert!(provider_uses_cookie("open-code"));
        assert!(!provider_uses_cookie("codex"));
        assert!(!provider_uses_cookie("copilot"));
        assert!(!provider_uses_cookie("zai"));
    }

    #[test]
    fn sign_in_kinds_match_the_dotnet_switch() {
        assert_eq!(SignInKind::from_provider("codex"), Some(SignInKind::Codex));
        assert_eq!(
            SignInKind::from_provider("Claude"),
            Some(SignInKind::Claude)
        );
        assert_eq!(
            SignInKind::from_provider("github-copilot"),
            Some(SignInKind::Copilot)
        );
        assert_eq!(SignInKind::from_provider("opencode"), None);
        assert_eq!(SignInKind::from_provider("zai"), None);
    }

    #[test]
    fn provider_summaries_format_percentages_without_trailing_zeroes() {
        assert_eq!(format_percent_value(15.0), "15");
        assert_eq!(format_percent_value(12.5), "12.5");
        assert_eq!(format_percent_value(0.0), "0");
        assert_eq!(theme_label("light"), "Light");
        assert_eq!(theme_label("dark"), "Dark");
    }

    #[test]
    fn config_validation_accepts_the_default_settings_copy() {
        let config = ConfigStore::create_default_config();
        assert!(ConfigValidation::validate(&config).is_empty());
        assert_eq!(
            config
                .providers
                .iter()
                .filter(|provider| INITIAL_PROVIDER_IDS.contains(&provider.id.as_str()))
                .count(),
            INITIAL_PROVIDER_IDS.len()
        );
    }
}
