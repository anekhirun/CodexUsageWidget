# Windows verification and release checklist

This replacement for PR #10 is based on the 2.1.2 `main` branch. It preserves the default weekly/5-hour overview and adds the compact context slider as an opt-in layout. The monitor-follow behavior proposed in #10 is deliberately removed: the widget exclusively owns placement.

## Reproducible checks

```powershell
dotnet restore tests\CodexUsageWidget.Tests\CodexUsageWidget.Tests.csproj --locked-mode
$env:CODEX_WIDGET_RENDER_OUTPUT = Join-Path $PWD 'artifacts\qa'
dotnet test tests\CodexUsageWidget.Tests\CodexUsageWidget.Tests.csproj -c Release --no-restore --logger trx --results-directory artifacts\test-results
powershell -NoProfile -File scripts\Test-Watchdog.ps1
pwsh -NoProfile -File scripts\Build-Release.ps1
```

Run packaging from a clean commit. `global.json` pins the SDK; lock files pin resolved dependencies. The package records the source commit, SDK, executable product version and SHA-256. Build again from the final merged commit before releasing. No changed opaque EXE is included in the source PR.

The isolated tests cover:

- backup exists → user changes catalog `context_window` → selecting a preset preserves the edit and backup;
- newest valid usage behind an incomplete record or outside the 64 KB fast tail;
- the fourth/deep and fifth/fast-only file boundary, the 2 MB cap, and an exact line/tail boundary;
- malformed JSON shapes, out-of-order timestamps, and the upstream five-hour quota contract;
- off-screen and negative-origin coordinates, both layout widths, scaled offsets, hidden taskbars and temporary monitor fallback;
- all four context presets saved and read back with CCR offline, with unrelated model selection preserved;
- actual WPF slider bindings at four render scales and both widget view trees, without opening windows or touching live settings.

The optional live CCR tests are **skipped** unless `CODEX_USAGE_WIDGET_LIVE_CCR_TEST=1` is explicitly set. They are not counted as live verification in ordinary runs.

`Test-Watchdog.ps1` checks script parsing and compiles the embedded lifecycle controller under Windows PowerShell 5.1. It does not register tasks, change startup entries, start Codex, or close running apps.

## Manual Windows checks still required before release

These are not established by unit tests, static rendering, or by the existing user's installation. Run them from the generated package on a separate clean Windows user profile with the packaged Codex desktop app installed.

- [ ] Install the optional watchdog without administrator access; confirm the task action points to the packaged runtime and no widget sign-in entry remains.
- [ ] Launch Codex: one watchdog and one widget start. Repeated Codex windows do not create duplicate widgets.
- [ ] Exit the owning Codex process: the widget closes via `WM_CLOSE`. Reopen Codex and repeat.
- [ ] Move Codex between screens: the widget stays on its selected display.
- [ ] Move the widget using its menu between secondary monitors with mixed DPI, drag it, then restart it: the intended relative position is restored.
- [ ] Disconnect/reconnect the chosen monitor; change resolution, taskbar size and auto-hide; confirm the widget remains visible and clickable.
- [ ] Confirm both layouts, right-click menus, the context slider, capture loss during a drag, and the Lock position option using real input.
- [ ] Uninstall after closing Codex/widget; confirm only the documented task/runtime are removed.

Until this checklist is completed, the clean-profile lifecycle and physical mixed-DPI/input behavior remain unverified release conditions. The PR must not claim those scenarios have passed.
