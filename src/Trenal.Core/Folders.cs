using System.Management.Automation;
using System.Text.Json;

namespace Trenal.Core;

/// <summary>
/// Access to folders outside the app sandbox. On iOS this is the system folder picker plus
/// security-scoped bookmarks (iCloud Drive, other apps, USB drives, SMB shares); elsewhere
/// folders are just paths.
/// </summary>
public interface IFolderAccess
{
    /// <summary>Asks the user for a folder. Called on the engine thread; returns null if cancelled or unsupported.</summary>
    string? PickFolder();

    /// <summary>Persists access so the mount survives restarts.</summary>
    void Remember(string name, string path);

    void Forget(string name);

    /// <summary>Re-opens remembered folders at session start; entries whose access can't be restored are skipped.</summary>
    IReadOnlyList<(string Name, string Path)> Restore();

    /// <summary>Remembered mounts, without touching access.</summary>
    IReadOnlyList<(string Name, string Path)> List();
}

/// <summary>Plain-path mounts stored as JSON. Base for platforms that add a picker/bookmarks.</summary>
public class JsonFolderAccess(string file) : IFolderAccess
{
    protected string File { get; } = file;

    public virtual string? PickFolder() => null;

    public virtual void Remember(string name, string path)
    {
        var all = Load();
        all[name] = new Entry(path, null);
        Save(all);
    }

    public void Forget(string name)
    {
        var all = Load();
        if (all.Remove(name)) Save(all);
    }

    public virtual IReadOnlyList<(string Name, string Path)> Restore() => List();

    public IReadOnlyList<(string Name, string Path)> List() =>
        Load().Select(kv => (kv.Key, kv.Value.Path)).ToList();

    protected record Entry(string Path, string? Bookmark);

    protected Dictionary<string, Entry> Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(System.IO.File.ReadAllText(File)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    protected void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(all));
    }
}

/// <summary>What trenal's cmdlets reach through <c>$Host.PrivateData</c>.</summary>
public sealed class HostServices(IFolderAccess folders)
{
    public IFolderAccess Folders { get; } = folders;

    internal static HostServices From(PSCmdlet cmdlet) =>
        cmdlet.Host.PrivateData?.BaseObject as HostServices
        ?? throw new PSInvalidOperationException("This command needs the trenal host.");
}

static class Drives
{
    public static PSDriveInfo Mount(SessionState state, string name, string root)
    {
        if (state.Drive.GetAll().Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            state.Drive.Remove(name, force: true, "Global");
        var provider = state.Provider.GetOne("FileSystem");
        return state.Drive.New(new PSDriveInfo(name, provider, root, "trenal mounted folder", null), "Global");
    }
}

[Cmdlet(VerbsData.Mount, "Folder")]
[OutputType(typeof(PSObject))]
public sealed class MountFolderCommand : PSCmdlet
{
    /// <summary>Drive name, e.g. <c>icloud</c> gives <c>icloud:</c>.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidatePattern("^[A-Za-z][A-Za-z0-9_-]*$")]
    public string Name { get; set; } = "";

    /// <summary>Folder to mount. Omit to choose one with the system picker (iOS).</summary>
    [Parameter(Position = 1)]
    public string? Path { get; set; }

    protected override void EndProcessing()
    {
        var folders = HostServices.From(this).Folders;
        string? root = Path is null ? folders.PickFolder() : SessionState.Path.GetUnresolvedProviderPathFromPSPath(Path);
        if (root is null)
        {
            WriteError(new ErrorRecord(new OperationCanceledException("No folder chosen. On this platform pass -Path."),
                "Trenal.MountCancelled", ErrorCategory.OperationStopped, Name));
            return;
        }
        if (!Directory.Exists(root))
        {
            WriteError(new ErrorRecord(new DirectoryNotFoundException(root), "Trenal.MountMissing", ErrorCategory.ObjectNotFound, root));
            return;
        }
        Drives.Mount(SessionState, Name, root);
        folders.Remember(Name, root);
        WriteObject(Describe(Name, root));
    }

    internal static PSObject Describe(string name, string root)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("Name", name + ":"));
        o.Properties.Add(new PSNoteProperty("Root", root));
        return o;
    }
}

[Cmdlet(VerbsData.Dismount, "Folder")]
public sealed class DismountFolderCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    protected override void EndProcessing()
    {
        var name = Name.TrimEnd(':');
        if (SessionState.Drive.GetAll().Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            SessionState.Drive.Remove(name, force: true, "Global");
        HostServices.From(this).Folders.Forget(name);
    }
}

[Cmdlet(VerbsCommon.Get, "MountedFolder")]
public sealed class GetMountedFolderCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        foreach (var (name, root) in HostServices.From(this).Folders.List())
            WriteObject(MountFolderCommand.Describe(name, root));
    }
}
