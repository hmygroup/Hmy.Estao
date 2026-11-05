//! Usage refresh service and adaptive loop, ported from
//! `Refresh/UsageRefreshService.cs`.
//!
//! The .NET implementation is async; this port is synchronous because the
//! providers use blocking `ureq` HTTP. Independent providers are still fetched
//! concurrently with [`std::thread::scope`], mirroring `Task.WhenAll`, so a
//! refresh is bounded by the slowest provider instead of the sum of their
//! latencies.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, TryLockError};
use std::time::Duration as StdDuration;

use chrono::{DateTime, TimeDelta, Utc};

use crate::catalog;
use crate::config::{ConfigStore, ProviderConfig, ProviderSource};
use crate::history::UsageHistoryStore;
use crate::models::{FetchRequest, ProviderAccount, UsageHistoryPoint, UsageSnapshot};
use crate::providers::UsageProviderFactory;

/// Fetches usage for every enabled provider and keeps the in-memory history.
pub struct UsageRefreshService {
    config_store: Arc<ConfigStore>,
    provider_factory: UsageProviderFactory,
    history_store: UsageHistoryStore,
    refresh_gate: Mutex<()>,
    last_snapshots: Mutex<Vec<UsageSnapshot>>,
    history: Mutex<Vec<UsageHistoryPoint>>,
    history_loaded: AtomicBool,
}

impl UsageRefreshService {
    /// Creates a service for `config_store`, deriving the history file from the
    /// config path like the .NET constructor.
    pub fn new(config_store: Arc<ConfigStore>) -> Self {
        let history_store = UsageHistoryStore::new(config_store.path());
        Self::with_parts(config_store, UsageProviderFactory::new(), history_store)
    }

    /// Creates a service with an injected provider factory (tests, hosts).
    pub fn with_factory(
        config_store: Arc<ConfigStore>,
        provider_factory: UsageProviderFactory,
    ) -> Self {
        let history_store = UsageHistoryStore::new(config_store.path());
        Self::with_parts(config_store, provider_factory, history_store)
    }

    /// Creates a service with fully injected dependencies (tests, hosts).
    pub fn with_parts(
        config_store: Arc<ConfigStore>,
        provider_factory: UsageProviderFactory,
        history_store: UsageHistoryStore,
    ) -> Self {
        Self {
            config_store,
            provider_factory,
            history_store,
            refresh_gate: Mutex::new(()),
            last_snapshots: Mutex::new(Vec::new()),
            history: Mutex::new(Vec::new()),
            history_loaded: AtomicBool::new(false),
        }
    }

    /// The snapshots produced by the most recent refresh.
    pub fn last_snapshots(&self) -> Vec<UsageSnapshot> {
        self.last_snapshots
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
            .clone()
    }

    /// The persisted usage history known to this service.
    pub fn history(&self) -> Vec<UsageHistoryPoint> {
        self.history
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
            .clone()
    }

    /// The history store used by this service.
    pub fn history_store(&self) -> &UsageHistoryStore {
        &self.history_store
    }

    /// Refreshes every enabled provider.
    ///
    /// Single-flight, like the .NET `SemaphoreSlim` gate: if another refresh is
    /// already running, the cached snapshots are returned instead of starting a
    /// second round of provider requests.
    pub fn refresh(&self) -> Vec<UsageSnapshot> {
        let _guard = match self.refresh_gate.try_lock() {
            Ok(guard) => guard,
            Err(TryLockError::WouldBlock) => return self.last_snapshots(),
            Err(TryLockError::Poisoned(poison)) => poison.into_inner(),
        };

        if !self.history_loaded.load(Ordering::Relaxed) {
            // A read-only profile or a malformed local file must not prevent
            // current provider usage from rendering.
            let loaded = self.history_store.load().unwrap_or_default();
            *self
                .history
                .lock()
                .unwrap_or_else(|poison| poison.into_inner()) = loaded;
            self.history_loaded.store(true, Ordering::Relaxed);
        }

        let config = match self.config_store.load() {
            Ok(config) => config,
            // The .NET service propagates config errors to its caller. This
            // synchronous API cannot, so it keeps the last successful result.
            Err(_) => return self.last_snapshots(),
        };

        let enabled_providers: Vec<&ProviderConfig> = config
            .providers
            .iter()
            .filter(|provider| {
                provider.enabled == Some(true) && catalog::is_supported(&provider.id)
            })
            .collect();

        // Provider requests are independent. Fetching them concurrently keeps
        // startup and manual refresh bounded by the slowest provider instead
        // of the sum of every provider's network latency.
        let snapshots = std::thread::scope(|scope| {
            let handles: Vec<_> = enabled_providers
                .iter()
                .map(|provider_config| {
                    let factory = &self.provider_factory;
                    scope.spawn(move || fetch_provider(factory, provider_config))
                })
                .collect();

            handles
                .into_iter()
                .zip(enabled_providers.iter())
                .map(|(handle, provider_config)| {
                    handle.join().unwrap_or_else(|_| {
                        UsageSnapshot::failure(
                            &catalog::normalize_id(&provider_config.id),
                            provider_config.source.as_deref().unwrap_or("auto"),
                            "provider fetch panicked",
                        )
                    })
                })
                .collect::<Vec<_>>()
        });

        *self
            .last_snapshots
            .lock()
            .unwrap_or_else(|poison| poison.into_inner()) = snapshots.clone();

        // History failures never fail the refresh.
        if let Ok(history) = self.history_store.append(&snapshots, None) {
            *self
                .history
                .lock()
                .unwrap_or_else(|poison| poison.into_inner()) = history;
        }

        snapshots
    }

