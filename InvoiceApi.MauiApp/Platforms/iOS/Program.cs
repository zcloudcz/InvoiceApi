using UIKit;

namespace InvoiceApi.MauiApp.Platforms.iOS;

/// <summary>
/// iOS program entry point — launches the UIApplication with AppDelegate.
/// </summary>
public class Program
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
