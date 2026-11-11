using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;

namespace Orchard;

/// <summary>The app's window: files on the left, designer and code tabs in the middle, the cloud build's messages below.</summary>
public sealed class MainWindow : Window
{
    sealed class Doc
    {
        public string Path = "";
        public TabItem Tab = null!;
        public TextBlock Title = new();
        public bool Dirty;
        public DateTime Stamp;
        public Action Save = () => { }, Reload = () => { };
        public TextEditor? Editor;
    }

    static readonly string LastFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orchard", "last.txt");
    static readonly IHighlightingDefinition Swift = LoadSwift();

    string? app;
    readonly List<Doc> docs = new();
    readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    readonly TreeView tree = new() { BorderThickness = new Thickness(0) };
    readonly TabControl tabs = new();
    readonly ListBox log = new() { FontFamily = new FontFamily("Consolas"), BorderThickness = new Thickness(0) };
    readonly TextBox version = new() { Width = 64, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Button build;

    // While a cloud build is on its way, a line above the messages shows a running clock, so a long step never looks frozen.
    readonly TextBlock status = new() { Padding = new Thickness(6, 3, 6, 3), Background = Brushes.LemonChiffon, Foreground = Brushes.Black, Visibility = Visibility.Collapsed };
    readonly System.Windows.Threading.DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    int working;
    string news = "";
    DateTime started, newsAt;

    void Working(bool on)
    {
        working += on ? 1 : -1;
        if (on) started = newsAt = DateTime.Now;
        status.Visibility = working > 0 ? Visibility.Visible : Visibility.Collapsed;
        clock.IsEnabled = working > 0;
    }

    public MainWindow()
    {
        Title = "Orchard";
        Width = 1200;
        Height = 760;
        WindowState = WindowState.Maximized;

        var bar = new ToolBar();
        Button B(string text, Action act, string tip)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), ToolTip = tip };
            b.Click += (_, _) => Guard(act);
            bar.Items.Add(b);
            return b;
        }
        B("New app", NewApp, "Start a new iPhone, iPad and Mac app");
        B("Open app", () => { var d = new OpenFolderDialog { InitialDirectory = Cloud.AppsFolder() }; if (d.ShowDialog(this) == true) OpenApp(d.FolderName); }, "Open an app's folder");
        bar.Items.Add(new Separator());
        B("New screen", NewScreen, "Add a screen to design by dragging");
        B("New Swift file", NewSwift, "Add a file of your own code");
        B("Save all", SaveAll, "Ctrl+S");
        bar.Items.Add(new Separator());
        bar.Items.Add(new TextBlock { Text = "Version ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        bar.Items.Add(version);
        build = B("▶ Build on a cloud Mac", BuildNow, "Saves everything, builds the Mac and iPhone/iPad apps, takes screenshots, and keeps the result");
        build.FontWeight = FontWeights.Bold;
        B("Finished builds", () => { if (app != null) Process.Start("explorer.exe", Directory.CreateDirectory(Path.Combine(app, "Versions", "Archive")).FullName); }, "Open the folder of every build made so far");
        B("GitHub page", async () => { if (app != null && await Cloud.HasGitHub(app)) await Cloud.Run(app, "gh", "repo", "view", "--web"); else Log("This app isn't on GitHub yet. The first build puts it there."); }, "Open the app's page on GitHub");

        tree.MouseDoubleClick += (_, _) => { if (tree.SelectedItem is TreeViewItem { Tag: string p } && File.Exists(p)) Guard(() => Open(p)); };
        var menu = new ContextMenu();
        void M(string text, Action<string> act)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => { if (tree.SelectedItem is TreeViewItem { Tag: string p }) Guard(() => act(p)); };
            menu.Items.Add(item);
        }
        M("Show in File Explorer", p => Process.Start("explorer.exe", $"/select,\"{p}\""));
        M("Delete (to the Recycle Bin)", DeleteFile);
        tree.ContextMenu = menu;

        log.MouseDoubleClick += (_, _) =>
        {
            // A Swift error names its file and line, e.g. Sources/ContentView.swift:12:5: error: ...
            if (app != null && log.SelectedItem is string line && Regex.Match(line, @"(Sources[/\\][^:]+):(\d+)") is { Success: true } m)
                Guard(() => Open(Path.Combine(app, m.Groups[1].Value.Replace('/', '\\')), int.Parse(m.Groups[2].Value)));
        };

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
        var across = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var messages = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        messages.Children.Add(status);
        messages.Children.Add(log);
        Grid.SetRow(across, 1);
        Grid.SetRow(messages, 2);
        right.Children.Add(tabs);
        right.Children.Add(across);
        right.Children.Add(messages);
        static string Clock(DateTime since) => (DateTime.Now - since).ToString(@"m\:ss");
        clock.Tick += (_, _) => status.Text = $"Working: {Clock(started)} so far.  Latest news, {Clock(newsAt)} ago: {news}";

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition());
        var down = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(down, 1);
        Grid.SetColumn(right, 2);
        main.Children.Add(tree);
        main.Children.Add(down);
        main.Children.Add(right);

        var all = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        all.Children.Add(bar);
        all.Children.Add(main);
        Content = all;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => Guard(SaveAll)));
        Activated += (_, _) => Guard(PickUpOutsideChanges);
        Closing += (_, e) => e.Cancel = !MayDiscard() || (working > 0 && MessageBox.Show(this,
            "A cloud build is still on its way. If Orchard closes now, what the Mac makes is never collected and the build has to be started again.\n\nClose anyway?",
            "Orchard", MessageBoxButton.YesNo) != MessageBoxResult.Yes);
        Loaded += async (_, _) =>
        {
            Log("Welcome to Orchard. Start with New app, or open one you made before.");
            if (await Cloud.MissingTools() is { } missing) Log(missing);
            if (File.Exists(LastFile) && File.ReadAllText(LastFile) is var last && File.Exists(Path.Combine(last, "project.yml"))) Guard(() => OpenApp(last));
        };
    }

    static IHighlightingDefinition LoadSwift()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Swift.xshd")!;
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    /// <summary>Anything that goes wrong becomes one plain line in the messages, never a crash.</summary>
    void Guard(Action act)
    {
        try { act(); }
        catch (Exception ex) { Log("That didn't work: " + ex.Message); }
    }

    void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        log.Items.Add(line);
        log.ScrollIntoView(log.Items[^1]);
        news = line;
        newsAt = DateTime.Now;
    });

    // ---- apps ----

    async void NewApp()
    {
        var name = Ask("What's the app called?", "New app", "My App");
        if (name == "" || !MayDiscard()) return;
        try
        {
            Directory.CreateDirectory(Cloud.AppsFolder());
            OpenApp(await Cloud.NewApp(Cloud.AppsFolder(), name));
            Log($"Made \"{name.Trim()}\". Design its first screen, then press Build.");
        }
        catch (Exception ex) { Log("That didn't work: " + ex.Message); }
    }

    async void OpenApp(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "project.yml")))
        {
            Log("That folder isn't an Orchard app (it has no project.yml).");
            return;
        }
        if (docs.Count > 0 && !MayDiscard()) return;
        docs.Clear();
        tabs.Items.Clear();
        app = dir;
        Title = "Orchard: " + Path.GetFileName(dir);
        version.Text = Cloud.Version(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(LastFile)!);
        File.WriteAllText(LastFile, dir);
        RefreshTree();
        var first = Path.Combine(dir, "Sources", "ContentView.screen.json");
        if (File.Exists(first)) Open(first);

        // The same app may have been changed on another computer: fetch that before working on it.
        if (await Cloud.HasGitHub(dir))
        {
            var pull = await Cloud.Run(dir, "git", "pull", "--ff-only");
            if (pull.Code != 0) Log("Couldn't fetch the newest copy from GitHub: " + pull.Err);
            else if (!pull.Out.Contains("Already up to date")) Log("Fetched newer changes from GitHub.");
            PickUpOutsideChanges();
        }
    }

    string? AskName(string what)
    {
        if (app == null) { Log("Open or make an app first."); return null; }
        var name = Ask($"Name of the new {what} (one word starting with a capital, like Settings):", "New " + what);
        if (name == "") return null;
        if (!Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]*$")) { Log("Names are one word of letters and numbers, starting with a capital letter."); return null; }
        if (File.Exists(Path.Combine(app, "Sources", name + ".swift"))) { Log($"There's already a file called {name}."); return null; }
        return name;
    }

    void NewScreen()
    {
        if (AskName("screen") is not { } name) return;
        var file = Path.Combine(app!, "Sources", name + ".screen.json");
        File.WriteAllText(file, """{ "Kind": "VStack", "Props": { "spacing": "16", "padding": "24" }, "Children": [] }""");
        File.WriteAllText(Path.Combine(app!, "Sources", name + ".swift"), SwiftGen.Screen(name, new Node { Props = { ["spacing"] = "16", ["padding"] = "24" } }));
        RefreshTree();
        Open(file);
    }

    void NewSwift()
    {
        if (AskName("Swift file") is not { } name) return;
        var file = Path.Combine(app!, "Sources", name + ".swift");
        File.WriteAllText(file, "import SwiftUI\n\n");
        RefreshTree();
        Open(file);
    }

    void DeleteFile(string path)
    {
        if (!File.Exists(path)) { Log("Only files can be deleted here, not folders."); return; }
        if (MessageBox.Show(this, $"Move {Path.GetFileName(path)} to the Recycle Bin?", "Orchard", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (docs.Find(d => Same(d.Path, path)) is { } open) { docs.Remove(open); tabs.Items.Remove(open.Tab); }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        RefreshTree();
    }

    async void BuildNow()
    {
        if (app == null) { Log("Open or make an app first."); return; }
        SaveAll();
        PickUpOutsideChanges();
        if (version.Text.Trim() != Cloud.Version(app)) Cloud.SetVersion(app, version.Text.Trim());
        PickUpOutsideChanges();
        log.Items.Clear();
        build.IsEnabled = false;
        var dir = app;
        var building = Cloud.Version(dir);
        string? run = null;
        Working(true);
        try
        {
            run = await Task.Run(() => Cloud.Build(dir, Log,
                question => Dispatcher.Invoke(() => MessageBox.Show(this, question, "Orchard", MessageBoxButton.YesNo) == MessageBoxResult.Yes)));
            if (run != null && dir == app)
            {
                // That version is now taken, so the box moves on to the next one.
                var v = System.Version.Parse(building);
                version.Text = $"{v.Major}.{v.Minor}.{v.Build + 1}";
            }
        }
        catch (Exception ex) { Log("That didn't work: " + ex.Message); }
        finally
        {
            build.IsEnabled = true;
            RefreshTree();
        }
        // The apps are done and the Build button is free again; the pictures arrive a few minutes later.
        try
        {
            if (run == null) return;
            var shot = await Task.Run(() => Cloud.Screenshots(dir, run, building, Log));
            RefreshTree();
            if (shot != null && dir == app) Open(shot);
        }
        catch (Exception ex) { Log("The screenshots didn't arrive: " + ex.Message); }
        finally { Working(false); }
    }

    // ---- files and tabs ----

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void RefreshTree()
    {
        tree.Items.Clear();
        if (app == null) return;
        foreach (var item in Items(app)) tree.Items.Add(item);
    }

    IEnumerable<TreeViewItem> Items(string dir)
    {
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var name = Path.GetFileName(d);
            if (name is ".git" or "build") continue;
            var item = new TreeViewItem { Header = name, Tag = d, IsExpanded = name == "Sources" || expanded.Contains(d) };
            item.Expanded += (_, e) => { if (e.OriginalSource == item) expanded.Add(d); };
            item.Collapsed += (_, e) => { if (e.OriginalSource == item) expanded.Remove(d); };
            foreach (var child in Items(d)) item.Items.Add(child);
            yield return item;
        }
        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            yield return new TreeViewItem { Header = Path.GetFileName(f), Tag = f };
    }

    void Mark(Doc d, bool dirty)
    {
        d.Dirty = dirty;
        d.Title.Text = Path.GetFileName(d.Path) + (dirty ? " •" : "");
        if (!dirty && File.Exists(d.Path)) d.Stamp = File.GetLastWriteTimeUtc(d.Path);
    }

    void Open(string path, int line = 0)
    {
        var d = docs.Find(x => Same(x.Path, path));
        if (d == null)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".ipa" or ".zip")
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }
            d = new Doc { Path = path };
            var doc = d;
            FrameworkElement body;
            if (path.EndsWith(".screen.json", StringComparison.OrdinalIgnoreCase))
            {
                var designer = new Designer(path);
                designer.Changed += () => Mark(doc, true);
                d.Save = () => { designer.Save(); Mark(doc, false); };
                d.Reload = () => { designer.Reload(); Mark(doc, false); };
                body = designer;
            }
            else if (ext is ".png" or ".jpg" or ".jpeg")
            {
                // Loaded fully into memory so the file itself isn't held open.
                var picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.UriSource = new Uri(path);
                picture.EndInit();
                body = new Image { Source = picture, Margin = new Thickness(16) };
            }
            else
            {
                var editor = new TextEditor
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = true,
                    SyntaxHighlighting = ext == ".swift" ? Swift : HighlightingManager.Instance.GetDefinitionByExtension(ext),
                };
                editor.Load(path);
                SearchPanel.Install(editor);
                editor.TextChanged += (_, _) => { if (!doc.Dirty) Mark(doc, true); };
                d.Save = () => { editor.Save(path); Mark(doc, false); };
                d.Reload = () => { editor.Load(path); Mark(doc, false); };
                d.Editor = editor;
                body = editor;
            }
            var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            close.Click += (_, _) => Guard(() => Close(doc));
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(d.Title);
            header.Children.Add(close);
            d.Tab = new TabItem { Header = header, Content = body };
            docs.Add(d);
            tabs.Items.Add(d.Tab);
            Mark(d, false);
        }
        tabs.SelectedItem = d.Tab;
        if (line > 0 && d.Editor is { } ed && line <= ed.Document.LineCount)
        {
            ed.ScrollToLine(line);
            ed.CaretOffset = ed.Document.GetLineByNumber(line).Offset;
            ed.Focus();
        }
    }

    void Close(Doc d)
    {
        if (d.Dirty)
        {
            var answer = MessageBox.Show(this, $"Save the changes to {Path.GetFileName(d.Path)}?", "Orchard", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) Save(d);
        }
        docs.Remove(d);
        tabs.Items.Remove(d.Tab);
    }

    void Save(Doc d)
    {
        d.Save();
        // A saved screen rewrites its Swift file: an open copy of that file shows the new code.
        PickUpOutsideChanges();
    }

    void SaveAll()
    {
        foreach (var d in docs.Where(d => d.Dirty).ToList()) Save(d);
        RefreshTree();
    }

    /// <summary>False when the user backs out of closing with unsaved work.</summary>
    bool MayDiscard()
    {
        if (!docs.Any(d => d.Dirty)) return true;
        var answer = MessageBox.Show(this, "Save your changes first?", "Orchard", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Yes) SaveAll();
        return answer != MessageBoxResult.Cancel;
    }

    /// <summary>Files changed outside Orchard (by Claude, by a build, by a saved screen) are shown fresh, unless you have unsaved edits in them.</summary>
    void PickUpOutsideChanges()
    {
        if (app == null) return;
        foreach (var d in docs.Where(d => !d.Dirty && File.Exists(d.Path) && File.GetLastWriteTimeUtc(d.Path) != d.Stamp).ToList()) d.Reload();
        // A version raised in project.yml itself shows up in the Version box.
        if (System.Version.TryParse(Cloud.Version(app), out var saved) && System.Version.TryParse(version.Text, out var shown) && saved > shown)
            version.Text = saved.ToString();
    }

    /// <summary>A one-line question box. Returns "" when cancelled.</summary>
    string Ask(string question, string title, string start = "")
    {
        var box = new TextBox { Text = start, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var dialog = new Window
        {
            Title = title, Content = panel, Owner = this, Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        box.SelectAll();
        box.Focus();
        return dialog.ShowDialog() == true ? box.Text.Trim() : "";
    }
}
