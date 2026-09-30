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
- Network cmdlets: `Invoke-RestMethod` / `Invoke-WebRequest`.
- Classes, `Add-Type` C# compilation, `ConvertTo-Json`, remote HTTP APIs, file I/O in the app sandbox.
- A line editor with syntax colours, history, inline suggestions from history (Right/End accepts, Alt+F one word), Ctrl+R reverse search, Tab completion, Ctrl+C, and multi-line paste with `>>` continuation. Lines that look like secrets (`password`, `token`, `apikey`...) stay out of the history file.
- A key bar for the on-screen keyboard (esc, tab, sticky ctrl/alt, arrows, symbols, font size). It hides when a hardware keyboard connects.
- One independent session per window, so Split View and Stage Manager work.
- Files live in `$HOME`: **Files › On My iPad › trenal**. Your profile goes in `~/.config/powershell/Microsoft.PowerShell_profile.ps1`.

### Tools that normally need native binaries

iOS can't run executables, so each of these runs inside the app process instead:

| Command | How | Notes |
|---------|-----|-------|
| `curl`, `wget`, `grep`, `head`, `tail`, `wc`, `touch`, `which`, `env`, `export`, `whoami`, `uname` | PowerShell functions (module `Trenal.Tools`) | Common flags only: `curl -sSL -o -X -H -d --json -u -I -f`, `grep -ivnrlcFowE`. `ls cat cp mv rm ps kill sort tee` are aliases to the cmdlets. |
| `git` | [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) + libgit2 built for iOS | init, clone (`--depth`), status, add, rm, commit, log, diff, show, branch, checkout/switch, fetch, pull, merge, push, remote, config, reset, tag, rev-parse. HTTPS remotes only. Tokens: `$env:GITHUB_TOKEN`, or you're asked once and it's kept in the Keychain. |
| `ssh`, `scp`, `ssh-keygen` | [SSH.NET](https://github.com/sshnet/SSH.NET) | Interactive shell (type `~.` to disconnect), remote commands, `known_hosts`, `~/.ssh/id_ed25519`/`id_ecdsa`/`id_rsa` keys, ed25519 key generation. |
| `*.wasm` programs | [WAMR](https://github.com/bytecodealliance/wasm-micro-runtime) interpreter + WASI | Put a WASI command in `~/.local/bin` (or `Install-WasmTool <url>`) and `jq.wasm` becomes `jq`. Pipes, stdin, exit codes, Ctrl+C and the current folder work. Anything built for `wasm32-wasip1` (Rust, C via wasi-sdk, TinyGo, Zig) runs; no sockets or threads. |
| `Mount-Folder <name>` | iOS document picker + security-scoped bookmarks | Pick any folder from Files (iCloud Drive, a USB drive, an SMB share, another app) and it becomes the drive `<name>:`, restored on every launch. `Dismount-Folder`, `Get-MountedFolder`. |

## Limits

These come from iOS, not from trenal:

- **No native executables.** iOS apps can't `fork`/`exec`. The tools above cover the usual ones; anything else needs a WASI build or a PowerShell equivalent.
- **No root.** Files outside the app sandbox are reachable only through `Mount-Folder`.
- **No Windows-only cmdlets** (registry, WMI/CIM, services).
- **No PSReadLine.** It drives `System.Console`, which iOS doesn't have; trenal has its own editor.
- **Slower than desktop.** Everything is interpreted (PowerShell and WebAssembly). Interactive use is fine; heavy loops take longer.

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

`Invoke-Wasm` and its selftest need the WAMR runtime built for the host first: `native/build-wasm.sh host` (needs cmake and a C compiler). Opt-in selftest groups: `TRENAL_SELFTEST_WASM=1`, `TRENAL_SELFTEST_SSH=1` (starts a throwaway sshd on port 2222, needs `openssh-server` and sudo), `TRENAL_SELFTEST_NET=1`.

The iOS app needs macOS + Xcode (or just CI):

```bash
native/build-psl-native-ios.sh   # PowerShell's native helper as an iOS framework
native/build-libgit2-ios.sh      # libgit2, pinned to the commit LibGit2Sharp expects
native/build-wasm.sh ios         # WAMR interpreter + WASI
dotnet workload install ios
dotnet build src/Trenal.iOS -c Release -p:EnableCodeSigning=false
```

| Path | What |
|------|------|
| `src/Trenal.Core` | PSHost, line editor, key parser, and the in-process tools (git, ssh, wasm, mounts). |
| `src/Trenal.Cli` | Desktop harness and CI selftest. |
| `src/Trenal.iOS` | UIKit scene, WKWebView bridge, xterm.js page. |
| `native/` | Builds the native libraries as iOS frameworks: `libpsl-native`, libgit2, WAMR (`trenal-wasm/shim.c`). |
| `tests/` | sshd fixture and the `.wat`/`.wasm` programs the selftest runs. |
