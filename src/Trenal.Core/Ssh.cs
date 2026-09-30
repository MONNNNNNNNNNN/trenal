using System.Management.Automation;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Trenal.Core;

/// <summary>user@host[:port] plus the options shared by ssh and scp.</summary>
sealed record SshTarget(string User, string Host, int Port, string? IdentityFile)
{
    public static SshTarget Parse(string target, int port, string? identity)
    {
        var at = target.LastIndexOf('@');
        var user = at > 0 ? target[..at] : Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        return new SshTarget(user, at > 0 ? target[(at + 1)..] : target, port, identity);
    }

    public string KnownHostsName => Port == 22 ? Host : $"[{Host}]:{Port}";
}

/// <summary>Connection setup shared by the ssh/scp cmdlets: keys, passwords, known_hosts.</summary>
static class SshConnect
{
    static string SshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public static T Open<T>(PSCmdlet cmdlet, SshTarget t, Func<ConnectionInfo, T> create) where T : BaseClient
    {
        var keys = LoadKeys(cmdlet, t);
        var methods = new List<AuthenticationMethod>();
        if (keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(t.User, [.. keys]));
        methods.Add(KeyboardInteractive(cmdlet, t));
        try
        {
            return Connect(cmdlet, t, create, methods);
        }
        catch (SshAuthenticationException)
        {
            // Many servers only offer "password", which needs the secret up front.
            var password = Prompt(cmdlet, $"{t.User}@{t.Host}'s password: ", secret: true);
            return Connect(cmdlet, t, create, [new PasswordAuthenticationMethod(t.User, password)]);
        }
    }

