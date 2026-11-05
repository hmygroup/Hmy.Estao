//! Estao: Windows tray usage monitor for AI coding providers.
//!
//! One binary: `estao` with no arguments launches the GPUI tray app,
//! `estao usage` / `estao config ...` run CLI commands.

mod app;
mod cli;

use clap::Parser;

fn main() {
    let cli = cli::Cli::parse();
    let exit_code = match cli.run() {
        Ok(code) => code,
        Err(error) => {
            eprintln!("{error:#}");
            1
        }
    };
    std::process::exit(exit_code);
}
