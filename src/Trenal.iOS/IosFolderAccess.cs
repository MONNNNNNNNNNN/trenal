using CoreFoundation;
using Foundation;
using Trenal.Core;
using UIKit;
using UniformTypeIdentifiers;

namespace Trenal.iOS;

/// <summary>
/// Mount-Folder on iOS: the system folder picker (iCloud Drive, On My iPad, other apps' folders,
/// USB drives, SMB servers added in Files) plus bookmarks so access survives app restarts.
/// </summary>
sealed class IosFolderAccess(UIViewController presenter, string file) : JsonFolderAccess(file)
{
    public override string? PickFolder()
    {
        var done = new TaskCompletionSource<NSUrl?>();
        PickerDelegate? keepAlive = null;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            var picker = new UIDocumentPickerViewController([UTTypes.Folder], asCopy: false)
            {
                AllowsMultipleSelection = false,
            };
            keepAlive = new PickerDelegate(done);
            picker.Delegate = keepAlive;
            presenter.PresentViewController(picker, true, null);
        });
        var url = done.Task.GetAwaiter().GetResult();
        GC.KeepAlive(keepAlive);
        if (url is null || !url.StartAccessingSecurityScopedResource()) return null;
        return url.Path;
    }

    public override void Remember(string name, string path)
    {
        var url = NSUrl.FromFilename(path);
        var data = url.CreateBookmarkData(0, [], null, out var error);
        var all = Load();
        all[name] = new Entry(path, error is null && data is not null ? data.GetBase64EncodedString(0) : null);
        Save(all);
    }

    public override IReadOnlyList<(string Name, string Path)> Restore()
    {
        var result = new List<(string, string)>();
        var all = Load();
        bool changed = false;
        foreach (var (name, entry) in all.ToList())
        {
            if (entry.Bookmark is null)
            {
                result.Add((name, entry.Path));
                continue;
            }
            var url = NSUrl.FromBookmarkData(new NSData(entry.Bookmark, NSDataBase64DecodingOptions.None),
                0, null, out bool stale, out var error);
            if (url is null || error is not null || !url.StartAccessingSecurityScopedResource()) continue;
            if (stale || url.Path != entry.Path)
            {
                var fresh = url.CreateBookmarkData(0, [], null, out var e2);
                all[name] = new Entry(url.Path!, e2 is null && fresh is not null ? fresh.GetBase64EncodedString(0) : entry.Bookmark);
                changed = true;
            }
            result.Add((name, url.Path!));
        }
        if (changed) Save(all);
        return result;
    }

    sealed class PickerDelegate(TaskCompletionSource<NSUrl?> done) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) =>
            done.TrySetResult(urls.FirstOrDefault());

        public override void WasCancelled(UIDocumentPickerViewController controller) => done.TrySetResult(null);
    }
}
