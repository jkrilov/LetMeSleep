# LetMeSleep

A small native Windows app that explains what is keeping your PC awake and lets
you allow sleep without killing applications.

## Download and run

**[Download LetMeSleep.exe from the latest release](https://github.com/jkrilov/LetMeSleep/releases/latest)**

1. Open the release page and download **LetMeSleep.exe** under **Assets**.
   The "Source code" archives are for developers, not the ready-to-run app.
2. Save the EXE anywhere you like and double-click it. No installer, .NET
   installation, or developer tools are needed.
3. Approve the Windows administrator prompt. Windows requires administrator
   access for complete power diagnostics and override changes.

**Requirements:** Windows 10 or 11, x64 (Intel/AMD 64-bit), and administrator
access. Version 1.0.0 ships an unsigned executable, so Windows may show an
unrecognized-app or publisher warning. Only run a copy you trust from this
repository's releases; do not bypass your organization's security policies.

Opening the app only reads diagnostics. **Nothing changes until you choose an
action and confirm it.** There is no telemetry, network access, or background
service.

## Find what's keeping your PC awake

The **Active blockers** tab shows the apps, services, and drivers asking Windows
to stay awake. Repeated processes such as `msedge.exe` appear in one row, with
their categories, explanations, and a **Requests** count. That count is power
requests, not browser tabs or running processes; Edge can issue several requests
even with one tab open.

The **Overridden** column shows a check mark and the row turns grey when all its
supported categories are already ignored. Partially overridden rows stay normal.
Hover over a row to see per-category status and original paths.

| Control | What it does |
| --- | --- |
| **Refresh** | Reads a new snapshot of blockers and diagnostics. |
| **Allow sleep for selected** | Lets Windows ignore the selected source's supported active requests. |
| **Allow sleep for all** | Does the same for all supported active blockers, with one confirmation. |
| **Saved changes (undo)** | Select a saved change and click **Undo selected change** to restore its previous rule. |
| **Sleep now...** | Asks Windows to sleep immediately, without forcibly closing apps. |
| **Power settings** | Opens Windows settings so you can adjust your normal sleep timer. |
| **Raw diagnostics** | Shows detailed Windows reports, wake sources, timers, available sleep states, and errors. |

## Before allowing sleep

**Closing or pausing the source app is the safest option.** Overrides are
persistent, system-wide rules for a caller name, not just one running instance.
They remain after you close LetMeSleep or restart Windows. Allowing sleep can
interrupt playback, downloads, backups, or other important work.

The app preserves existing override categories and saves the previous rule so
you can undo changes later. If one source fails during **Allow sleep for all**,
other sources are still processed and the app reports the failures. Successful
changes and pending recovery entries remain in **Saved changes**.

Windows supports overrides for **DISPLAY** (screen-on requests), **SYSTEM**, and
**AWAYMODE** (sleep-related requests). Other categories are diagnostic only.
Overrides do not kill processes, change sleep timeouts, or guarantee sleep.
Windows may still list an ignored request; use the **Overridden** column to see
its status. Requests are checked again before applying changes; newly appearing
requests need another refresh and confirmation.

## If your PC still won't sleep

Check **Raw diagnostics** for sleep availability, power-plan timeouts, wake
timers, and the last wake source. A device allowed to wake the PC is not
necessarily preventing it from sleeping; it may instead wake it afterward.
Modern Standby, firmware, hardware, and organization policies can also affect
sleep.

Structured parsing currently supports **English Windows powercfg reports**.
If the output is localized or unrecognized, the app shows raw diagnostics and
disables unsafe actions instead of claiming everything is clear.

## Updating or removing the app

To update, close LetMeSleep and replace the EXE with the one from a newer release.
Saved undo entries remain available when you run it using the same administrator
account.

**Undo your changes before deleting the app.** Deleting the EXE does not remove
Windows overrides. Undo records are stored in
`%LOCALAPPDATA%\LetMeSleep\overrides.json` for the administrator account running
the app. Keep this file while rules are active. Undo refuses to overwrite a rule
that another tool has changed to an unrecognized state; avoid editing the same
rule with multiple tools at once.

<details>
<summary>Advanced: manual recovery</summary>

In an administrator terminal, inspect `powercfg /requestsoverride`.
To remove all override categories for a caller, omit the category:

```powershell
powercfg /requestsoverride PROCESS "Music Player.exe"
```

Only remove rules you intend to remove. If LetMeSleep extended an existing rule,
restore the categories recorded in the journal's `OriginalCategories` instead of
clearing the caller's rule.

</details>

## For developers

<details>
<summary>Build, test, and package from source</summary>

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
or a newer SDK that can build .NET 8 applications. **Start.cmd** builds and opens
the app with an administrator prompt.

```powershell
dotnet build .\src\LetMeSleep\LetMeSleep.csproj -c Release
dotnet run --project .\tests\LetMeSleep.Tests\LetMeSleep.Tests.csproj -c Release
dotnet publish .\src\LetMeSleep\LetMeSleep.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist
```

The last command produces `dist\LetMeSleep.exe`, which can be copied to another
x64 Windows PC without installing .NET. For ARM64 Windows, use `-r win-arm64`.
Publishing may download Microsoft runtime packs. The test executable uses fake
powercfg responses and a temporary journal, exercises the native UI, and never
changes the host's power settings or puts it to sleep.

The executable, window, and taskbar use the sleeping symbol (the zzz emoji) as
their icon. The checked-in multi-resolution icon is generated from Windows'
Segoe UI Emoji font in blue on a transparent background. To regenerate it on
Windows, run `powershell -NoProfile -File .\tools\New-AppIcon.ps1` before building.

Pull requests and pushes to `main` run a Windows build, the regression/UI tests,
and a NuGet audit of direct and transitive dependencies. Restore fails on known
vulnerabilities at any severity or an unavailable audit feed. The same checks
run weekly and can be started manually. Dependabot checks for NuGet and GitHub
Actions updates weekly; workflow actions are pinned to commit SHAs.

For public repositories, the workflow also runs CodeQL's extended C# security
queries and reviews PR dependency changes for newly introduced vulnerabilities
at any severity. These two jobs are skipped while the repository is private
to avoid requiring a paid private-repository security entitlement. A visibility
change takes effect on the next workflow run; dependency review also requires
GitHub's dependency graph to be enabled in repository settings.

These are baseline checks, not a guarantee of security. CodeQL findings appear
in GitHub's code-scanning results; configure code-scanning rules in the
repository's ruleset if alerts should block merging. After checks have run, make
**Windows build, tests, and dependency audit** and **Review dependency changes**
required status checks if merges should be blocked on failures (only require
dependency review when the repository is public). Dependabot configuration and
scheduled runs take effect after they are merged to the default branch.

</details>

Windows diagnostics are provided by the built-in
[`powercfg.exe`](https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options).
