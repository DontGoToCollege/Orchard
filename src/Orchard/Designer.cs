using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Orchard;

/// <summary>The visual designer: a palette of pieces, a preview you drop them on, and the selected piece's settings.</summary>
public sealed class Designer : UserControl
{
    public event Action? Changed;

    static readonly (string Name, double W, double H)[] Devices = { ("iPhone", 393, 852), ("iPad", 820, 1180), ("Mac", 900, 600) };
    static readonly Brush Blue = Hex("#007AFF"), Gray = Hex("#8A8A8E"), Line = Hex("#D1D1D6"), Faint = Hex("#F2F2F7");
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    readonly string path;
    Node root, selected;
    readonly Dictionary<Node, Border> map = new();
    readonly Stack<string> undo = new();
    string lastEdit = "";
    readonly ContentControl host = new() { FontSize = 17, FontFamily = new FontFamily("Segoe UI") };
    readonly Border device;
    readonly StackPanel props = new() { Margin = new Thickness(10) };
    Node? pressed, dragNode;
    string? dragKind;
    Point pressedAt;

    public Designer(string path)
    {
        this.path = path;
        root = selected = Load();
        Focusable = true;

        var palette = new ListBox { ItemsSource = Catalog.Kinds.Select(k => k.Name).ToList(), BorderThickness = new Thickness(0) };
        Point paletteAt = default;
        palette.PreviewMouseLeftButtonDown += (_, e) => paletteAt = e.GetPosition(this);
        palette.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - paletteAt).Length < 6) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not string kind) return;
            dragKind = kind;
            DragDrop.DoDragDrop(palette, "orchard", DragDropEffects.Copy);
            dragKind = null;
        };
        palette.MouseDoubleClick += (_, _) => { if (palette.SelectedItem is string kind) AddNew(kind); };

        var screen = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(30), Child = host, ClipToBounds = true };
        device = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(40), Padding = new Thickness(10), Child = screen };
        device.MouseMove += (_, e) =>
        {
            if (pressed == null || pressed == root || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - pressedAt).Length < 6) return;
            dragNode = pressed;
            pressed = null;
            DragDrop.DoDragDrop(device, "orchard", DragDropEffects.Move);
            dragNode = null;
        };
        device.PreviewMouseLeftButtonUp += (_, _) => pressed = null;

        var which = new ComboBox { ItemsSource = Devices.Select(d => d.Name).ToList(), Width = 90, Margin = new Thickness(8) };
        which.SelectionChanged += (_, _) =>
        {
            var d = Devices[which.SelectedIndex];
            (device.Width, device.Height) = (d.W + 20, d.H + 20);
            // Room for the iPhone's clock at the top and its home bar at the bottom.
            host.Margin = d.Name == "iPhone" ? new Thickness(0, 54, 0, 30) : new Thickness(0);
        };
        which.SelectedIndex = 0;
        var top = new DockPanel();
        top.Children.Add(which);
        top.Children.Add(new TextBlock
        {
            Text = "A rough picture. Every cloud build takes real screenshots on an iPhone and an iPad.",
            Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });

        var middle = new DockPanel { Background = Faint };
        DockPanel.SetDock(top, Dock.Top);
        middle.Children.Add(top);
        middle.Children.Add(new Viewbox { Child = device, Margin = new Thickness(16) });

        var left = new DockPanel();
        var hint = new TextBlock { Text = "Drag onto the screen, or double-click", Foreground = Gray, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(hint, Dock.Top);
        left.Children.Add(hint);
        left.Children.Add(palette);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        var right = new ScrollViewer { Content = props, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);
        Content = grid;

        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox or ComboBox) return;
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
            if (e.Key == Key.Delete) { Delete(); e.Handled = true; }
        };

        Draw();
        ShowProps();
    }

    Node Load() => JsonSerializer.Deserialize<Node>(File.ReadAllText(path)) ?? new Node();

    string SwiftPath => Path.ChangeExtension(Path.ChangeExtension(path, null), ".swift");

    /// <summary>Saves the design, and writes the Swift file the cloud Mac actually builds.</summary>
    public void Save()
    {
        File.WriteAllText(path, JsonSerializer.Serialize(root, Pretty));
        File.WriteAllText(SwiftPath, SwiftGen.Screen(Path.GetFileNameWithoutExtension(SwiftPath), root));
    }

    public void Reload()
    {
        root = selected = Load();
        undo.Clear();
        Draw();
        ShowProps();
    }

    // ---- changing the design ----

    void Snapshot()
    {
        undo.Push(JsonSerializer.Serialize(root));
        lastEdit = "";
    }

    void Undo()
    {
        if (undo.Count == 0) return;
        root = selected = JsonSerializer.Deserialize<Node>(undo.Pop())!;
        lastEdit = "";
        Touched();
    }

    void Touched()
    {
        Draw();
        ShowProps();
        Changed?.Invoke();
    }

    Node? Holder(Node n, Node? from = null)
    {
        from ??= root;
        if (from.Children.Contains(n)) return from;
        return from.Children.Select(c => Holder(n, c)).FirstOrDefault(p => p != null);
    }

    void AddNew(string kind)
    {
        Snapshot();
        var n = new Node { Kind = kind };
        if (Catalog.Def(selected.Kind).Container) selected.Children.Add(n);
        else
        {
            var parent = Holder(selected)!;
            parent.Children.Insert(parent.Children.IndexOf(selected) + 1, n);
        }
        selected = n;
        Touched();
    }

    /// <summary>Dropped on a stack: goes inside it, at the end. Dropped on anything else: goes just before it.</summary>
    void DropOn(Node target, Node piece)
    {
        if (piece == target || Holder(target, piece) != null) return;
        var into = Catalog.Def(target.Kind).Container ? target : Holder(target);
        if (into == null) return;
        Snapshot();
        Holder(piece)?.Children.Remove(piece);
        if (into == target) into.Children.Add(piece);
        else into.Children.Insert(into.Children.IndexOf(target), piece);
        selected = piece;
        Touched();
    }

    void Move(int by)
    {
        if (Holder(selected) is not { } parent) return;
        var at = parent.Children.IndexOf(selected);
        if (at + by < 0 || at + by >= parent.Children.Count) return;
        Snapshot();
        parent.Children.RemoveAt(at);
        parent.Children.Insert(at + by, selected);
        Touched();
    }

    void Delete()
    {
        if (Holder(selected) is not { } parent) return;
        Snapshot();
        parent.Children.Remove(selected);
        selected = parent;
        Touched();
    }

    void Set(Node n, Prop p, string value)
    {
        // Typing in one box is one undo step, not one per letter.
        var edit = n.GetHashCode() + p.Key;
        if (lastEdit != edit) { Snapshot(); lastEdit = edit; }
        if (value == Catalog.Default(n.Kind, p.Key)) n.Props.Remove(p.Key); else n.Props[p.Key] = value;
        Draw();
        Changed?.Invoke();
    }

    void Select(Node n)
    {
        selected = n;
        foreach (var (node, border) in map) border.BorderBrush = Outline(node);
        ShowProps();
        Focus();
    }

    // ---- the settings panel ----

    void ShowProps()
    {
        props.Children.Clear();
        var n = selected;
        props.Children.Add(new TextBlock { Text = n == root ? n.Kind + " (the whole screen)" : n.Kind, FontSize = 16, FontWeight = FontWeights.Bold });
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void B(string text, Action act, bool on = true)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), IsEnabled = on };
            b.Click += (_, _) => act();
            buttons.Children.Add(b);
        }
        B("Select what holds it", () => Select(Holder(n)!), n != root);
        B("Up", () => Move(-1), n != root);
        B("Down", () => Move(1), n != root);
        B("Delete", Delete, n != root);
        B("Undo", Undo, undo.Count > 0);
        props.Children.Add(buttons);

        IEnumerable<Prop> all = Catalog.Def(n.Kind).Props.Concat(Catalog.Common);
        if (n == root) all = all.Append(Catalog.ScreenCode);
        foreach (var p in all)
        {
            if (p.Type == PropType.Bool)
            {
                var check = new CheckBox { Content = p.Label, IsChecked = n[p.Key] == "true", Margin = new Thickness(0, 10, 0, 0) };
                check.Click += (_, _) => Set(n, p, check.IsChecked == true ? "true" : "false");
                props.Children.Add(check);
                continue;
            }
            props.Children.Add(new TextBlock { Text = p.Label, Foreground = Gray, Margin = new Thickness(0, 10, 0, 2), TextWrapping = TextWrapping.Wrap });
            if (p.Type == PropType.Choice)
            {
                var combo = new ComboBox
                {
                    ItemsSource = p.Choices!.Select(c => c == "" ? "(standard)" : c).ToList(),
                    SelectedIndex = Math.Max(0, Array.IndexOf(p.Choices!, n[p.Key])),
                };
                combo.SelectionChanged += (_, _) => Set(n, p, p.Choices![combo.SelectedIndex]);
                props.Children.Add(combo);
                continue;
            }
            var box = new TextBox { Text = n[p.Key], Padding = new Thickness(3) };
            if (p.Type == PropType.Code)
            {
                box.AcceptsReturn = box.AcceptsTab = true;
                box.FontFamily = new FontFamily("Consolas");
                box.MinHeight = 64;
                box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            box.TextChanged += (_, _) => Set(n, p, box.Text);
            props.Children.Add(box);
        }
    }

    // ---- the preview ----

    void Draw()
    {
        map.Clear();
        host.Content = Render(root);
    }

    Brush Outline(Node n) => n == selected ? Blue : Catalog.Def(n.Kind).Container ? Line : Brushes.Transparent;

    Border Render(Node n)
    {
        FrameworkElement inner = n.Kind switch
        {
            "Text" => Txt(n["text"]),
            "Button" => n["style"] switch
            {
                "borderedProminent" => Pill(Txt(n["title"], Brushes.White), Blue),
                "bordered" => Pill(Txt(n["title"]), Hex("#1F007AFF")),
                _ => Txt(n["title"]),
            },
            // Apple's symbols don't exist on Windows, so the preview shows the symbol's name.
            "Image" => new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "◈", HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = n["symbol"], FontSize = 11, Foreground = Gray, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
            "TextField" or "SecureField" => new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(n["style"] == "plain" ? 0 : 1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6), MinWidth = 220, Child = new TextBlock { Text = n["placeholder"], Foreground = Gray },
            },
            "Toggle" => Row(n["label"], new Border { Width = 51, Height = 31, CornerRadius = new CornerRadius(16), Background = Hex("#34C759") }),
            "Stepper" => Row(n["label"], Pill(new TextBlock { Text = "−    +", Foreground = Brushes.Black }, Faint)),
            "Slider" => new Slider { Width = 220, Maximum = 1, Value = 0.5, IsHitTestVisible = false },
            "Spacer" => new Border { MinWidth = 10, MinHeight = 10 },
            "Divider" => new Border { Height = 1, Background = Line, MinWidth = 40 },
            "NavigationLink" => Row(n["title"], new TextBlock { Text = "›", Foreground = Gray }),
            "Custom" => Pill(new TextBlock { Text = "Swift code", Foreground = Gray }, Faint),
            _ => Stack(n),
        };

        var wrap = new Border
        {
            Child = inner, Tag = n, AllowDrop = true, BorderThickness = new Thickness(1), BorderBrush = Outline(n),
            // Never null: a piece with no background at all can't be clicked or dropped on.
            Background = Paint(n["background"]) ?? Brushes.Transparent,
        };
        if (Catalog.Def(n.Kind).Container && n.Children.Count == 0) wrap.MinWidth = wrap.MinHeight = 44;
        var color = Paint(n["color"]) ?? (n.Kind is "Button" or "NavigationLink" ? Blue : null);
        if (color != null) TextElement.SetForeground(wrap, color);
        var size = Catalog.Num(n["size"]) ?? n["font"] switch
        {
            "largeTitle" => 34, "title" => 28, "title2" => 22, "title3" => 20, "subheadline" => 15,
            "callout" => 16, "footnote" => 13, "caption" => 12, "headline" or "body" => 17, _ => (double?)null,
        };
        if (size > 0) TextElement.SetFontSize(wrap, size.Value);
        if (n["bold"] == "true" || n["font"] == "headline") TextElement.SetFontWeight(wrap, FontWeights.SemiBold);
        if (Catalog.Num(n["padding"]) is double pad && pad >= 0) wrap.Padding = new Thickness(pad);
        if (Catalog.Num(n["width"]) is double w && w > 0) wrap.Width = w;
        if (Catalog.Num(n["height"]) is double h && h > 0) wrap.Height = h;
        if (Catalog.Num(n["corner"]) is double r && r >= 0) wrap.CornerRadius = new CornerRadius(r);
        if (Catalog.Num(n["opacity"]) is double o && o >= 0 && o <= 1) wrap.Opacity = o;

        wrap.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pressed = n;
            pressedAt = e.GetPosition(this);
            Select(n);
        };
        wrap.Drop += (_, e) =>
        {
            e.Handled = true;
            var piece = dragNode ?? (dragKind != null ? new Node { Kind = dragKind } : null);
            if (piece != null) DropOn(n, piece);
        };
        map[n] = wrap;
        return wrap;
    }

    /// <summary>Whether a piece takes all the room it's offered in that direction, as it would in SwiftUI.</summary>
    static bool Greedy(Node c, bool horizontal) =>
        c.Kind is "List" or "Form" or "ScrollView" or "NavigationStack"
        || (horizontal && (c["fill"] == "true" || c.Kind == "Divider"))
        || (c.Kind == (horizontal ? "HStack" : "VStack") && c.Children.Any(x => x.Kind == "Spacer" || Greedy(x, horizontal)));

    FrameworkElement Stack(Node n)
    {
        bool across = n.Kind == "HStack", layered = n.Kind == "ZStack", rows = n.Kind is "List" or "Form" or "Section";
        var gap = Catalog.Num(n["spacing"]) ?? (rows ? 0 : 8);
        var grid = new Grid();
        if (n.Kind is "List" or "Form") grid.Background = Faint;
        var stretch = false;
        var count = 0;

        void Add(FrameworkElement el, bool takesRest)
        {
            var length = takesRest ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            stretch |= takesRest;
            if (layered) { }
            else if (across)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
                Grid.SetColumn(el, count);
                el.Margin = new Thickness(count > 0 ? gap : 0, 0, 0, 0);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = length });
                Grid.SetRow(el, count);
                el.Margin = new Thickness(0, count > 0 ? gap : 0, 0, 0);
            }
            grid.Children.Add(el);
            count++;
        }

        var heading = n.Kind == "NavigationStack" ? n["title"] : n.Kind == "Section" ? n["header"].ToUpperInvariant() : "";
        if (heading != "")
            Add(new TextBlock
            {
                Text = heading, Margin = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = n.Kind == "Section" ? 13 : 34, FontWeight = n.Kind == "Section" ? FontWeights.Normal : FontWeights.Bold,
                Foreground = n.Kind == "Section" ? Gray : Brushes.Black,
            }, false);

        foreach (var c in n.Children)
        {
            FrameworkElement el = Render(c);
            if (across)
            {
                el.VerticalAlignment = Greedy(c, false) ? VerticalAlignment.Stretch
                    : n["align"] switch { "top" => VerticalAlignment.Top, "bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
            }
            else
            {
                el.HorizontalAlignment = rows || Greedy(c, true) ? HorizontalAlignment.Stretch
                    : n["align"] switch { "leading" => HorizontalAlignment.Left, "trailing" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
                if (layered) el.VerticalAlignment = Greedy(c, false) ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            }
            if (rows && c.Kind != "Section")
                el = new Border { Child = el, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 10, 16, 10) };
            Add(el, c.Kind == "Spacer" || Greedy(c, across));
        }

        // A stack hugs its pieces and sits in the middle, unless a Spacer (or a list) pushes it to fill the room.
        var fromTop = n.Kind is "List" or "Form" or "ScrollView" or "NavigationStack" or "Section";
        if (!layered && !across) grid.VerticalAlignment = stretch ? VerticalAlignment.Stretch : fromTop ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (across) grid.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        return grid;
    }

    static TextBlock Txt(string s, Brush? color = null)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        if (color != null) t.Foreground = color;
        return t;
    }

    static Border Pill(UIElement child, Brush background) =>
        new() { Child = child, Background = background, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7) };

    static DockPanel Row(string label, FrameworkElement right)
    {
        var row = new DockPanel { MinWidth = 220 };
        DockPanel.SetDock(right, Dock.Right);
        right.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        return row;
    }

    static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    static Brush? Paint(string color)
    {
        color = color.Trim();
        if (Catalog.Colors.TryGetValue(color, out var named)) return Hex(named);
        return System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? Hex(color) : null;
    }
}
