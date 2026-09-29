# trenal

On-device PowerShell terminal for iPad. There is no server and no remote shell: PowerShell 7.6 runs in-process on .NET for iOS (Mono interpreter), and xterm.js renders it inside a WKWebView from bundled files.

## Layout
- `src/Trenal.Core`: platform-neutral. `Shell` (engine thread, runspace, REPL), `LineEditor`, `KeyParser`, `Host.cs` (PSHost/UI/RawUI). Targets net10.0 (PS 7.6) plus net8.0 (PS 7.4), and net8.0 exists only for the Mono selftest.
- `src/Trenal.Cli`: desktop harness. `--selftest` drives a Shell with keystrokes through a fake terminal.
- `src/Trenal.iOS`: `TerminalViewController` (WKWebView + JS bridge), `WebTerminal` (output coalescing), `web/` (xterm.js page and key bar).
- `native/build-psl-native-ios.sh`: builds PowerShell's `libpsl-native` as an iOS framework. SMA P/Invokes it at runspace start.

## Rules
- No JIT on iOS. Anything added to Core must pass `MONO_ENV_OPTIONS=--interpreter out/mono/trenal --selftest` (see `.github/workflows/core.yml`). Avoid `RegexOptions.Compiled`.
- No `System.Console` in Core: iOS has none. All I/O goes through `ITerminal` and the `KeyQueue`.
- No native processes on iOS. `PATH` is empty on purpose.
- The engine thread needs a large stack (16 MB). iOS secondary threads default to 512 KB.
- The web page must never load remote content. `LocalOnly` enforces this.
- Building the iOS app needs macOS + Xcode. Use the `ios` workflow; Linux can only build and test Core/Cli.
