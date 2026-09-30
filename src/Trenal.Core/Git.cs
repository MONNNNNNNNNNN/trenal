using System.Management.Automation;
using System.Text;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Signature = LibGit2Sharp.Signature;

namespace Trenal.Core;

/// <summary>
/// `git` without a git binary: the common porcelain on top of libgit2 (LibGit2Sharp). HTTPS
/// remotes and local paths; credentials come from GIT_TOKEN/GITHUB_TOKEN, the secret store,
/// or a prompt (saved after a successful operation).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "Git")]
public sealed class InvokeGitCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromRemainingArguments = true)]
    public string[] Arguments { get; set; } = [];

    (string Key, string Value)? pendingCredential;

    string Cwd => SessionState.Path.CurrentFileSystemLocation.ProviderPath;

    const string Supported =
        "init, clone, status, add, rm, commit, log, diff, show, branch, checkout, switch, fetch, pull, push, remote, config, reset, tag, rev-parse";

    protected override void EndProcessing()
    {
        if (Arguments.Length == 0)
        {
            WriteObject($"usage: git <command> [<args>]\nsupported: {Supported}");
            return;
        }
        var a = new GitArgs(Arguments[1..]);
        try
        {
            switch (Arguments[0])
            {
                case "init": Init(a); break;
                case "clone": Clone(a); break;
                case "status": Status(a); break;
                case "add": Add(a); break;
                case "rm": Rm(a); break;
                case "commit": Commit(a); break;
                case "log": Log(a); break;
                case "diff": Diff(a); break;
                case "show": Show(a); break;
                case "branch": BranchCmd(a); break;
                case "checkout": Checkout(a, createFlag: "-b"); break;
                case "switch": Checkout(a, createFlag: "-c"); break;
                case "fetch": Fetch(a); break;
                case "pull": Pull(a); break;
                case "push": Push(a); break;
                case "remote": RemoteCmd(a); break;
                case "config": Config(a); break;
                case "reset": Reset(a); break;
                case "tag": Tag(a); break;
                case "rev-parse": RevParse(a); break;
                case "--version" or "version": WriteObject($"git version (libgit2 {GlobalSettings.Version.LibGit2CommitSha}, LibGit2Sharp)"); break;
                default: throw new PSArgumentException($"git: '{Arguments[0]}' is not supported here. Supported: {Supported}");
            }
        }
        catch (LibGit2SharpException e)
        {
            ThrowTerminatingError(new ErrorRecord(e, "Trenal.Git", ErrorCategory.InvalidOperation, Arguments[0]));
        }
    }

    // --- repository helpers ------------------------------------------------------------------

    Repository Open()
    {
        var path = Repository.Discover(Cwd)
            ?? throw new PSInvalidOperationException("fatal: not a git repository (or any of the parent directories): .git");
        return new Repository(path);
    }

    string Full(string path) => SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);

    static string RepoRelative(Repository repo, string fullPath) =>
        Path.GetRelativePath(repo.Info.WorkingDirectory, fullPath).Replace('\\', '/');

    Signature Me(Repository repo) =>
        repo.Config.BuildSignature(DateTimeOffset.Now)
        ?? throw new PSInvalidOperationException(
            "Please tell me who you are:\n  git config --global user.name \"Your Name\"\n  git config --global user.email you@example.com");

    CredentialsHandler Credentials()
    {
        int attempt = 0;
        return (url, userFromUrl, _) =>
        {
            attempt++;
            var key = "git:" + new Uri(url).GetLeftPart(UriPartial.Authority);
            var secrets = HostServices.From(this).Secrets;
            if (attempt == 1)
            {
                var token = Environment.GetEnvironmentVariable("GIT_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(token))
                    return new UsernamePasswordCredentials { Username = userFromUrl ?? "x-access-token", Password = token };
                if (secrets.Get(key) is { } saved && saved.Split('\n', 2) is [var u, var p])
                    return new UsernamePasswordCredentials { Username = u, Password = p };
            }
            if (attempt > 3) throw new PSInvalidOperationException($"Authentication failed for '{url}'");
            if (attempt > 1) secrets.Remove(key);
            Host.UI.Write($"Username for '{url}'{(userFromUrl is null ? "" : $" [{userFromUrl}]")}: ");
            var user = Host.UI.ReadLine();
            if (string.IsNullOrEmpty(user)) user = userFromUrl ?? "";
            Host.UI.Write($"Password or token for '{user}': ");
            var pass = new System.Net.NetworkCredential("", Host.UI.ReadLineAsSecureString()).Password;
            pendingCredential = (key, $"{user}\n{pass}");
            return new UsernamePasswordCredentials { Username = user, Password = pass };
        };
    }

    void CredentialsWorked()
    {
        if (pendingCredential is { } c) HostServices.From(this).Secrets.Set(c.Key, c.Value);
        pendingCredential = null;
    }

    FetchOptions FetchOpts(int depth = 0) => new()
    {
        CredentialsProvider = Credentials(),
        OnTransferProgress = Progress,
        Depth = depth,
    };

    bool Progress(TransferProgress p)
    {
        if (p.TotalObjects > 0)
        {
            var done = p.ReceivedObjects == p.TotalObjects && p.IndexedObjects == p.TotalObjects;
            Host.UI.Write($"\rReceiving objects: {100 * p.ReceivedObjects / p.TotalObjects,3}% ({p.ReceivedObjects}/{p.TotalObjects}), {p.ReceivedBytes / 1024} KiB{(done ? ", done.\n" : "")}");
        }
        return !Stopping; // Ctrl+C cancels the transfer
    }

    // --- commands ----------------------------------------------------------------------------

    void Init(GitArgs a)
    {
        bool bare = a.Flag("--bare");
        var dir = Full(a.Rest.FirstOrDefault() ?? ".");
        Directory.CreateDirectory(dir);
        var path = Repository.Init(dir, bare);
        WriteObject($"Initialized empty Git repository in {path}");
    }

    void Clone(GitArgs a)
    {
        var branch = a.Value("-b", "--branch");
        var depth = int.TryParse(a.Value("--depth"), out var d) ? d : 0;
        if (a.Rest.Count == 0) throw new PSArgumentException("usage: git clone [-b branch] [--depth N] <url> [dir]");
        var url = a.Rest[0];
        var name = a.Rest.Count > 1 ? a.Rest[1] : Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
        var dir = Full(name);
        Host.UI.WriteLine($"Cloning into '{name}'...");
        var options = new CloneOptions(FetchOpts(depth)) { BranchName = branch };
        Repository.Clone(url, dir, options);
        CredentialsWorked();
    }

    void Status(GitArgs a)
    {
        bool shortFormat = a.Flag("-s", "--short");
        using var repo = Open();
        var status = repo.RetrieveStatus(new StatusOptions());
        if (shortFormat)
        {
            foreach (var e in status)
            {
                if (e.State == FileStatus.Ignored) continue;
                WriteObject($"{IndexCode(e.State)}{WorkCode(e.State)} {e.FilePath}");
            }
            return;
        }
        var head = repo.Head;
        WriteObject(repo.Info.IsHeadDetached ? $"HEAD detached at {head.Tip?.Sha[..7]}" : $"On branch {head.FriendlyName}");
        if (head.IsTracking && head.TrackingDetails is { AheadBy: not null, BehindBy: not null } t)
        {
            var up = head.TrackedBranch.FriendlyName;
            WriteObject((t.AheadBy, t.BehindBy) switch
            {
                (0, 0) => $"Your branch is up to date with '{up}'.",
                ( > 0, 0) => $"Your branch is ahead of '{up}' by {t.AheadBy} commit(s).",
                (0, > 0) => $"Your branch is behind '{up}' by {t.BehindBy} commit(s).",
                _ => $"Your branch and '{up}' have diverged ({t.AheadBy} and {t.BehindBy} different commits).",
            });
        }
        Section("Changes to be committed:", status.Where(e => IndexCode(e.State) != ' ' && IndexCode(e.State) != '?'), e => Describe(e.State, index: true));
        Section("Changes not staged for commit:", status.Where(e => WorkCode(e.State) is 'M' or 'D'), e => Describe(e.State, index: false));
        Section("Untracked files:", status.Untracked, _ => "");
        if (!status.IsDirty) WriteObject("nothing to commit, working tree clean");

        void Section(string title, IEnumerable<StatusEntry> entries, Func<StatusEntry, string> label)
        {
            var list = entries.ToList();
            if (list.Count == 0) return;
            WriteObject(title);
            foreach (var e in list) WriteObject($"\t{label(e)}{e.FilePath}");
            WriteObject("");
        }
    }

    static char IndexCode(FileStatus s) =>
        s.HasFlag(FileStatus.NewInIndex) ? 'A' : s.HasFlag(FileStatus.ModifiedInIndex) ? 'M' :
        s.HasFlag(FileStatus.DeletedFromIndex) ? 'D' : s.HasFlag(FileStatus.RenamedInIndex) ? 'R' :
        s.HasFlag(FileStatus.NewInWorkdir) ? '?' : ' ';

    static char WorkCode(FileStatus s) =>
        s.HasFlag(FileStatus.NewInWorkdir) ? '?' : s.HasFlag(FileStatus.ModifiedInWorkdir) ? 'M' :
        s.HasFlag(FileStatus.DeletedFromWorkdir) ? 'D' : ' ';

    static string Describe(FileStatus s, bool index) => (index ? IndexCode(s) : WorkCode(s)) switch
    {
        'A' => "new file:   ",
        'M' => "modified:   ",
        'D' => "deleted:    ",
        'R' => "renamed:    ",
        _ => "",
    };

    void Add(GitArgs a)
    {
        bool all = a.Flag("-A", "--all");
        using var repo = Open();
        var paths = a.Rest.Count == 0 && all ? ["*"] : a.Rest.Select(p => p == "." ? "*" : RepoRelative(repo, Full(p))).ToList();
        if (paths.Count == 0) throw new PSArgumentException("Nothing specified, nothing added.");
        Commands.Stage(repo, paths);
    }

    void Rm(GitArgs a)
    {
        bool cached = a.Flag("--cached");
        using var repo = Open();
        foreach (var p in a.Rest) Commands.Remove(repo, RepoRelative(repo, Full(p)), removeFromWorkingDirectory: !cached);
    }

    void Commit(GitArgs a)
    {
        var message = a.Value("-m", "--message");
        bool all = a.Flag("-a", "--all");
        bool allowEmpty = a.Flag("--allow-empty");
        if (message is null) throw new PSArgumentException("git commit: -m <message> is required (there is no editor).");
        using var repo = Open();
        if (all)
        {
            var tracked = repo.RetrieveStatus().Where(e => e.State.HasFlag(FileStatus.ModifiedInWorkdir) || e.State.HasFlag(FileStatus.DeletedFromWorkdir));
            foreach (var e in tracked) Commands.Stage(repo, e.FilePath);
        }
        var me = Me(repo);
        var c = repo.Commit(message, me, me, new CommitOptions { AllowEmptyCommit = allowEmpty });
        WriteObject($"[{repo.Head.FriendlyName} {c.Sha[..7]}] {c.MessageShort}");
    }

    void Log(GitArgs a)
    {
        bool oneline = a.Flag("--oneline");
        int n = int.MaxValue;
        if (a.Value("-n", "--max-count") is { } nv) n = int.Parse(nv);
        foreach (var r in a.Rest.ToList())
            if (r.Length > 1 && r[0] == '-' && int.TryParse(r[1..], out var k)) { n = k; a.Rest.Remove(r); }
        using var repo = Open();
        IEnumerable<Commit> commits = a.Rest.Count > 0
            ? repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = a.Rest[0] })
            : repo.Commits;
        foreach (var c in commits.Take(n))
        {
            if (oneline) { WriteObject($"{c.Sha[..7]} {c.MessageShort}"); continue; }
            WriteObject($"commit {c.Sha}");
            WriteObject($"Author: {c.Author.Name} <{c.Author.Email}>");
            WriteObject($"Date:   {c.Author.When:ddd MMM d HH:mm:ss yyyy zzz}");
            WriteObject("");
            foreach (var line in c.Message.TrimEnd().Split('\n')) WriteObject("    " + line);
            WriteObject("");
        }
    }

    void Diff(GitArgs a)
    {
        bool cached = a.Flag("--cached", "--staged");
        using var repo = Open();
        var paths = a.Rest.Select(p => RepoRelative(repo, Full(p))).ToList();
        var patch = cached
            ? repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, paths.Count > 0 ? paths : null)
            : repo.Diff.Compare<Patch>(paths.Count > 0 ? paths : null, includeUntracked: false);
        if (patch.Content.Length == 0) return;
        foreach (var line in patch.Content.TrimEnd('\n').Split('\n')) WriteObject(line);
    }

    void Show(GitArgs a)
    {
        using var repo = Open();
        var c = repo.Lookup<Commit>(a.Rest.FirstOrDefault() ?? "HEAD") ?? throw new PSArgumentException("unknown revision");
        WriteObject($"commit {c.Sha}");
        WriteObject($"Author: {c.Author.Name} <{c.Author.Email}>");
        WriteObject($"Date:   {c.Author.When:ddd MMM d HH:mm:ss yyyy zzz}\n");
        foreach (var line in c.Message.TrimEnd().Split('\n')) WriteObject("    " + line);
        WriteObject("");
        var patch = repo.Diff.Compare<Patch>(c.Parents.FirstOrDefault()?.Tree, c.Tree);
        foreach (var line in patch.Content.TrimEnd('\n').Split('\n')) WriteObject(line);
    }

    void BranchCmd(GitArgs a)
    {
        bool all = a.Flag("-a", "--all");
        var delete = a.Value("-d", "--delete") ?? a.Value("-D");
        using var repo = Open();
        if (delete is not null)
        {
            repo.Branches.Remove(delete);
            WriteObject($"Deleted branch {delete}.");
            return;
        }
        if (a.Rest.Count > 0)
        {
            repo.CreateBranch(a.Rest[0], a.Rest.Count > 1 ? a.Rest[1] : "HEAD");
            return;
        }
        foreach (var b in repo.Branches.Where(b => all || !b.IsRemote).OrderBy(b => b.IsRemote).ThenBy(b => b.FriendlyName))
            WriteObject($"{(b.IsCurrentRepositoryHead ? "* " : "  ")}{(b.IsRemote ? "remotes/" : "")}{b.FriendlyName}");
    }

    void Checkout(GitArgs a, string createFlag)
    {
        var create = a.Value(createFlag);
        using var repo = Open();
        if (create is not null)
        {
            var from = a.Rest.FirstOrDefault() ?? "HEAD";
            var b = repo.CreateBranch(create, from);
            Commands.Checkout(repo, b);
            WriteObject($"Switched to a new branch '{create}'");
            return;
        }
        if (a.Rest.Count == 0) throw new PSArgumentException("usage: git checkout <branch|commit>");
        var name = a.Rest[0];
        var local = repo.Branches[name];
        if (local is null && repo.Branches[$"origin/{name}"] is { IsRemote: true } remote)
        {
            // DWIM like git: create a local branch tracking origin/<name>.
            local = repo.CreateBranch(name, remote.Tip);
            repo.Branches.Update(local, u => u.TrackedBranch = remote.CanonicalName);
        }
        if (local is not null) { Commands.Checkout(repo, local); WriteObject($"Switched to branch '{name}'"); }
        else { Commands.Checkout(repo, name); WriteObject($"HEAD is now at {repo.Head.Tip.Sha[..7]}"); }
    }

    void Fetch(GitArgs a)
    {
        using var repo = Open();
        var remote = repo.Network.Remotes[a.Rest.FirstOrDefault() ?? "origin"] ?? throw new PSArgumentException("no such remote");
        Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification), FetchOpts(), null);
        CredentialsWorked();
    }

    void Pull(GitArgs a)
    {
        using var repo = Open();
        var result = Commands.Pull(repo, Me(repo), new PullOptions { FetchOptions = FetchOpts() });
        CredentialsWorked();
        WriteObject(result.Status switch
        {
            MergeStatus.UpToDate => "Already up to date.",
            MergeStatus.FastForward => $"Fast-forward to {result.Commit?.Sha[..7]}",
            MergeStatus.NonFastForward => $"Merge made: {result.Commit?.Sha[..7]}",
            MergeStatus.Conflicts => "CONFLICT: fix conflicts, then git add and git commit.",
            _ => result.Status.ToString(),
        });
    }

    void Push(GitArgs a)
    {
        bool setUpstream = a.Flag("-u", "--set-upstream");
        bool force = a.Flag("-f", "--force");
        using var repo = Open();
        var remoteName = a.Rest.Count > 0 ? a.Rest[0] : repo.Head.RemoteName ?? "origin";
        var branch = a.Rest.Count > 1 ? a.Rest[1] : repo.Head.FriendlyName;
        var remote = repo.Network.Remotes[remoteName] ?? throw new PSArgumentException($"no such remote '{remoteName}'");
        string? error = null;
        var options = new PushOptions
        {
            CredentialsProvider = Credentials(),
            OnPushStatusError = e => error = $"{e.Reference}: {e.Message}",
        };
        repo.Network.Push(remote, $"{(force ? "+" : "")}refs/heads/{branch}:refs/heads/{branch}", options);
        if (error is not null) throw new PSInvalidOperationException($"push rejected: {error}");
        CredentialsWorked();
        if (setUpstream)
        {
            repo.Branches.Update(repo.Branches[branch], u => u.Remote = remoteName, u => u.UpstreamBranch = $"refs/heads/{branch}");
            WriteObject($"branch '{branch}' set up to track '{remoteName}/{branch}'.");
        }
        WriteObject($"To {remote.PushUrl}\n   {branch} -> {branch}");
    }

    void RemoteCmd(GitArgs a)
    {
        bool verbose = a.Flag("-v", "--verbose");
        using var repo = Open();
        var sub = a.Rest.FirstOrDefault();
        switch (sub)
        {
            case null:
                foreach (var r in repo.Network.Remotes)
                {
                    if (verbose) { WriteObject($"{r.Name}\t{r.Url} (fetch)"); WriteObject($"{r.Name}\t{r.PushUrl} (push)"); }
                    else WriteObject(r.Name);
                }
                break;
            case "add" when a.Rest.Count == 3: repo.Network.Remotes.Add(a.Rest[1], a.Rest[2]); break;
            case "remove" or "rm" when a.Rest.Count == 2: repo.Network.Remotes.Remove(a.Rest[1]); break;
            case "get-url" when a.Rest.Count == 2: WriteObject(repo.Network.Remotes[a.Rest[1]]?.Url); break;
            case "set-url" when a.Rest.Count == 3: repo.Network.Remotes.Update(a.Rest[1], u => u.Url = a.Rest[2]); break;
            default: throw new PSArgumentException("usage: git remote [-v] | add <name> <url> | remove <name> | get-url <name> | set-url <name> <url>");
        }
    }

    void Config(GitArgs a)
    {
        bool global = a.Flag("--global");
        bool list = a.Flag("-l", "--list");
        var discovered = global ? null : Repository.Discover(Cwd);
        if (global || discovered is null)
        {
            // libgit2 only writes global values once ~/.gitconfig exists, and snapshots the file set when opened.
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig");
            if (!File.Exists(file)) File.WriteAllText(file, "");
        }
        using var repo = discovered is null ? null : new Repository(discovered);
        using var standalone = repo is null ? LibGit2Sharp.Configuration.BuildFrom(null) : null;
        var cfg = repo?.Config ?? standalone!;
        var level = global || repo is null ? ConfigurationLevel.Global : ConfigurationLevel.Local;
        if (list)
        {
            foreach (var e in cfg) WriteObject($"{e.Key}={e.Value}");
            return;
        }
        if (a.Rest.Count == 1) WriteObject(cfg.Get<string>(a.Rest[0])?.Value);
        else if (a.Rest.Count == 2) cfg.Set(a.Rest[0], a.Rest[1], level);
        else throw new PSArgumentException("usage: git config [--global] <key> [<value>] | --list");
    }

    void Reset(GitArgs a)
    {
        bool hard = a.Flag("--hard"), soft = a.Flag("--soft");
        a.Flag("--mixed");
        var mode = hard ? ResetMode.Hard : soft ? ResetMode.Soft : ResetMode.Mixed;
        using var repo = Open();
        var target = repo.Lookup<Commit>(a.Rest.FirstOrDefault() ?? "HEAD") ?? throw new PSArgumentException("unknown revision");
        repo.Reset(mode, target);
        if (mode == ResetMode.Hard) WriteObject($"HEAD is now at {target.Sha[..7]} {target.MessageShort}");
    }

    void Tag(GitArgs a)
    {
        using var repo = Open();
        if (a.Rest.Count == 0) { foreach (var t in repo.Tags) WriteObject(t.FriendlyName); return; }
        repo.ApplyTag(a.Rest[0], a.Rest.Count > 1 ? a.Rest[1] : "HEAD");
    }

    void RevParse(GitArgs a)
    {
        bool abbrev = a.Flag("--abbrev-ref");
        bool top = a.Flag("--show-toplevel");
        using var repo = Open();
        if (top) { WriteObject(repo.Info.WorkingDirectory.TrimEnd('/', '\\')); return; }
        var rev = a.Rest.FirstOrDefault() ?? "HEAD";
        if (abbrev && rev == "HEAD") { WriteObject(repo.Head.FriendlyName); return; }
        WriteObject(repo.Lookup(rev)?.Sha ?? throw new PSArgumentException($"unknown revision '{rev}'"));
    }
}

/// <summary>Unix-style argument list: take flags/values by name, the rest are positionals.</summary>
sealed class GitArgs(IEnumerable<string> args)
{
    public List<string> Rest { get; } = args.ToList();

    public bool Flag(params string[] names)
    {
        int i = Rest.FindIndex(names.Contains);
        if (i < 0) return false;
        Rest.RemoveAt(i);
        return true;
    }

    public string? Value(params string[] names)
    {
        for (int i = 0; i < Rest.Count; i++)
        {
            foreach (var n in names)
            {
                if (Rest[i] == n && i + 1 < Rest.Count)
                {
                    var v = Rest[i + 1];
                    Rest.RemoveRange(i, 2);
                    return v;
                }
                if (n.StartsWith("--") && Rest[i].StartsWith(n + "="))
                {
                    var v = Rest[i][(n.Length + 1)..];
                    Rest.RemoveAt(i);
                    return v;
                }
            }
        }
        return null;
    }
}
