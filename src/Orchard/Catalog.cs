using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orchard;

/// <summary>One piece of a designed screen (a text, a button, a stack holding other pieces).</summary>
public sealed class Node
{
    public string Kind { get; set; } = "VStack";
    public Dictionary<string, string> Props { get; set; } = new();
    public List<Node> Children { get; set; } = new();

    /// <summary>A setting's value, or its default when the piece doesn't set it.</summary>
    public string this[string key] => Props.TryGetValue(key, out var v) ? v : Catalog.Default(Kind, key);
}

public enum PropType { Text, Code, Number, Bool, Choice }
public record Prop(string Key, string Label, PropType Type = PropType.Text, string Default = "", string[]? Choices = null);
public record KindDef(string Name, bool Container, params Prop[] Props);

/// <summary>Every piece the designer offers and every setting it has. Adding a piece = one line here, one in SwiftGen, one in Designer.Render.</summary>
public static class Catalog
{
    public static readonly KindDef[] Kinds =
    {
        new("Text", false, new Prop("text", "Text", Default: "Text")),
        new("Button", false, new Prop("title", "Title", Default: "Button"),
            new Prop("style", "Look", PropType.Choice, "", new[] { "", "bordered", "borderedProminent", "plain" }),
            new Prop("action", "When tapped (Swift)", PropType.Code)),
        new("Image", false, new Prop("symbol", "Symbol name (from Apple's SF Symbols)", Default: "star.fill")),
        new("TextField", false, new Prop("placeholder", "Hint text", Default: "Type here"), new Prop("variable", "Saves into variable", Default: "text"),
            new Prop("style", "Look", PropType.Choice, "roundedBorder", new[] { "roundedBorder", "plain" })),
        new("SecureField", false, new Prop("placeholder", "Hint text", Default: "Password"), new Prop("variable", "Saves into variable", Default: "password"),
            new Prop("style", "Look", PropType.Choice, "roundedBorder", new[] { "roundedBorder", "plain" })),
        new("Toggle", false, new Prop("label", "Label", Default: "Switch"), new Prop("variable", "Saves into variable", Default: "isOn")),
        new("Slider", false, new Prop("variable", "Saves into variable", Default: "amount"),
            new Prop("min", "Lowest", PropType.Number, "0"), new Prop("max", "Highest", PropType.Number, "1")),
        new("Stepper", false, new Prop("label", "Label", Default: "Count"), new Prop("variable", "Saves into variable", Default: "count")),
        new("Spacer", false),
        new("Divider", false),
        new("VStack", true, new Prop("spacing", "Gap between pieces", PropType.Number),
            new Prop("align", "Line up", PropType.Choice, "", new[] { "", "leading", "trailing" })),
        new("HStack", true, new Prop("spacing", "Gap between pieces", PropType.Number),
            new Prop("align", "Line up", PropType.Choice, "", new[] { "", "top", "bottom" })),
        new("ZStack", true),
        new("ScrollView", true),
        new("List", true),
        new("Form", true),
        new("Section", true, new Prop("header", "Heading")),
        new("NavigationStack", true, new Prop("title", "Title")),
        new("NavigationLink", false, new Prop("title", "Title", Default: "Next"), new Prop("destination", "Screen to open", Default: "ContentView")),
        new("Custom", false, new Prop("code", "Swift view code", PropType.Code, "Rectangle().fill(.blue).frame(height: 80)")),
    };

    /// <summary>Settings every piece has.</summary>
    public static readonly Prop[] Common =
    {
        new("font", "Text style", PropType.Choice, "", new[] { "", "largeTitle", "title", "title2", "title3", "headline", "subheadline", "body", "callout", "footnote", "caption" }),
        new("size", "Text size", PropType.Number),
        new("bold", "Bold", PropType.Bool, "false"),
        new("color", "Color (name or #RRGGBB)"),
        new("padding", "Space around", PropType.Number),
        new("fill", "Fill the width", PropType.Bool, "false"),
        new("width", "Width", PropType.Number),
        new("height", "Height", PropType.Number),
        new("background", "Background color"),
        new("corner", "Rounded corners", PropType.Number),
        new("opacity", "See-through (0 to 1)", PropType.Number),
        new("extra", "Extra Swift modifiers, one per line", PropType.Code),
    };

