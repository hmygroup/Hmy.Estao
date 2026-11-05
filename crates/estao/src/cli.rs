//! Command-line interface: `estao usage` and `estao config ...`.
//!
//! The clap argument definitions and the dispatch in [`Cli::run`] are the
//! stable interface; the command handlers are ported from
//! `src/Hmy.Estao.Cli/Program.cs` (Cli.RunAsync/UsageAsync/ConfigAsync).

use clap::{Parser, Subcommand};

use crate::app;

/// Parses and dispatches one CLI invocation.
#[derive(Debug, Parser)]
#[command(name = "estao", version, about = "Estao - provider usage tray monitor")]
pub struct Cli {
    /// Launch the GUI with the settings window open
    #[arg(long)]
    pub settings: bool,

    #[command(subcommand)]
    pub command: Option<Command>,
}

#[derive(Debug, Subcommand)]
pub enum Command {
    /// Fetch and print current usage for all enabled providers
    #[command(name = "usage")]
    Usage {
        /// Only this provider (codex|claude|copilot|opencode|zai)
        #[arg(long)]
        provider: Option<String>,

        /// 1-based account index for the selected provider
        #[arg(long, value_name = "N")]
        account_index: Option<usize>,

        /// Output format: text or json
        #[arg(long, default_value = "text")]
        format: String,

        /// Pretty-print JSON output
        #[arg(long)]
        pretty: bool,
    },

    /// Manage the raw JSON config file
    #[command(name = "config")]
    Config {
        #[command(subcommand)]
        action: Option<ConfigAction>,
    },
}

#[derive(Debug, Subcommand)]
pub enum ConfigAction {
    /// Validate the config file
    Validate,

    /// Print the normalized config as JSON
    Dump,

    /// Enable a provider
    Enable {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Disable a provider
    Disable {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Save an API key for a provider (read from --stdin, --value, or --api-key)
    #[command(name = "set-api-key")]
    SetApiKey {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the secret from stdin
        #[arg(long)]
        stdin: bool,

        /// The secret value
        #[arg(long)]
        api_key: Option<String>,

        /// Alias for --api-key
        #[arg(long)]
        value: Option<String>,
    },

    /// Save an API key or token for a provider (read from --stdin, --value, or --api-key)
    Set {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the secret from stdin
        #[arg(long)]
        stdin: bool,

        /// The secret value
        #[arg(long)]
        api_key: Option<String>,

        /// Alias for --api-key
        #[arg(long)]
        value: Option<String>,
    },

    /// Save an encrypted cookie header for a provider (read from --stdin, --cookie, or --value)
    #[command(name = "set-cookie")]
    SetCookie {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the cookie header from stdin
        #[arg(long)]
        stdin: bool,

        /// The cookie header value
        #[arg(long)]
        cookie: Option<String>,

        /// Alias for --cookie
        #[arg(long)]
        value: Option<String>,
    },

    /// Remove a provider's saved cookie
    #[command(name = "clear-cookie")]
    ClearCookie {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Import a config file explicitly
    Import {
        /// Path of the config file to import
        #[arg(long)]
        file: String,
    },
}

impl Cli {
    /// Runs the parsed invocation and returns the process exit code:
    /// 0 on success, 1 when any provider reported an error, 64 on CLI usage
    /// errors (mirroring the .NET CLI).
    pub fn run(self) -> anyhow::Result<i32> {
        if let Some(command) = self.command {
            return match command {
                Command::Usage {
                    provider,
                    account_index,
                    format,
                    pretty,
                } => handle_usage(provider, account_index, &format, pretty),
                Command::Config { action } => handle_config(action),
            };
        }

        app::run(app::AppOptions {
            open_settings: self.settings,
        });
        Ok(0)
    }
}

fn handle_usage(
    _provider: Option<String>,
    _account_index: Option<usize>,
    _format: &str,
    _pretty: bool,
) -> anyhow::Result<i32> {
    todo!("Wave 4: port UsageAsync from src/Hmy.Estao.Cli/Program.cs")
}

fn handle_config(_action: Option<ConfigAction>) -> anyhow::Result<i32> {
    todo!("Wave 4: port ConfigAsync from src/Hmy.Estao.Cli/Program.cs (default action: validate)")
}
