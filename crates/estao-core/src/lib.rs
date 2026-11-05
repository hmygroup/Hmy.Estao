//! Estao core: provider usage fetching, configuration, refresh scheduling,
//! and secure (DPAPI) secret storage.
//!
//! This crate is a Rust port of the original .NET `Hmy.Estao.Core` library.

pub mod catalog;
pub mod config;
pub mod constants;
pub mod formatting;
pub mod history;
pub mod models;
pub mod pacing;
pub mod paths;
pub mod providers;
pub mod refresh;
pub mod security;
pub mod window_catalog;