    /// Picks the adaptive refresh interval for the current user activity.
    pub fn adaptive_delay(
        now: DateTime<Utc>,
        last_interaction: Option<DateTime<Utc>>,
        power_constrained: bool,
    ) -> TimeDelta {
        if power_constrained {
            return TimeDelta::minutes(30);
        }

        let Some(last_interaction) = last_interaction else {
            return TimeDelta::minutes(30);
        };

        let age = now - last_interaction;
        if age <= TimeDelta::minutes(5) {
            return TimeDelta::minutes(2);
        }

        if age <= TimeDelta::hours(1) {
            return TimeDelta::minutes(5);
        }

        if age <= TimeDelta::hours(4) {
            return TimeDelta::minutes(15);
        }

        TimeDelta::minutes(30)
    }
}

/// Fetches one provider, turning any failure into a failure snapshot exactly
/// like the .NET `FetchProviderAsync` catch block.
fn fetch_provider(factory: &UsageProviderFactory, config: &ProviderConfig) -> UsageSnapshot {
    let provider = match factory.create(&config.id) {
        Ok(provider) => provider,
        Err(error) => {
            return UsageSnapshot::failure(
                &catalog::normalize_id(&config.id),
                config.source.as_deref().unwrap_or("auto"),
                &error.to_string(),
            );
        }
    };

    let accounts = provider.accounts(config);
    let selected_account = select_account(config, &accounts);
    let request = FetchRequest {
        config,
        account: selected_account,
        source: ProviderSource::parse(config.source.as_deref()),
    };

    match provider.fetch(&request) {
        Ok(snapshot) => snapshot,
        Err(error) => UsageSnapshot::failure(
            provider.id(),
            config.source.as_deref().unwrap_or("auto"),
            &error.to_string(),
        ),
    }
}

fn select_account<'a>(
    config: &ProviderConfig,
    accounts: &'a [ProviderAccount],
) -> Option<&'a ProviderAccount> {
    if accounts.is_empty() {
        return None;
    }

    let active_index = config
        .active_account_index
        .or_else(|| {
            config
                .token_accounts
                .as_ref()
                .map(|token_accounts| token_accounts.active_index)
        })
        .unwrap_or(0);
    if active_index >= 0 && (active_index as usize) < accounts.len() {
        return Some(&accounts[active_index as usize]);
    }

    Some(&accounts[0])
}

/// A background refresh loop that follows user activity and power state.
///
/// Mirrors the .NET `AdaptiveRefreshLoop`: it refreshes immediately, then waits
/// for the configured delay (or the adaptive delay) and refreshes again.
pub struct AdaptiveRefreshLoop {
    service: Arc<UsageRefreshService>,
    is_power_constrained: Arc<dyn Fn() -> bool + Send + Sync>,
    configured_delay: Arc<dyn Fn() -> Option<TimeDelta> + Send + Sync>,
    last_interaction: Arc<Mutex<Option<DateTime<Utc>>>>,
    worker: Mutex<Option<Worker>>,
}

struct Worker {
    stop: Arc<AtomicBool>,
    handle: Option<std::thread::JoinHandle<()>>,
}

impl AdaptiveRefreshLoop {
    /// Creates a loop for `service`.
    ///
    /// `configured_delay` overrides the adaptive policy when it returns
    /// `Some`; returning `None` falls back to
    /// [`UsageRefreshService::adaptive_delay`].
    pub fn new(
        service: Arc<UsageRefreshService>,
        is_power_constrained: impl Fn() -> bool + Send + Sync + 'static,
        configured_delay: impl Fn() -> Option<TimeDelta> + Send + Sync + 'static,
    ) -> Self {
        Self {
            service,
            is_power_constrained: Arc::new(is_power_constrained),
            configured_delay: Arc::new(configured_delay),
            last_interaction: Arc::new(Mutex::new(None)),
            worker: Mutex::new(None),
        }
    }

    /// Creates a loop that always uses the adaptive policy.
    pub fn with_defaults(service: Arc<UsageRefreshService>) -> Self {
        Self::new(service, || false, || None)
    }

