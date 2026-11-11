using System.IO;
using System.Windows;

namespace Orchard;

public static class Program
{
    /// <summary>
    /// No arguments: the app's window. Otherwise, the same work without a window (for scripts and for Claude):
    ///   Orchard.exe new "My App"                 makes a new app, prints its folder
    ///   Orchard.exe build "folder" [--publish]   builds it on a cloud Mac; --publish allows the first build to make the public GitHub page
    ///   Orchard.exe --selftest                   checks itself
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            new Application().Run(new MainWindow());
            return 0;
        }
        try
        {
            return Cli(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static async Task<int> Cli(string[] args)
    {
        switch (args[0])
        {
            case "--selftest":
                return SelfTest.Run();
            case "new" when args.Length == 2:
                Directory.CreateDirectory(Cloud.AppsFolder());
                Console.WriteLine(await Cloud.NewApp(Cloud.AppsFolder(), args[1]));
                return 0;
            case "build" when args.Length >= 2:
                var allowed = args.Contains("--publish");
                var dir = Path.GetFullPath(args[1]);
                var version = Cloud.Version(dir);
                var run = await Cloud.Build(dir, Console.WriteLine, _ =>
                {
                    if (!allowed) Console.WriteLine("This app isn't on GitHub yet. Add --publish to let the build make its public GitHub page.");
                    return allowed;
                });
                if (run == null) return 1;
                await Cloud.Screenshots(dir, run, version, Console.WriteLine);
                return 0;
            default:
                Console.Error.WriteLine("Usage: Orchard.exe [new \"App Name\" | build \"folder\" [--publish] | --selftest]");
                return 1;
        }
    }
}
