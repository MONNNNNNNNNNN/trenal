using System.Management.Automation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Foundation;
using UIKit;

namespace Trenal.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        RegisterPslNative();
        Console.SetOut(ConsoleRouter.Out);
        Console.SetError(ConsoleRouter.Out);
        return true;
    }

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role);

    /// <summary>
    /// Native libraries ship as Frameworks/&lt;name&gt;.framework/&lt;name&gt; (native/*.sh):
    /// libpsl-native for PowerShell, git2-&lt;sha&gt; for LibGit2Sharp. Point every P/Invoke
    /// of such a name at its framework binary.
    /// </summary>
    static void RegisterPslNative()
    {
        var frameworks = NSBundle.MainBundle.PrivateFrameworksPath!;
        IntPtr Resolve(string name)
        {
            var path = Path.Combine(frameworks, name + ".framework", name);
            return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        }

        // SMA has no resolver of its own; LibGit2Sharp does, and falls back to the ALC event.
        NativeLibrary.SetDllImportResolver(typeof(PSObject).Assembly, (name, _, _) => Resolve(name));
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) => Resolve(name);
    }

    public static string PowerShellVersion
    {
        get
        {
            var v = typeof(PSObject).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            int cut = v.IndexOfAny([' ', '+']);
            return cut > 0 ? v[..cut] : v;
        }
    }
}

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    // Each window (Stage Manager / Split View) gets its own independent PowerShell session.
    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window = new UIWindow(windowScene) { RootViewController = new TerminalViewController(windowScene) };
        Window.MakeKeyAndVisible();
    }
}
