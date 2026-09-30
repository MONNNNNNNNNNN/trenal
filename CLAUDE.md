# trenal

On-device PowerShell terminal for iPad. There is no server and no remote shell: PowerShell 7.6 runs in-process on .NET for iOS (Mono interpreter), and xterm.js renders it inside a WKWebView from bundled files.

## Layout
- `src/Trenal.Core`: platform-neutral. `Shell` (engine thread, runspace, REPL), `LineEditor`, `KeyParser`, `Host.cs` (PSHost/UI/RawUI). Targets net10.0 (PS 7.6) plus net8.0 (PS 7.4), and net8.0 exists only for the Mono selftest.
- `src/Trenal.Cli`: desktop harness. `--selftest` drives a Shell with keystrokes through a fake terminal.
- `src/Trenal.iOS`: `TerminalViewController` (WKWebView + JS bridge), `WebTerminal` (output coalescing), `web/` (xterm.js page and key bar).
- `src/Trenal.Core` tools: `Git.cs` (LibGit2Sharp), `Ssh.cs` (SSH.NET), `Wasm.cs` (WAMR via P/Invoke), `Folders.cs` (Mount-Folder), `Modules/Trenal.Tools` (curl/grep/... shims as PowerShell functions). Platform services (`IFolderAccess`, `ISecretStore`) reach cmdlets through `HostServices` in `Host.PrivateData`.
- `native/`: builds each native library as an iOS framework (`make-framework.sh`): `libpsl-native` (SMA P/Invokes it at runspace start), libgit2 (`git2-<sha7>`, pinned to what LibGit2Sharp expects), `trenal-wasm` (WAMR + `trenal-wasm/shim.c`). `build-wasm.sh host` builds the desktop copy the Cli selftest loads.

## Rules
- No JIT on iOS. Anything added to Core must pass `MONO_ENV_OPTIONS=--interpreter out/mono/trenal --selftest` (see `.github/workflows/core.yml`). Avoid `RegexOptions.Compiled`.
- No `System.Console` in Core: iOS has none. All I/O goes through `ITerminal` and the `KeyQueue`.
- No native processes on iOS. `PATH` is empty on purpose. A new "command" is a cmdlet, a PowerShell function, or a WASI program.
- The Mono interpreter mis-marshals some P/Invokes that CoreCLR tolerates (e.g. an `int` declared where C takes `size_t`: LibGit2Sharp's `git_merge_analysis`, so `git pull` avoids `Commands.Pull`). Test new native calls under the Mono selftest, not just CoreCLR.
- `Environment.SetEnvironmentVariable` doesn't reach native code (libgit2 reads the process HOME): pass paths to native libraries explicitly.
- The engine thread needs a large stack (16 MB). iOS secondary threads default to 512 KB.
- The web page must never load remote content. `LocalOnly` enforces this.
- Building the iOS app needs macOS + Xcode. Use the `ios` workflow; Linux can only build and test Core/Cli.