    static T Connect<T>(PSCmdlet cmdlet, SshTarget t, Func<ConnectionInfo, T> create, List<AuthenticationMethod> methods) where T : BaseClient
    {
        var info = new ConnectionInfo(t.Host, t.Port, t.User, [.. methods]) { Timeout = TimeSpan.FromSeconds(20) };
        var client = create(info);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30); // iPad networks drop idle NAT mappings
        client.HostKeyReceived += (_, e) => e.CanTrust = CheckHostKey(cmdlet, t, e);
        try
        {
            client.Connect();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    static KeyboardInteractiveAuthenticationMethod KeyboardInteractive(PSCmdlet cmdlet, SshTarget t)
    {
        var kbd = new KeyboardInteractiveAuthenticationMethod(t.User);
        kbd.AuthenticationPrompt += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Instruction)) cmdlet.Host.UI.WriteLine(e.Instruction);
            foreach (var p in e.Prompts) p.Response = Prompt(cmdlet, p.Request, secret: !p.IsEchoed);
        };
        return kbd;
    }

    static List<IPrivateKeySource> LoadKeys(PSCmdlet cmdlet, SshTarget t)
    {
        var paths = t.IdentityFile is { } id
            ? [cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(id)]
            : new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => Path.Combine(SshDir, n)).Where(File.Exists).ToArray();
        var keys = new List<IPrivateKeySource>();
        foreach (var path in paths)
        {
            try
            {
                keys.Add(new PrivateKeyFile(path));
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                keys.Add(new PrivateKeyFile(path, Prompt(cmdlet, $"Enter passphrase for key '{path}': ", secret: true)));
            }
        }
        return keys;
    }

    internal static string Prompt(PSCmdlet cmdlet, string text, bool secret)
    {
        cmdlet.Host.UI.Write(text);
        return secret
            ? new NetworkCredential("", cmdlet.Host.UI.ReadLineAsSecureString()).Password
            : cmdlet.Host.UI.ReadLine();
    }

    // --- known_hosts ------------------------------------------------------------------------

    static bool CheckHostKey(PSCmdlet cmdlet, SshTarget t, HostKeyEventArgs e)
    {
        var file = Path.Combine(SshDir, "known_hosts");
        var key = Convert.ToBase64String(e.HostKey);
        if (File.Exists(file))
        {
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || line.StartsWith('#') || parts[1] != e.HostKeyName) continue;
                if (!parts[0].Split(',').Any(p => HostMatches(p, t.KnownHostsName))) continue;
                if (parts[2] == key) return true;
                cmdlet.Host.UI.WriteErrorLine(
                    $"WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED for {t.KnownHostsName} ({e.HostKeyName}).\n" +
                    $"Offending entry in {file}. Remove it if the change is expected.");
                return false;
            }
        }
        cmdlet.Host.UI.WriteLine($"The authenticity of host '{t.KnownHostsName}' can't be established.");
        cmdlet.Host.UI.WriteLine($"{e.HostKeyName} key fingerprint is SHA256:{e.FingerPrintSHA256}.");
        var answer = Prompt(cmdlet, "Are you sure you want to continue connecting (yes/no)? ", secret: false).Trim();
        if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return false;
        Directory.CreateDirectory(SshDir);
        File.AppendAllText(file, $"{t.KnownHostsName} {e.HostKeyName} {key}\n");
        cmdlet.Host.UI.WriteLine($"Permanently added '{t.KnownHostsName}' to the list of known hosts.");
        return true;
    }

    static bool HostMatches(string pattern, string host)
    {
        if (!pattern.StartsWith("|1|")) return pattern.Equals(host, StringComparison.OrdinalIgnoreCase);
        // Hashed entry: |1|base64(salt)|base64(HMAC-SHA1(salt, host))
        var p = pattern.Split('|');
        if (p.Length != 4) return false;
        try
        {
            using var hmac = new HMACSHA1(Convert.FromBase64String(p[2]));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(host))) == p[3];
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>ssh: interactive shell with a remote pty, or run one command.</summary>
[Cmdlet(VerbsCommon.Enter, "SshSession")]
public sealed class EnterSshSessionCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] public string Target { get; set; } = "";
    [Parameter] public int Port { get; set; } = 22;
    [Parameter] public string? IdentityFile { get; set; }
    [Parameter(Position = 1, ValueFromRemainingArguments = true)] public string[]? Command { get; set; }

    protected override void EndProcessing()
    {
        var t = SshTarget.Parse(Target, Port, IdentityFile);
        using var client = SshConnect.Open(this, t, info => new SshClient(info));
        try
        {
            if (Command is { Length: > 0 }) RunCommand(client, string.Join(' ', Command));
            else Interactive(client);
        }
        finally
        {
            client.Disconnect();
        }
    }

    void RunCommand(SshClient client, string text)
    {
        using var cmd = client.CreateCommand(text);
        var output = cmd.Execute();
        if (output.Length > 0) Host.UI.Write(output);
        if (cmd.Error.Length > 0) Host.UI.WriteErrorLine(cmd.Error.TrimEnd('\n'));
        SessionState.PSVariable.Set("global:LASTEXITCODE", cmd.ExitStatus ?? -1);
    }

    void Interactive(SshClient client)
    {
        var shell = HostServices.From(this).Shell ?? throw new PSInvalidOperationException("Interactive ssh needs the trenal terminal.");
        var term = shell.Terminal;
        int cols = term.Columns, rows = term.Rows;
        using var stream = client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 64 * 1024);
        var closed = new ManualResetEventSlim();
        stream.Closed += (_, _) => closed.Set();

        var reader = new Thread(() =>
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[16 * 1024];
            var chars = new char[16 * 1024 + 4];
            try
            {
                int n;
                while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    int c = decoder.GetChars(bytes, 0, n, chars, 0);
                    if (c > 0) term.Write(new string(chars, 0, c));
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SshException or IOException) { }
            finally
            {
                closed.Set();
            }
        }) { IsBackground = true, Name = "trenal-ssh-read" };
        reader.Start();

        // Every keystroke goes to the remote side (Ctrl+C included). "~." after a newline
        // disconnects locally, like OpenSSH.
        var writeLock = new object();
        bool lineStart = true, tilde = false;
        void Send(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            lock (writeLock)
            {
                try { stream.Write(b, 0, b.Length); stream.Flush(); }
                catch (Exception e) when (e is ObjectDisposedException or SshException or IOException) { closed.Set(); }
            }
        }
        using (shell.BeginRawInput(data =>
        {
            var pass = new StringBuilder(data.Length + 1);
            foreach (var ch in data)
            {
                if (tilde)
                {
                    tilde = false;
                    if (ch == '.') { closed.Set(); return; }
                    pass.Append('~');
                }
                else if (lineStart && ch == '~') { tilde = true; lineStart = false; continue; }
                pass.Append(ch);
                lineStart = ch is '\r' or '\n';
            }
            if (pass.Length > 0) Send(pass.ToString());
        }))
        {
            while (!closed.Wait(250))
            {
                if (term.Columns != cols || term.Rows != rows)
                {
                    (cols, rows) = (term.Columns, term.Rows);
                    lock (writeLock) stream.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
                }
            }
        }
        term.Write($"\r\nConnection to {client.ConnectionInfo.Host} closed.\r\n");
    }
}

