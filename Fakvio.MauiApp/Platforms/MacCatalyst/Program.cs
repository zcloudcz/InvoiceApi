using UIKit;

namespace Fakvio.MauiApp.Platforms.MacCatalyst;

/// <summary>
/// macOS Catalyst program entry point — launches the UIApplication with AppDelegate.
/// </summary>
public class Program
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
