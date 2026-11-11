using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orchard;

/// <summary>Making new apps and building them on GitHub's Macs, through the git and gh command-line tools.</summary>
public static class Cloud
{
    public static async Task<(int Code, string Out, string Err)> Run(string dir, string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never stop to ask a question in a window nobody can see.
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await o).Trim(), (await e).Trim());
    }

    /// <summary>One plain sentence about what's missing on this computer, or null when all is ready.</summary>
    public static async Task<string?> MissingTools()
    {
        var here = Environment.CurrentDirectory;
        try { await Run(here, "git", "--version"); }
        catch { return "Git isn't installed. Get it from git-scm.com, then start Orchard again."; }
        try
        {
            if ((await Run(here, "gh", "auth", "status")).Code != 0)
                return "You're not signed in to GitHub. Open a terminal, run \"gh auth login\", then start Orchard again.";
        }
        catch { return "The GitHub tool (gh) isn't installed. Get it from cli.github.com, then start Orchard again."; }
        return null;
    }

    /// <summary>Where new apps go: beside Orchard's own folder when that's a "Projects" folder, else Documents.</summary>
    public static string AppsFolder()
    {
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath)!);
        return parent != null && Path.GetFileName(parent) == "Projects"
            ? parent
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Orchard Apps");
    }

    public static async Task<string> NewApp(string root, string name)
    {
        name = name.Trim();
        var id = Regex.Replace(name, "[^A-Za-z0-9]", "");
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9 \-]*$"))
            throw new InvalidOperationException("An app's name starts with a letter and uses only letters, numbers, spaces and dashes.");
        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"There's already a folder called \"{name}\".");

        var login = (await Run(Path.GetTempPath(), "gh", "api", "user", "-q", ".login")).Out;
        if (login == "") throw new InvalidOperationException("Couldn't read your GitHub name. Check that you're signed in (gh auth login).");
        var bundle = $"com.{Regex.Replace(login, "[^A-Za-z0-9]", "").ToLowerInvariant()}.{id.ToLowerInvariant()}";

        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(r => r.StartsWith("Template/")))
        {
            var dest = Path.Combine(dir, res["Template/".Length..].Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
            // Mac-style line endings: the cloud Mac can't run the build steps with Windows ones.
            File.WriteAllText(dest, reader.ReadToEnd().Replace("\r\n", "\n")
                .Replace("{NAME}", name).Replace("{ID}", id).Replace("{BUNDLE}", bundle)
                .Replace("{LOGIN}", login).Replace("{YEAR}", DateTime.Now.Year.ToString()));
        }
        var screen = Path.Combine(dir, "Sources", "ContentView.screen.json");
        File.WriteAllText(Path.ChangeExtension(Path.ChangeExtension(screen, null), ".swift"),
            SwiftGen.Screen("ContentView", JsonSerializer.Deserialize<Node>(File.ReadAllText(screen))!));

        await Must(dir, "git", "init", "-b", "main");
        await Must(dir, "git", "add", "-A");
        await Must(dir, "git", "commit", "-m", $"Start {name}");
        return dir;
    }

    public static string Version(string dir) =>
        Regex.Match(File.ReadAllText(Path.Combine(dir, "project.yml")), @"MARKETING_VERSION:\s*(\S+)").Groups[1].Value;

    public static void SetVersion(string dir, string version)
    {
        var file = Path.Combine(dir, "project.yml");
        File.WriteAllText(file, Regex.Replace(File.ReadAllText(file), @"(MARKETING_VERSION:\s*)\S+", "${1}" + version));
    }

    public static async Task<bool> HasGitHub(string dir) => (await Run(dir, "git", "remote", "get-url", "origin")).Code == 0;

    /// <summary>
    /// Gives an app made by an older Orchard the current cloud build steps. True when the file was replaced.
    /// A file whose first line the owner changed is theirs and is left alone.
    /// </summary>
    public static bool UpdateBuildSteps(string dir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var reader = new StreamReader(asm.GetManifestResourceStream(asm.GetManifestResourceNames().First(r => r.EndsWith("build.yml")))!);
        var ours = reader.ReadToEnd().Replace("\r\n", "\n");
        int Number(string line) => Regex.Match(line, @"^# Orchard build steps (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;

        var file = Path.Combine(dir, ".github", "workflows", "build.yml");
        if (File.Exists(file))
        {
            var first = File.ReadLines(file).FirstOrDefault() ?? "";
            // Before the steps were numbered, the file started with "# Builds this app on".
            var theirs = first.StartsWith("# Builds this app on") ? 0 : Number(first);
            if (theirs < 0 || theirs >= Number(ours)) return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ours);
        return true;
    }

    /// <summary>What a cloud Mac is doing right now, in the words of its build steps.</summary>
    static string Doing(JsonElement job)
    {
        if (job.GetProperty("status").GetString() == "completed") return job.GetProperty("conclusion").GetString() == "success" ? "finished" : "didn't work";
        var now = job.GetProperty("steps").EnumerateArray().FirstOrDefault(s => s.GetProperty("status").GetString() == "in_progress");
        if (now.ValueKind == JsonValueKind.Object) return now.GetProperty("name").GetString() ?? "";
        // Between two steps there is nothing to say; before the first, GitHub hasn't found a free Mac yet.
        return job.GetProperty("status").GetString() == "queued" ? "waiting in line for a free Mac" : "";
    }

    /// <summary>The app's one-word name, which starts every built file's name.</summary>
    public static string Id(string dir) =>
        Regex.Match(File.ReadAllText(Path.Combine(dir, "project.yml")), @"(?m)^name:\s*(\S+)").Groups[1].Value;

    /// <summary>
    /// Commits, pushes, waits for the cloud Mac, then archives and releases the apps it built.
    /// Returns the cloud run's number (hand it to Screenshots), or null when no build came out.
    /// </summary>
    public static async Task<string?> Build(string dir, Action<string> log, Func<string, bool> confirmPublic)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        var id = Id(dir);
        var version = Version(dir);
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
        {
            log($"\"{version}\" isn't a version number. Use three numbers, like 1.0.0.");
            return null;
        }

        // A version that's already built is never built again: the archive keeps every build exactly as it was.
        var archive = Path.Combine(dir, "Versions", "Archive");
        var onGitHub = await HasGitHub(dir);
        if ((Directory.Exists(archive) && Directory.GetFiles(archive, $"{id}-{version}-*").Length > 0)
            || (onGitHub && (await Run(dir, "gh", "release", "view", "v" + version)).Code == 0))
        {
            log($"Version {version} is already built and kept. Raise the version number to build again.");
            return null;
        }

        if (UpdateBuildSteps(dir)) log("This app was made with an older Orchard. It now has the newer build steps, which deliver the apps before the screenshots.");
        await Must(dir, "git", "add", "-A");
        await Must(dir, "git", "commit", "--allow-empty", "-m", $"Build {version}");
        if (!onGitHub)
        {
            var repo = Regex.Replace(name, @"[^A-Za-z0-9._\-]", "-");
            if (!confirmPublic($"The first build puts this app's code on GitHub as a new public page called \"{repo}\", where anyone can see it. " +
                               "Public pages get free cloud Mac builds.\n\nGo ahead?"))
            {
                log("Build cancelled: nothing was put on GitHub.");
                return null;
            }
            log("Making the app's GitHub page...");
            await Must(dir, "gh", "repo", "create", repo, "--public", "--source", ".", "--remote", "origin", "--push");
        }
        else
        {
            log("Sending the code to GitHub...");
            var push = await Run(dir, "git", "push", "origin", "HEAD");
            if (push.Code != 0)
            {
                log("GitHub didn't take the code. If you changed this app on another computer, open the app again to fetch those changes first.");
                log(push.Err);
                return null;
            }
        }

        var sha = (await Must(dir, "git", "rev-parse", "HEAD")).Out;
        log("Waiting for a cloud Mac...");
        string run = "";
        for (var i = 0; i < 36 && run == ""; i++)
        {
            await Task.Delay(5000);
            var list = await Run(dir, "gh", "run", "list", "--commit", sha, "--json", "databaseId", "-L", "1");
            if (list.Code == 0 && JsonDocument.Parse(list.Out).RootElement is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0)
                run = a[0].GetProperty("databaseId").GetInt64().ToString();
        }
        if (run == "")
        {
            log("GitHub never started the build. Check the app's GitHub page, under Actions.");
            return null;
        }

        // Only the "build" job is waited for here. The screenshot jobs that follow it take minutes longer (see Screenshots).
        string step = "", conclusion = "", url = "";
        while (conclusion == "")
        {
            await Task.Delay(8000);
            var view = await Run(dir, "gh", "run", "view", run, "--json", "status,conclusion,url,jobs");
            if (view.Code != 0) continue;
            var json = JsonDocument.Parse(view.Out).RootElement;
            url = json.GetProperty("url").GetString() ?? "";
            var job = json.GetProperty("jobs").EnumerateArray().FirstOrDefault(j => j.GetProperty("name").GetString() == "build");
            if (job.ValueKind == JsonValueKind.Object)
            {
                if (job.GetProperty("status").GetString() == "completed") conclusion = job.GetProperty("conclusion").GetString() ?? "failure";
                else if (Doing(job) is var n && n != "" && n != step) log("Cloud Mac: " + (step = n));
            }
            // No build job at all, yet the run is over: the workflow file itself was refused.
            else if (json.GetProperty("status").GetString() == "completed") conclusion = "failure";
        }

        if (conclusion != "success")
        {
            // What went wrong can only be read once the whole run has stopped, which is at once: the screenshot jobs are skipped.
            await WaitForRun(dir, run);
            var failed = (await Run(dir, "gh", "run", "view", run, "--log-failed")).Out.Split('\n')
                // Each line starts with the job, the step and a timestamp; only what the Mac said is kept.
                .Select(l => Regex.Replace(l.TrimEnd(), @"^.*?\d{4}-\d\d-\d\dT[\d:.]+Z ", ""))
                .Where(l => l.Trim() != "")
                .ToList();
            var errors = failed.Where(l => l.Contains("error:")).Distinct().Take(40).ToList();
            log("The build didn't work. " + (errors.Count > 0 ? "Double-click a line to jump to it:" : failed.Count > 0 ? "The last thing the Mac said:" : "The Mac left no message."));
            foreach (var l in errors.Count > 0 ? errors : failed.TakeLast(20))
                // Paths are shown from the app's folder, not the cloud Mac's.
                log("  " + Regex.Replace(l, @"/Users/runner/work/[^/]+/[^/]+/", ""));
            log("The full story is at " + url);
            return null;
        }

        log("Fetching the finished app...");
        var temp = Path.Combine(Path.GetTempPath(), "orchard-" + run);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        await Must(dir, "gh", "run", "download", run, "-n", "app", "-D", temp);
        Directory.CreateDirectory(archive);
        var kept = new List<string>();
        foreach (var file in Directory.GetFiles(temp))
        {
            var dest = Path.Combine(archive, $"{id}-{version}-{Path.GetFileName(file)}");
            File.Move(file, dest);
            kept.Add(dest);
            // The newest app itself also sits in the app's folder, ready to hand to someone.
            File.Copy(dest, Path.Combine(dir, $"{id}-{Path.GetFileName(file)}"), true);
        }
        Directory.Delete(temp, true);

        log("Publishing the release...");
        await Must(dir, new[] { "gh", "release", "create", "v" + version }.Concat(kept).Concat(new[]
        {
            "--title", $"{name} {version}",
            "--notes", "Built on a GitHub cloud Mac by Orchard. The .ipa (iPhone and iPad) has no Apple signature: install it with a sideloading tool " +
                       "such as Sideloadly or AltStore. For the Mac, unzip macOS.zip, and the first time allow the app under System Settings > Privacy & Security.",
        }).ToArray());
        log($"Done: {name} {version} is built. The files are in Versions\\Archive and on the app's GitHub Releases page.");
        return run;
    }

    /// <summary>Waits for the run's screenshot jobs, keeps the pictures with that version's build and adds them to its release. Returns the iPhone picture, if any.</summary>
    public static async Task<string?> Screenshots(string dir, string run, string version, Action<string> log)
    {
        log("Screenshots are being taken on two more cloud Macs (about 8 minutes). You can keep working.");
        var said = new Dictionary<string, string>();
        for (var done = false; !done; await Task.Delay(done ? 0 : 10000))
        {
            var view = await Run(dir, "gh", "run", "view", run, "--json", "status,jobs");
            if (view.Code != 0) continue;
            var json = JsonDocument.Parse(view.Out).RootElement;
            // The two jobs are called "screenshots (iPhone)" and "screenshots (iPad)".
            foreach (var job in json.GetProperty("jobs").EnumerateArray())
                if (Regex.Match(job.GetProperty("name").GetString() ?? "", @"^screenshots \((.+)\)") is { Success: true } m
                    && Doing(job) is var now && now != "" && said.GetValueOrDefault(m.Groups[1].Value) != now)
                    log($"{m.Groups[1].Value} screenshot: {said[m.Groups[1].Value] = now}");
            done = json.GetProperty("status").GetString() == "completed";
        }
        var temp = Path.Combine(Path.GetTempPath(), "orchard-shots-" + run);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        var got = await Run(dir, "gh", "run", "download", run, "-p", "screenshot-*", "-D", temp);
        var kept = new List<string>();
        if (got.Code == 0)
            foreach (var file in Directory.GetFiles(temp, "*.png", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(dir, "Versions", "Archive", $"{Id(dir)}-{version}-{Path.GetFileName(file)}");
                File.Move(file, dest);
                kept.Add(dest);
            }
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        if (kept.Count == 0)
        {
            log("No screenshots came back this time. The apps themselves are fine.");
            return null;
        }
        await Must(dir, new[] { "gh", "release", "upload", "v" + version }.Concat(kept).ToArray());
        log("The screenshots are in Versions\\Archive and on the release.");
        return kept.Find(k => k.EndsWith("iPhone.png")) ?? kept[0];
    }

    static async Task WaitForRun(string dir, string run)
    {
        while ((await Run(dir, "gh", "run", "view", run, "--json", "status", "-q", ".status")).Out != "completed") await Task.Delay(10000);
    }

    static Task<(int Code, string Out, string Err)> Must(string dir, string exe, params string[] args) => Must(dir, new[] { exe }.Concat(args).ToArray());

    static async Task<(int Code, string Out, string Err)> Must(string dir, string[] command)
    {
        var r = await Run(dir, command[0], command[1..]);
        if (r.Code != 0) throw new InvalidOperationException($"\"{command[0]} {command[1]}\" didn't work: {(r.Err != "" ? r.Err : r.Out)}");
        return r;
    }
}