/// <summary>scp: copy files/directories to or from user@host:path.</summary>
[Cmdlet(VerbsCommon.Copy, "SshItem")]
public sealed class CopySshItemCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] public string Source { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1)] public string Destination { get; set; } = "";
    [Parameter] public int Port { get; set; } = 22;
    [Parameter] public string? IdentityFile { get; set; }
    [Parameter] public SwitchParameter Recurse { get; set; }

    static bool IsRemote(string s, out string target, out string path)
    {
        // user@host:path or host:path; a lone "C:" style drive letter is local.
        int colon = s.IndexOf(':');
        target = path = "";
        if (colon <= 1 || s.StartsWith('/') || s.StartsWith('.') || s.StartsWith('~')) return false;
        target = s[..colon];
        path = s[(colon + 1)..];
        if (path.Length == 0) path = ".";
        return true;
    }

    protected override void EndProcessing()
    {
        bool srcRemote = IsRemote(Source, out var srcTarget, out var srcPath);
        bool dstRemote = IsRemote(Destination, out var dstTarget, out var dstPath);
        if (srcRemote == dstRemote) throw new PSArgumentException("scp: exactly one side must be remote (user@host:path).");

        var t = SshTarget.Parse(srcRemote ? srcTarget : dstTarget, Port, IdentityFile);
        // ShellQuote: remote paths are single-quoted for a POSIX shell, so file names can't inject commands.
        using var scp = SshConnect.Open(this, t, info => new ScpClient(info, RemotePathTransformation.ShellQuote));
        try
        {
            if (dstRemote)
            {
                var local = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Source);
                if (Directory.Exists(local))
                {
                    if (!Recurse) throw new PSArgumentException($"scp: {Source}: is a directory (use -r)");
                    scp.Upload(new DirectoryInfo(local), dstPath);
                }
                else
                {
                    using var f = File.OpenRead(local);
                    var remote = dstPath.EndsWith('/') || dstPath == "." ? $"{dstPath.TrimEnd('/')}/{Path.GetFileName(local)}" : dstPath;
                    scp.Upload(f, remote);
                }
                WriteVerbose($"{Source} -> {Destination}");
            }
            else
            {
                var local = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Destination);
                if (Recurse)
                {
                    Directory.CreateDirectory(local);
                    scp.Download(srcPath, new DirectoryInfo(local));
                }
                else
                {
                    if (Directory.Exists(local)) local = Path.Combine(local, Path.GetFileName(srcPath));
                    using var f = File.Create(local);
                    scp.Download(srcPath, f);
                }
            }
        }
        finally
        {
            scp.Disconnect();
        }
    }
}

/// <summary>ssh-keygen: new ed25519 key pair in OpenSSH format.</summary>
[Cmdlet(VerbsCommon.New, "SshKey")]
public sealed class NewSshKeyCommand : PSCmdlet
{
    [Parameter(Position = 0)] public string? Path { get; set; }
    [Parameter] public string? Comment { get; set; }
    [Parameter] public SwitchParameter Force { get; set; }

    protected override void EndProcessing()
    {
        var path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            Path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519"));
        if (File.Exists(path) && !Force) throw new PSInvalidOperationException($"{path} already exists (use -Force to overwrite).");

        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var priv = Convert.ToBase64String(OpenSshPrivateKeyUtilities.EncodePrivateKey(pair.Private));
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < priv.Length; i += 70) pem.Append(priv, i, Math.Min(70, priv.Length - i)).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        var comment = Comment ?? $"{Environment.GetEnvironmentVariable("USER") ?? "trenal"}@trenal";
        var pub = $"ssh-ed25519 {Convert.ToBase64String(OpenSshPublicKeyUtilities.EncodePublicKey(pair.Public))} {comment}";

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, pem.ToString());
        File.WriteAllText(path + ".pub", pub + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Host.UI.WriteLine($"Your identification has been saved in {path}");
        Host.UI.WriteLine($"Your public key has been saved in {path}.pub");
        WriteObject(pub);
    }
}
