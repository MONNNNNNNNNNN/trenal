using System.Text.Json;
using CoreGraphics;
using Foundation;
using GameController;
using Trenal.Core;
using UIKit;
using WebKit;

namespace Trenal.iOS;

public sealed class TerminalViewController(UIWindowScene scene) : UIViewController
{
    static readonly UIColor Background = UIColor.FromRGB(0x1a, 0x1b, 0x26);

    WKWebView web = null!;
    WebTerminal terminal = null!;
    Shell? shell;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        View!.BackgroundColor = Background;

        var config = new WKWebViewConfiguration();
        config.UserContentController.AddScriptMessageHandler(new Bridge(this), "term");
        web = new WKWebView(CGRect.Empty, config)
        {
            Opaque = false,
            BackgroundColor = Background,
            TranslatesAutoresizingMaskIntoConstraints = false,
            NavigationDelegate = new LocalOnly(),
        };
        web.ScrollView.ScrollEnabled = false;
        web.ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
#if DEBUG
        if (OperatingSystem.IsIOSVersionAtLeast(16, 4)) web.Inspectable = true;
#endif
        View.AddSubview(web);

        // KeyboardLayoutGuide shrinks the terminal above the on-screen keyboard, so xterm.js
        // just refits; with a hardware keyboard it sits at the safe-area bottom.
        var safe = View.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints([
            web.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor),
            web.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
            web.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
            web.BottomAnchor.ConstraintEqualTo(View.KeyboardLayoutGuide.TopAnchor),
        ]);

        terminal = new WebTerminal(web, title => scene.Title = title);
        var dir = NSBundle.MainBundle.BundleUrl.Append("web", isDirectory: true);
        web.LoadFileUrl(dir.Append("index.html", isDirectory: false), dir);

        NSNotificationCenter.DefaultCenter.AddObserver(GCKeyboard.DidConnectNotification, _ => SyncHardwareKeyboard());
        NSNotificationCenter.DefaultCenter.AddObserver(GCKeyboard.DidDisconnectNotification, _ => SyncHardwareKeyboard());
    }

    void SyncHardwareKeyboard() =>
        web.EvaluateJavaScript($"trenal.setHardwareKeyboard({(GCKeyboard.CoalescedKeyboard is null ? "false" : "true")})", null!);

    internal void OnMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement;
        switch (msg.GetProperty("t").GetString())
        {
            case "i":
                shell?.Input(msg.GetProperty("d").GetString() ?? "");
                break;
            case "r":
                terminal.Resize(msg.GetProperty("c").GetInt32(), msg.GetProperty("r").GetInt32());
                break;
            case "title":
                scene.Title = msg.GetProperty("v").GetString() ?? "";
                break;
            case "ready":
                terminal.Resize(msg.GetProperty("c").GetInt32(), msg.GetProperty("r").GetInt32());
                SyncHardwareKeyboard();
                StartShell();
                break;
        }
    }

    void StartShell()
    {
        if (shell is not null) return; // page reloads keep the running session
        ConsoleRouter.Target = terminal;

        var home = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path!;
        var library = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.LibraryDirectory, NSSearchPathDomain.User)[0].Path!;
        var builtIn = Path.Combine(NSBundle.MainBundle.BundlePath, "Modules");
        var user = Path.Combine(home, ".local", "share", "powershell", "Modules");

        shell = new Shell(terminal, new ShellOptions
        {
            Environment = new Dictionary<string, string?>
            {
                ["HOME"] = home,
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
                ["PSModulePath"] = $"{user}:{builtIn}",
                // iOS can't start processes; an empty PATH makes that a clean "not recognized"
                // instead of PowerShell finding /bin binaries it can never run.
                ["PATH"] = "",
            },
            Banner = $"\x1b[1mtrenal\x1b[0m · PowerShell {AppDelegate.PowerShellVersion} running on this iPad, offline\n"
                + "\x1b[2mfiles: Files › On My iPad › trenal · Mount-Folder icloud to pick any folder · exit starts a new session\x1b[0m",
            HistoryFile = Path.Combine(home, ".local", "share", "trenal", "history.txt"),
            RestartOnExit = true,
            // Bookmarks live in Library (not Documents) so they aren't exposed in the Files app.
            Folders = new IosFolderAccess(this, Path.Combine(library, "trenal", "mounts.json")),
        });
        shell.Start();
    }

    sealed class Bridge(TerminalViewController owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body is NSString s) owner.OnMessage(s.ToString());
        }
    }

    /// <summary>The page may only load bundled files; tapped links open in Safari.</summary>
    sealed class LocalOnly : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url;
            if (url?.IsFileUrl == true)
            {
                decisionHandler(WKNavigationActionPolicy.Allow);
                return;
            }
            if (url is not null && navigationAction.NavigationType == WKNavigationType.LinkActivated)
                UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
            decisionHandler(WKNavigationActionPolicy.Cancel);
        }
    }
}
