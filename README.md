# trenal

A terminal for iPad that runs **PowerShell on the device itself**. It works offline and needs no server, SSH or browser session.

```
PowerShell 7.6 engine (System.Management.Automation)
  └─ hosted in-process by Trenal.Core (PSHost + line editor)
       └─ .NET for iOS, Mono interpreter (iOS allows no JIT)
            └─ xterm.js renderer, loaded from files inside the app
```

## Why this works on iOS

iOS forbids JIT compilation. It does not forbid interpreters. .NET for iOS can run all managed code on Mono's IL interpreter, including code that PowerShell generates at runtime: compiled script blocks, `class` definitions (Reflection.Emit), `Add-Type` and binary modules.

CI proves this on every push. `core.yml` runs the full terminal selftest on desktop Mono with `--interpreter`, so nothing is JIT-compiled.

## What you get

- PowerShell 7.6 language and core modules: Utility, Management, Security, Host.
- Network cmdlets: `Invoke-RestMethod` / `Invoke-WebRequest` (your `curl`/`fetch`).
- Classes, `Add-Type` C# compilation, `ConvertTo-Json`, remote HTTP APIs, file I/O in the app sandbox.
- A line editor with history, Tab completion (a candidate list, then Tab to cycle), Ctrl+C, and multi-line paste with `>>` continuation.
- A key bar for the on-screen keyboard (esc, tab, sticky ctrl/alt, arrows, symbols, font size). It hides when a hardware keyboard connects.
- One independent session per window, so Split View and Stage Manager work.
- Files live in `$HOME`: **Files › On My iPad › trenal**. Your profile goes in `~/.config/powershell/Microsoft.PowerShell_profile.ps1`.

## Limits

These come from iOS, not from trenal:

- **No native executables.** iOS apps can't `fork`/`exec`, so `git`, `curl`, `ssh` binaries can't run. Use the PowerShell equivalents (`irm`, `iwr`).
- **No root, no files outside the app sandbox** (except files you open through Files).
- **No Windows-only cmdlets** (registry, WMI/CIM, services).
- **No PSReadLine.** It drives `System.Console`, which iOS doesn't have; trenal has its own editor.
- **Slower than desktop.** Everything is interpreted. Interactive use is fine; heavy loops take longer.

## Install on your iPad

Every push to `main` builds an unsigned IPA. It is attached to the [`latest` release](../../releases/tag/latest) and also available as a CI artifact. Sign it with your own Apple ID using one of these:

| Tool | Cost | Re-sign |
|------|------|---------|
| [SideStore](https://sidestore.io) | free Apple ID | on-device, every 7 days (needs a computer once for setup) |
| [Sideloadly](https://sideloadly.io) (Windows/macOS) | free Apple ID | from the computer, every 7 days |
| Any of the above + Apple Developer Program | $99/yr | once a year |

## Develop

The engine is platform-neutral and runs on Linux, macOS and Windows:

```bash
dotnet run --project src/Trenal.Cli -f net10.0                # interactive, in a real tty
dotnet run --project src/Trenal.Cli -f net10.0 -- --selftest  # scripted keystrokes, asserts output

# the iOS execution mode (Mono, no JIT)
dotnet publish src/Trenal.Cli -c Release -f net8.0 -p:Mono=true -o out/mono
MONO_ENV_OPTIONS=--interpreter out/mono/trenal --selftest
```

The iOS app needs macOS + Xcode (or just CI):

```bash
native/build-psl-native-ios.sh   # PowerShell's native helper as an iOS framework
dotnet workload install ios
dotnet build src/Trenal.iOS -c Release -p:EnableCodeSigning=false
```

| Path | What |
|------|------|
| `src/Trenal.Core` | PSHost, line editor, key parser. All PowerShell logic lives here. |
| `src/Trenal.Cli` | Desktop harness and CI selftest. |
| `src/Trenal.iOS` | UIKit scene, WKWebView bridge, xterm.js page. |
| `native/` | Builds `libpsl-native` for iOS. |