    /// <summary>Only the screen's outermost piece has this.</summary>
    public static readonly Prop ScreenCode = new("screenCode", "Screen's own variables and functions (Swift)", PropType.Code);

    /// <summary>SwiftUI's colour names, with the Windows preview's stand-in for each.</summary>
    public static readonly Dictionary<string, string> Colors = new()
    {
        ["red"] = "#FF3B30", ["orange"] = "#FF9500", ["yellow"] = "#FFCC00", ["green"] = "#34C759", ["mint"] = "#00C7BE",
        ["teal"] = "#30B0C7", ["cyan"] = "#32ADE6", ["blue"] = "#007AFF", ["indigo"] = "#5856D6", ["purple"] = "#AF52DE",
        ["pink"] = "#FF2D55", ["brown"] = "#A2845E", ["white"] = "#FFFFFF", ["gray"] = "#8E8E93", ["black"] = "#000000",
        ["clear"] = "#00FFFFFF", ["primary"] = "#000000", ["secondary"] = "#8A8A8E", ["accentColor"] = "#007AFF",
    };

    public static KindDef Def(string kind) => Array.Find(Kinds, k => k.Name == kind) ?? Kinds[^1];

    public static string Default(string kind, string key) =>
        (Array.Find(Def(kind).Props, p => p.Key == key) ?? Array.Find(Common, p => p.Key == key))?.Default ?? "";

