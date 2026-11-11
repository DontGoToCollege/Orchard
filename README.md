# Orchard

Make iPhone, iPad and Mac apps on a Windows PC, without owning a Mac.

Apple only lets its apps be built on a Mac. Orchard gets around that by borrowing one: it sends your app to one of GitHub's cloud Macs, which builds it and sends the finished app back. That's free for apps whose code is public on GitHub.

Orchard is three tools in one window:

- **A visual designer.** Drag texts, buttons, pictures, switches, lists and stacks onto a phone-shaped preview and set their colors, sizes and spacing. Every piece also has a box for your own Swift, so the designer never limits what the app can do.
- **A code editor.** Every file of the app opens in a tab, with Swift coloring, line numbers and find (Ctrl+F). The designer writes ordinary SwiftUI code, and you can add files of your own beside it.
- **Build and deliver.** One button saves everything, builds the app on a cloud Mac, and brings back a Mac app, an iPhone and iPad app, and real screenshots of it running on an iPhone and an iPad. If the code has a mistake, the error shows up in plain text and a double-click takes you to the line.

## What you need

- Windows 10 or 11
- A free [GitHub](https://github.com) account
- [Git](https://git-scm.com) and the [GitHub tool](https://cli.github.com) (`gh`), signed in with `gh auth login`

Orchard tells you in one sentence if any of these is missing.

## Download

Get `Orchard.exe` from the [Releases page](../../releases/latest) and run it. There's nothing to install.

## Using it

1. Press **New app** and give it a name. Orchard makes a folder for it with a first screen.
2. Design the screen: drag pieces from the left onto the preview, click a piece to change its settings on the right.
3. Press **Build on a cloud Mac**. The first build asks before putting the app's code on GitHub as a public page. The apps are ready in about two minutes; the messages at the bottom show what the Mac is doing.
4. The finished files land in the app's `Versions\Archive` folder and on its GitHub Releases page. The screenshots take longer (Apple's simulator is slow to start), so they arrive about eight minutes later and the iPhone one opens by itself. Keep Orchard open until then, and keep working meanwhile.

Each successful build needs a new version number (the **Version** box moves on by itself), so every build you've ever made is kept exactly as it was.

## Getting the app onto a device

- **Mac:** unzip the `macOS.zip` file. The first time, macOS blocks the app because it has no Apple signature: open System Settings, go to Privacy & Security and click **Open Anyway**.
- **iPhone and iPad:** the `iOS.ipa` file has no Apple signature. A sideloading tool such as Sideloadly or AltStore installs it with your own free Apple ID; apps installed that way stop working after 7 days and need reinstalling. Putting an app on the App Store needs Apple's paid developer program ($99 a year), which Orchard doesn't handle yet.

## Good to know

- The designer's preview is a rough picture drawn by Windows. The screenshots from each cloud build show the real thing.
- A screen made in the designer is saved as two files: `Name.screen.json` (the design) and `Name.swift` (the code made from it). Change the design, not that Swift file, because saving the design writes the Swift file again.
- Everything about the app (name, version, which devices, minimum iOS version) is in its `project.yml` file, which you can edit in Orchard.
- Orchard also works without its window, for scripts: `Orchard.exe new "My App"` and `Orchard.exe build "folder" --publish`.

## Building Orchard itself

You need the .NET 8 SDK. Run `publish.ps1`; it makes `Orchard.exe` and keeps a copy of every version in `Versions\Archive`.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
