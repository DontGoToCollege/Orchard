using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Orchard;

/// <summary>Run by publish.ps1 on the built exe (Orchard.exe --selftest) before the build is kept.</summary>
public static class SelfTest
{
    public static int Run()
    {
        var log = Path.Combine(Path.GetTempPath(), "orchard-selftest.log");
        try
        {
            Check();
            File.WriteAllText(log, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, ex.ToString());
            return 1;
        }
    }

    static void Same(string expected, string actual)
    {
        expected = expected.Replace("\r", "").Trim();
        actual = actual.Replace("\r", "").Trim();
        if (expected != actual) throw new Exception($"Expected:\n{expected}\n\nGot:\n{actual}");
    }

    static void Check()
    {
        // A designed screen becomes exactly this Swift.
        var screen = new Node
        {
            Props = { ["spacing"] = "12", ["align"] = "leading", ["screenCode"] = "func hello() { }" },
            Children =
            {
                new Node { Kind = "Text", Props = { ["text"] = "Say \"hi\" to \\(name)", ["font"] = "title", ["bold"] = "true", ["color"] = "#FF0000" } },
                new Node { Kind = "TextField", Props = { ["variable"] = "name", ["padding"] = "oops" } },
                new Node { Kind = "Button", Props = { ["title"] = "Go", ["action"] = "hello()", ["style"] = "borderedProminent", ["fill"] = "true" } },
                new Node { Kind = "NavigationStack", Props = { ["title"] = "Home" }, Children = { new Node { Kind = "Toggle" }, new Node { Kind = "Spacer" } } },
                new Node { Kind = "Slider", Props = { ["background"] = "blue", ["corner"] = "8", ["extra"] = ".shadow(radius: 2)" } },
            },
        };
        Same("""
            // Made by Orchard's designer from Demo.screen.json.
            // Changes made here are replaced when the screen is saved. Put your own code in another file.
            import SwiftUI

            struct Demo: View {
                @State private var name: String = ""
                @State private var isOn: Bool = false
                @State private var amount: Double = 0
                func hello() { }

                var body: some View {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Say \"hi\" to \(name)")
                            .font(.title)
                            .bold()
                            .foregroundStyle(Color(red: 1, green: 0, blue: 0))
                        TextField("Type here", text: $name)
                            .textFieldStyle(.roundedBorder)
                        Button("Go") {
                            hello()
                        }
                        .buttonStyle(.borderedProminent)
                        .frame(maxWidth: .infinity)
                        NavigationStack {
                            Group {
                                Toggle("Switch", isOn: $isOn)
                                Spacer()
                            }
                            .navigationTitle("Home")
                        }
                        Slider(value: $amount, in: 0...1)
                            .background(Color.blue)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                            .shadow(radius: 2)
                    }
                }
            }
            """, SwiftGen.Screen("Demo", screen));

        // A design survives being saved and opened again.
        var again = JsonSerializer.Deserialize<Node>(JsonSerializer.Serialize(screen))!;
        Same(SwiftGen.Screen("Demo", screen), SwiftGen.Screen("Demo", again));

        // Every starter file is inside the exe, and the starter screen is a valid design.
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
        foreach (var needed in new[] { "project.yml", ".github/workflows/build.yml", ".gitignore", ".gitattributes", "README.md", "LICENSE", "Sources/App.swift", "Sources/ContentView.screen.json" })
            if (!names.Contains("Template/" + needed)) throw new Exception("Missing starter file: " + needed);
        if (!names.Contains("Swift.xshd")) throw new Exception("Missing Swift.xshd");

        // The version line in project.yml can be read and changed.
        var dir = Directory.CreateTempSubdirectory("orchard-test").FullName;
        File.WriteAllText(Path.Combine(dir, "project.yml"), "name: Demo\nsettings:\n  base:\n    MARKETING_VERSION: 1.0.0\n    OTHER: 1\n");
        Cloud.SetVersion(dir, "1.2.3");
        Same("1.2.3", Cloud.Version(dir));
        Same("name: Demo\nsettings:\n  base:\n    MARKETING_VERSION: 1.2.3\n    OTHER: 1", File.ReadAllText(Path.Combine(dir, "project.yml")));

        // The designer can draw every kind of piece, alone and inside every kind of stack, and the window can be made.
        var every = new Node { Children = Catalog.Kinds.Select(k => new Node { Kind = k.Name }).ToList() };
        foreach (var holder in every.Children.Where(c => Catalog.Def(c.Kind).Container))
            holder.Children = Catalog.Kinds.Select(k => new Node { Kind = k.Name, Props = { ["padding"] = "4", ["background"] = "mint", ["size"] = "20" } }).ToList();
        var file = Path.Combine(dir, "Every.screen.json");
        File.WriteAllText(file, JsonSerializer.Serialize(every));
        var designer = new Designer(file);
        designer.Measure(new System.Windows.Size(1200, 800));
        designer.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
        designer.Save();
        if (!File.ReadAllText(Path.Combine(dir, "Every.swift")).Contains("struct Every: View")) throw new Exception("The designer didn't write the Swift file.");
        _ = new MainWindow();
        Directory.Delete(dir, true);
    }
}