    /// <summary>A typed-in number, or null when it isn't one (so a typo never reaches the Swift code).</summary>
    public static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>Turns a designed screen into SwiftUI code.</summary>
public static class SwiftGen
{
    public static string Screen(string name, Node root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Made by Orchard's designer from {name}.screen.json.");
        sb.AppendLine("// Changes made here are replaced when the screen is saved. Put your own code in another file.");
        sb.AppendLine("import SwiftUI");
        sb.AppendLine();
        sb.AppendLine($"struct {name}: View {{");
        var own = root["screenCode"];
        var states = new Dictionary<string, string>();
        Collect(root, states);
        foreach (var (variable, rest) in states)
            // A variable the screen's own code already declares (say with @AppStorage) isn't declared twice.
            if (!Regex.IsMatch(own, $@"\bvar\s+{variable}\b"))
                sb.AppendLine($"    @State private var {variable}{rest}");
        foreach (var line in Lines(own)) sb.AppendLine("    " + line);
        if (states.Count > 0 || own.Trim() != "") sb.AppendLine();
        sb.AppendLine("    var body: some View {");
        Emit(root, sb, "        ");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    static void Collect(Node n, Dictionary<string, string> states)
    {
        var rest = n.Kind switch
        {
            "TextField" or "SecureField" => ": String = \"\"",
            "Toggle" => ": Bool = false",
            "Slider" => $": Double = {Number(n, "min")}",
            "Stepper" => ": Int = 0",
            _ => null,
        };
        if (rest != null) states.TryAdd(Variable(n), rest);
        foreach (var c in n.Children) Collect(c, states);
    }

    static void Emit(Node n, StringBuilder sb, string ind)
    {
        var inner = ind + "    ";
        if (Catalog.Def(n.Kind).Container)
        {
            sb.AppendLine(ind + Head(n) + " {");
            var titled = n.Kind == "NavigationStack" && n["title"] != "";
            if (titled) sb.AppendLine(inner + "Group {");
            foreach (var c in n.Children) Emit(c, sb, titled ? inner + "    " : inner);
            if (titled)
            {
                sb.AppendLine(inner + "}");
                sb.AppendLine(inner + $".navigationTitle({Str(n["title"])})");
            }
            sb.AppendLine(ind + "}");
        }
        else if (n.Kind == "Button")
        {
            sb.AppendLine(ind + $"Button({Str(n["title"])}) {{");
            foreach (var line in Lines(n["action"])) sb.AppendLine(inner + line);
            sb.AppendLine(ind + "}");
        }
        else if (n.Kind == "Custom")
            foreach (var line in Lines(n["code"])) sb.AppendLine(ind + line);
        else
            sb.AppendLine(ind + Head(n));
        // After a closing brace, modifiers line up with the brace, the way Swift is usually written.
        var afterBrace = Catalog.Def(n.Kind).Container || n.Kind == "Button";
        foreach (var m in Mods(n)) sb.AppendLine((afterBrace ? ind : inner) + m);
    }

    static string Head(Node n) => n.Kind switch
    {
        "Text" => $"Text({Str(n["text"])})",
        "Image" => $"Image(systemName: {Str(n["symbol"])})",
        "TextField" => $"TextField({Str(n["placeholder"])}, text: ${Variable(n)})",
        "SecureField" => $"SecureField({Str(n["placeholder"])}, text: ${Variable(n)})",
        "Toggle" => $"Toggle({Str(n["label"])}, isOn: ${Variable(n)})",
        "Slider" => $"Slider(value: ${Variable(n)}, in: {Number(n, "min")}...{Number(n, "max")})",
        "Stepper" => $"Stepper({Str(n["label"])}, value: ${Variable(n)})",
        "Spacer" => "Spacer()",
        "Divider" => "Divider()",
        "NavigationLink" => $"NavigationLink({Str(n["title"])}) {{ {Identifier(n["destination"], "ContentView")}() }}",
        "Section" => n["header"] == "" ? "Section" : $"Section({Str(n["header"])})",
        "VStack" or "HStack" => n.Kind + StackArgs(n),
        _ => n.Kind,
    };

    static string StackArgs(Node n)
    {
        var args = new List<string>();
        if (n["align"] != "") args.Add("alignment: ." + n["align"]);
        if (Catalog.Num(n["spacing"]) != null) args.Add("spacing: " + n["spacing"].Trim());
        return args.Count == 0 ? "" : "(" + string.Join(", ", args) + ")";
    }

    static IEnumerable<string> Mods(Node n)
    {
        if (Catalog.Num(n["size"]) != null) yield return $".font(.system(size: {n["size"].Trim()}))";
        else if (n["font"] != "") yield return $".font(.{n["font"]})";
        if (n["bold"] == "true") yield return ".bold()";
        if (n["color"].Trim() != "") yield return $".foregroundStyle({Color(n["color"])})";
        if (n.Kind == "Button" && n["style"] != "") yield return $".buttonStyle(.{n["style"]})";
        if (n.Kind is "TextField" or "SecureField") yield return $".textFieldStyle(.{n["style"]})";
        if (Catalog.Num(n["padding"]) != null) yield return $".padding({n["padding"].Trim()})";
        if (n["fill"] == "true") yield return ".frame(maxWidth: .infinity)";
        var size = new List<string>();
        if (Catalog.Num(n["width"]) != null) size.Add("width: " + n["width"].Trim());
        if (Catalog.Num(n["height"]) != null) size.Add("height: " + n["height"].Trim());
        if (size.Count > 0) yield return $".frame({string.Join(", ", size)})";
        if (n["background"].Trim() != "") yield return $".background({Color(n["background"])})";
        if (Catalog.Num(n["corner"]) != null) yield return $".clipShape(RoundedRectangle(cornerRadius: {n["corner"].Trim()}))";
        if (Catalog.Num(n["opacity"]) != null) yield return $".opacity({n["opacity"].Trim()})";
        foreach (var line in Lines(n["extra"])) yield return line;
    }

    /// <summary>A colour name or #RRGGBB as Swift. Anything else is taken to be Swift already, like Color("Brand").</summary>
    public static string Color(string c)
    {
        c = c.Trim();
        if (Catalog.Colors.ContainsKey(c)) return "Color." + c;
        if (!Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) return c;
        string Part(int at) => (Convert.ToInt32(c.Substring(at, 2), 16) / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
        return $"Color(red: {Part(1)}, green: {Part(3)}, blue: {Part(5)})";
    }

    // shortcut: backslashes are left alone so \(name) works inside text; a quote inside \( ) breaks the line, escape properly if that comes up.
    static string Str(string s) => "\"" + s.Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    static string Variable(Node n) => Identifier(n["variable"], Catalog.Default(n.Kind, "variable"));
    static string Identifier(string s, string fallback) => Regex.IsMatch(s.Trim(), @"^[A-Za-z_]\w*$") ? s.Trim() : fallback;
    static string Number(Node n, string key) => Catalog.Num(n[key]) != null ? n[key].Trim() : Catalog.Default(n.Kind, key);

    static IEnumerable<string> Lines(string s) =>
        s.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim() != "");
}
