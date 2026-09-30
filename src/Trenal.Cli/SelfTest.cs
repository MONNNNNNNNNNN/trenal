using System.Diagnostics;
using System.Text;
using Trenal.Core;

/// <summary>
/// Drives a real Shell through a fake terminal with keystrokes, the same way xterm.js does on iPad.
/// Run it under `MONO_ENV_OPTIONS=--interpreter` to prove the no-JIT path.
/// </summary>
static class SelfTest
{
    sealed class FakeTerminal : ITerminal
    {
        readonly StringBuilder output = new();
        public int Columns => 100;
        public int Rows => 30;

        public void Write(string text)
        {
            lock (output) output.Append(text);
        }

        public string Text
        {
            get { lock (output) return output.ToString(); }
        }
    }

    public static int Run(ShellOptions options)
    {
        Console.WriteLine(options.Banner);
        var term = new FakeTerminal();
        var shell = new Shell(term, options);
        int? exitCode = null;
        shell.Exited += code => exitCode = code;
        var total = Stopwatch.StartNew();
        shell.Start();

        int failures = 0;
        int mark = 0;
        const string Prompt = "~ > ";

        bool WaitFor(string needle, int timeoutMs = 60_000, int count = 1)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var seen = Vt.Strip(term.Text[mark..]);
                if (Occurrences(seen, needle) >= count) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        bool WaitForRaw(string needle, int timeoutMs = 30_000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (term.Text[mark..].Contains(needle, StringComparison.Ordinal)) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        void Send(string keys)
        {
            mark = term.Text.Length;
            shell.Input(keys);
        }

        void Check(string name, Func<bool> test)
        {
            var sw = Stopwatch.StartNew();
            bool ok;
            try { ok = test(); }
            catch (Exception e) { Console.WriteLine(e); ok = false; }
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name} ({sw.ElapsedMilliseconds} ms)");
            if (!ok)
            {
                failures++;
                Console.WriteLine("---- terminal output since last input ----");
                Console.WriteLine(Vt.Strip(term.Text[mark..]).Replace("\r", ""));
                Console.WriteLine("------------------------------------------");
            }
        }

        Check("first prompt", () => WaitFor(Prompt, 120_000));
        Check("expression", () => { Send("40+2\r"); return WaitFor("42") && WaitFor(Prompt); });
        Check("class via Reflection.Emit", () =>
        {
            Send("class Foo { [int] Next() { return 41 + 1 } }; [Foo]::new().Next() * 10\r");
            return WaitFor("420") && WaitFor(Prompt);
        });
        Check("Read-Host", () =>
        {
            Send("$n = Read-Host 'name'\r");
            if (!WaitFor("name:")) return false;
            Send("bob\r");
            if (!WaitFor(Prompt)) return false;
            Send("\"hi $n\"\r");
            return WaitFor("hi bob") && WaitFor(Prompt);
        });
        Check("Ctrl+C stops a running pipeline", () =>
        {
            Send("while ($true) { Start-Sleep -Milliseconds 20 }\r");
            Thread.Sleep(500);
            Send("\x03");
            if (!WaitFor(Prompt, 10_000)) return false;
            Send("'after-' + 'stop'\r");
            return WaitFor("after-stop");
        });
        Check("Ctrl+C cancels Read-Host", () =>
        {
            Send("Read-Host 'q'\r");
            if (!WaitFor("q:")) return false;
            Send("\x03");
            if (!WaitFor(Prompt, 10_000)) return false;
            Send("'after-' + 'readhost'\r");
            return WaitFor("after-readhost");
        });
        Check("multi-line continuation", () =>
        {
            Send("if ($true) {\r");
            if (!WaitFor(">> ")) return false;
            Send("'in' + 'side'\r");
            Send("}\r");
            return WaitFor("inside") && WaitFor(Prompt);
        });
        Check("bracketed multi-line paste", () =>
        {
            Send("\x1b[200~$a = 333\n$a * 3\x1b[201~\r");
            return WaitFor("999") && WaitFor(Prompt);
        });
        Check("errors are reported", () => { Send("Get-Nope\r"); return WaitFor("not recognized") && WaitFor(Prompt); });
        Check("tab completion", () =>
        {
            Send("Get-ChildIt\t");
            if (!WaitFor("Get-ChildItem")) return false;
            Send("\x03");
            return WaitFor("^C") && WaitFor(Prompt);
        });
        Check("history recall", () =>
        {
            Send("'hist-' + 'ok'\r");
            if (!WaitFor("hist-ok") || !WaitFor(Prompt)) return false;
            Send("\x1b[A\r");
            return WaitFor("hist-ok") && WaitFor(Prompt);
        });
        Check("Thai text", () => { Send("'สวัส' + 'ดี'\r"); return WaitFor("สวัสดี") && WaitFor(Prompt); });
        Check("files in HOME", () =>
        {
            Send("Set-Content ~/t.txt ('file-' + 'ok'); Get-Content ~/t.txt\r");
            return WaitFor("file-ok") && WaitFor(Prompt);
        });
        Check("ConvertTo-Json", () => { Send("@{k='json-ok'} | ConvertTo-Json -Compress\r"); return WaitFor("{\"k\":\"json-ok\"}"); });
        Check("syntax highlighting", () =>
        {
            Send("Write-Output 'hl-x'");
            if (!WaitForRaw("\x1b[93mWrite-Output") || !WaitForRaw("\x1b[36m'hl-x'")) return false;
            Send("\x03");
            return WaitFor(Prompt);
        });
        Check("inline suggestion from history", () =>
        {
            Send("'sugg-' + 'one'\r");
            if (!WaitFor("sugg-one") || !WaitFor(Prompt)) return false;
            Send("'sugg");
            if (!WaitFor("-' + 'one'")) return false;
            Send("\x1b[C\r"); // Right accepts, Enter runs
            return WaitFor("sugg-one") && WaitFor(Prompt);
        });
        Check("Ctrl+R reverse search", () =>
        {
            Send("\x12");
            if (!WaitFor("(reverse-i-search)")) return false;
            Send("sugg-");
            if (!WaitFor("'sugg-' + 'one'")) return false;
            Send("\r");
            return WaitFor("sugg-one") && WaitFor(Prompt);
        });
        Check("secrets stay out of the history file", () =>
        {
            Send("$apiToken = 'zz' + 'secret-value'; 'after-' + 'secret'\r");
            if (!WaitFor("after-secret") || !WaitFor(Prompt)) return false;
            var file = options.HistoryFile!;
            return File.Exists(file) && !File.ReadAllText(file).Contains("secret-value") && File.ReadAllText(file).Contains("'sugg-' + 'one'");
        });
        Check("Mount-Folder / Dismount-Folder", () =>
        {
            Send("$null = mkdir ~/proj; Set-Content ~/proj/m.txt ('mount-' + 'ok'); Mount-Folder work ~/proj | Out-Null; cat work:/m.txt; (Get-MountedFolder).Name\r");
            if (!WaitFor("mount-ok") || !WaitFor("work:") || !WaitFor(Prompt)) return false;
            Send("Dismount-Folder work; Test-Path work:/; @(Get-MountedFolder).Count\r");
            return WaitFor("False") && WaitFor("0") && WaitFor(Prompt);
        });
        Check("unix shims: grep/head/tail/wc/which", () =>
        {
            Send("'alpha','beta','gamma' | grep -n et; (1..50 | head -n 5 | tail -1) * 20; 'x','y' | wc -l; which curl\r");
            return WaitFor("2:beta") && WaitFor("100") && WaitFor("curl: function") && WaitFor(Prompt);
        });
        Check("unix aliases: ls/cat/rm", () =>
        {
            Send("touch ~/a.txt; Set-Content ~/a.txt ('cat-' + 'ok'); cat ~/a.txt; (ls ~/a.txt).Name; rm ~/a.txt; Test-Path ~/a.txt\r");
            return WaitFor("cat-ok") && WaitFor("a.txt") && WaitFor("False") && WaitFor(Prompt);
        });
        if (Environment.GetEnvironmentVariable("TRENAL_SELFTEST_NET") == "1")
        {
            Check("Invoke-RestMethod", () => { Send("(Invoke-RestMethod https://api.nuget.org/v3/index.json).version\r"); return WaitFor("3.0.0"); });
            Check("curl shim", () => { Send("(curl -sSL https://api.nuget.org/v3/index.json | ConvertFrom-Json).resources.Count -gt 0\r"); return WaitFor("True"); });
        }
        Check("git: init/add/commit/push/clone/pull", () =>
        {
            Send("git config --global user.name 'Self Test'; git config --global user.email st@example.com; " +
                 "cd ~; git init --bare remote.git | Out-Null; git init w | Out-Null; cd w; Set-Content a.txt 'v1'; " +
                 "git add .; git commit -m ('git-' + 'first'); git remote add origin ~/remote.git; git push -u origin (git rev-parse --abbrev-ref HEAD) | Out-Null; " +
                 "cd ~; git clone ~/remote.git c | Out-Null; cd w; Add-Content a.txt 'v2'; git commit -a -m ('git-' + 'second') | Out-Null; git push | Out-Null; " +
                 "cd ~/c; git pull | Out-Null; git log --oneline | Select-Object -First 1; git status -s; cd ~\r");
            return WaitFor("] git-first") && WaitFor(" git-second") && WaitFor(Prompt);
        });
        if (Environment.GetEnvironmentVariable("TRENAL_SELFTEST_NET") == "1")
            Check("git clone over HTTPS", () =>
            {
                Send("git clone --depth 1 https://github.com/octocat/Hello-World.git ~/hw | Out-Null; (Get-Content ~/hw/README) -join ''\r");
                return WaitFor("Hello World!", 120_000) && WaitFor(Prompt);
            });
        if (Environment.GetEnvironmentVariable("TRENAL_SELFTEST_SSH") == "1")
        {
            string? sshdDir = null;
            Check("ssh-keygen", () =>
            {
                Send("ssh-keygen -f ~/.ssh/id_ed25519 -N '' -C selftest | Out-Null; (cat ~/.ssh/id_ed25519.pub) -match '^ssh-ed25519 '\r");
                return WaitFor("True") && WaitFor(Prompt);
            });
            Check("sshd (test fixture) starts", () =>
            {
                var home = options.Environment["HOME"]!;
                var script = Path.Combine(AppContext.BaseDirectory, "start-sshd.sh");
                var p = Process.Start(new ProcessStartInfo("bash", [script, Path.Combine(home, ".ssh", "id_ed25519.pub"), "2222"])
                    { RedirectStandardOutput = true, UseShellExecute = false })!;
                sshdDir = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return p.ExitCode == 0 && File.Exists(Path.Combine(sshdDir, "sshd.pid"));
            });
            Check("ssh remote command + known_hosts prompt", () =>
            {
                Send("ssh -p 2222 \"$(whoami)@127.0.0.1\" echo remote-'$((40+2))'\r");
                if (!WaitFor("Are you sure you want to continue connecting")) return false;
                Send("yes\r");
                return WaitFor("remote-42") && WaitFor(Prompt);
            });
            Check("ssh interactive shell (raw passthrough)", () =>
            {
                Send("ssh -p 2222 \"$(whoami)@127.0.0.1\"\r");
                Thread.Sleep(1500);
                Send("echo inner-$((6*7))\r");
                if (!WaitFor("inner-42")) return false;
                Send("exit\r");
                return WaitFor("Connection to 127.0.0.1 closed.") && WaitFor(Prompt);
            });
            Check("scp upload + download", () =>
            {
                Send("Set-Content ~/up.txt ('scp-' + 'roundtrip'); scp -P 2222 ~/up.txt \"$(whoami)@127.0.0.1:/tmp/trenal-scp.txt\"; scp -P 2222 \"$(whoami)@127.0.0.1:/tmp/trenal-scp.txt\" ~/down.txt; cat ~/down.txt\r");
                return WaitFor("scp-roundtrip") && WaitFor(Prompt);
            });
            if (sshdDir is not null && File.Exists(Path.Combine(sshdDir, "sshd.pid")))
            {
                var pid = File.ReadAllText(Path.Combine(sshdDir, "sshd.pid")).Trim();
                Process.Start(new ProcessStartInfo("bash", ["-c", $"kill {pid} 2>/dev/null || sudo kill {pid}"]))?.WaitForExit();
            }
        }
        Check("exit code", () =>
        {
            Send("exit 3\r");
            var sw = Stopwatch.StartNew();
            while (exitCode is null && sw.ElapsedMilliseconds < 10_000) Thread.Sleep(20);
            return exitCode == 3;
        });

        Console.WriteLine($"{(failures == 0 ? "all passed" : $"{failures} failed")} in {total.ElapsedMilliseconds} ms");
        return failures == 0 ? 0 : 1;
    }

    static int Occurrences(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
