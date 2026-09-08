# Changelog

All notable changes to ChatGPT Codex Usage Widget are documented here.

## 2.1.3 (proposed)

- Ignores separate model quota pools such as Codex Spark when reading the main weekly quota, preventing false 100% remaining readings.
- Keeps the 2.1.2 overview and adds an optional compact layout with a responsive context slider and full-width blue fill.
- Preserves intentional custom-catalog context edits even when an older backup differs.
- Bounds usage-tail reads, skips malformed/incomplete records, and selects the newest valid event inside the configured limits.
- Saves position by display device and relative offset; recovers visible bounds after display/taskbar changes without parenting WPF to Explorer.
- Gives the widget sole ownership of positioning and provides an opt-in lifecycle-only Codex watchdog.
- Allows Codex preset changes while optional CCR is offline; shows synchronization errors and never rewrites settings on startup.
- Adds isolated configuration, usage, geometry, and WPF tests, a Windows CI workflow, locked dependencies, and commit/hash build provenance.

## 2.1.2 — 2026-08-26

- Moves context-mode selection from the taskbar slider to the right-click menu.
- Adds a leftmost ChatGPT status icon that pulses when Codex writes a newer usage event.
- Shows 5-hour remaining quota and its reset countdown beside the weekly quota when Codex supplies the short-window limit.
- Uses a transparent widget surface so the embedded control adopts the active Windows taskbar color.

## 2.1.1 — 2026-08-20

- Selects the newest token_count event across all local Codex sessions.
- Replaces the two-second session-directory polling loop with file-change monitoring and cached parsing.
- Makes Manual only stop background usage refreshes.
- Separates the manual refresh check time from the latest Codex data time.
- Adds regression coverage for multi-session selection, missing short-window data, and incomplete JSONL records.

## 2.1.0 — 2026-08-17

- Adds a global four-position context slider: Default, 300K, 600K, and 1M.
- Preserves the selected model and treats non-preset context values as CUSTOM without changing them at startup.
- Adds atomic configuration updates and backup files.
- Supports a custom model catalog ceiling for gpt-5.6-sol.

## 2.0.0 — 2026-07-16

- Rewrites the widget as a native C# / .NET 8 WPF application.
- Publishes a self-contained Windows x64 executable.
- Preserves quota parsing, taskbar embedding, settings, and startup behavior from the prior implementation.