    /// Records the current time as the last user interaction.
    pub fn mark_interaction(&self) {
        *self
            .last_interaction
            .lock()
            .unwrap_or_else(|poison| poison.into_inner()) = Some(Utc::now());
    }

    /// Starts the background loop. Calling it while it is already running is a
    /// no-op.
    pub fn start(&self) {
        let mut worker = self
            .worker
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        if worker.is_some() {
            return;
        }

        let stop = Arc::new(AtomicBool::new(false));
        let service = self.service.clone();
        let is_power_constrained = self.is_power_constrained.clone();
        let configured_delay = self.configured_delay.clone();
        let last_interaction = self.last_interaction.clone();
        let thread_stop = stop.clone();

        let handle = std::thread::spawn(move || {
            service.refresh();

            while !thread_stop.load(Ordering::Relaxed) {
                let delay = configured_delay().unwrap_or_else(|| {
                    let interaction = *last_interaction
                        .lock()
                        .unwrap_or_else(|poison| poison.into_inner());
                    UsageRefreshService::adaptive_delay(
                        Utc::now(),
                        interaction,
                        (is_power_constrained)(),
                    )
                });

                sleep_until_stopped(delay, &thread_stop);
                if thread_stop.load(Ordering::Relaxed) {
                    break;
                }

                service.refresh();
            }
        });

        *worker = Some(Worker {
            stop,
            handle: Some(handle),
        });
    }

    /// Stops the background loop and waits for the worker to exit.
    pub fn stop(&self) {
        let mut worker = self
            .worker
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        if let Some(mut current) = worker.take() {
            current.stop.store(true, Ordering::SeqCst);
            if let Some(handle) = current.handle.take() {
                let _ = handle.join();
            }
        }
    }

    /// Stops and restarts the loop, e.g. after the interval setting changed.
    pub fn restart(&self) {
        self.stop();
        self.start();
    }

    /// Whether the background worker is currently running.
    pub fn is_running(&self) -> bool {
        self.worker
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
            .is_some()
    }
}

impl Drop for AdaptiveRefreshLoop {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Sleeps for `delay`, waking up every 250 ms to check the stop flag so
/// [`AdaptiveRefreshLoop::stop`] joins promptly.
fn sleep_until_stopped(delay: TimeDelta, stop: &AtomicBool) {
    let total = delay.num_milliseconds().max(0) as u64;
    let mut slept = 0;
    while slept < total {
        if stop.load(Ordering::Relaxed) {
            return;
        }

        let chunk = (total - slept).min(250);
        std::thread::sleep(StdDuration::from_millis(chunk));
        slept += chunk;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from RefreshTests.adaptive_delay_matches_conservative_policy.
    #[test]
    fn adaptive_delay_matches_conservative_policy() {
        let now = DateTime::parse_from_rfc3339("2026-07-20T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc);

        assert_eq!(
            UsageRefreshService::adaptive_delay(now, None, false),
            TimeDelta::minutes(30)
        );
        assert_eq!(
            UsageRefreshService::adaptive_delay(now, Some(now - TimeDelta::minutes(2)), false),
            TimeDelta::minutes(2)
        );
        assert_eq!(
            UsageRefreshService::adaptive_delay(now, Some(now - TimeDelta::minutes(30)), false),
            TimeDelta::minutes(5)
        );
        assert_eq!(
            UsageRefreshService::adaptive_delay(now, Some(now - TimeDelta::hours(2)), false),
            TimeDelta::minutes(15)
        );
        assert_eq!(
            UsageRefreshService::adaptive_delay(now, Some(now - TimeDelta::minutes(1)), true),
            TimeDelta::minutes(30)
        );
    }

    #[test]
    fn adaptive_loop_starts_stops_and_restarts() {
        use crate::config::{ConfigStore, EstaoConfig, ProviderConfig};
        use std::fs;

        let directory = std::env::temp_dir().join(format!(
            "estao-refresh-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|duration| duration.as_nanos())
                .unwrap_or(0)
        ));
        fs::create_dir_all(&directory).unwrap();

        // Every provider is disabled, so the loop never touches the network.
        let config_store = Arc::new(ConfigStore::with_path(directory.join("config.json")));
        let config = EstaoConfig {
            providers: vec![ProviderConfig {
                id: "codex".to_string(),
                enabled: Some(false),
                source: Some("auto".to_string()),
                ..ProviderConfig::default()
            }],
            ..EstaoConfig::default()
        };
        config_store.save(config).unwrap();

        let service = Arc::new(UsageRefreshService::new(config_store));
        let refresh_loop =
            AdaptiveRefreshLoop::new(service, || false, || Some(TimeDelta::minutes(10)));

        refresh_loop.start();
        assert!(refresh_loop.is_running());
        refresh_loop.mark_interaction();
        refresh_loop.stop();
        assert!(!refresh_loop.is_running());

        refresh_loop.start();
        refresh_loop.stop();

        let _ = fs::remove_dir_all(&directory);
    }
}
