# AMPLE for Visual Studio

This extension adds an AMPLE CPS project type, a project template, AMPLE source
language support, MSBuild targets, and F5 playback to Visual Studio 2022.

## Requirements

- Visual Studio 2022 Community, Professional, or Enterprise.
- The **Visual Studio extension development** workload, including the VSSDK.
- The AMPLE CLI on the Visual Studio process `PATH` as `ample.exe`.
- A CLI build with `open --json`, `open --attach-only`,
  `build --through-instance`, `build --require-instance`,
  `build --instance-project-id`, and `lsp --project` support.

Check CLI compatibility from a terminal with `ample open --help`,
`ample build --help`, and `ample lsp --help`. AMPLE 0.1.28 and newer has all
of these; older versions lack some and cannot run this integration. Without a
CLI the language features stay off and the status bar says so (set `AMPLE_CLI`
to a CLI, or put `ample.exe` on the `PATH`).

## Build and install

From the root of the AMPLE repository, restore the solution's NuGet
dependencies and build:

```powershell
dotnet restore editors/visualstudio/src/Ample.ProjectSystem/Ample.ProjectSystem.csproj
dotnet build editors/visualstudio/src/Ample.ProjectSystem/Ample.ProjectSystem.csproj --no-restore
```

The VSIX is written to
`editors/visualstudio/src/Ample.ProjectSystem/bin/Debug/net472/Ample.ProjectSystem.vsix`.
Install the newer VSIX with the same extension identity to update it; restart
Visual Studio if prompted. To remove it, use **Extensions
> Manage Extensions > Installed** in Visual Studio and uninstall **AMPLE for
Visual Studio**. Restart Visual Studio when prompted.

## Use

Create an **AMPLE Project** from **File > New > Project**. The template
initializes an AMPLE project and includes a demo song. For an existing project,
open its `.ampleproj` file. The extension does not replace or regenerate the
existing `.ample/project.json` identity.

Set the startup song and instance selection in the AMPLE project properties.
Startup song is shared in `.ampleproj`; instance selection is local in
`.ampleproj.user`. **Managed** starts a server if needed. A listed local
instance attaches to that project and is never stopped by VS. Instance values
come from the local AMPLE instance registry when the property list is opened.

Build invokes the AMPLE MSBuild target and writes the rendered WAV below
`obj/Ample`. F5 saves open project documents, builds the startup song through
the active server, downloads its master WAV, and starts playback. Returning to
design mode stops playback. VS requests server shutdown only when this session
started it and its PID, process start time, and project identity still match.

## Language support and diagnostics

The extension starts the AMPLE language server for `.amplet` and `.amplec`
files. It passes the active project root so project modules and components can
be resolved. The language server provides completion, hover, navigation, and
diagnostics. Build errors use MSBuild's file, line, column, severity, and code
format so Visual Studio can display them in Error List and navigate to source.

## Current validation limits

The project-system assembly and VSIX compile, and the generated project has
been built in managed and attached-instance modes. The original development
machine still lacks the VSSDK workload and reports a missing `PkgDefMgmt.dll`;
project loading, template visibility, IntelliSense activation, Error List
navigation, audible F5 playback, and Stop behavior therefore remain unverified
inside the installed IDE. Do not treat a successful VSIX build as IDE
acceptance.

## Commands (Tools menu, and the editor's context menu for a song)

- **AMPLE: Build current AMPLET** and **Build and play current AMPLET**: the song that is open, not the startup song.
- **AMPLE: Open AMPLE folder as project** (`ample vs init`): writes the project file into an existing AMPLE folder and opens it. Nothing else in the folder is touched.
- **AMPLE: Connect this folder to an AMPLE project** (`ample vs connect`): merges a folder that already has files; a file that differs is moved to `_conflicts/<id>/`, never deleted.
- **AMPLE: Toggle F5 between play and debug build**: see "Debugging". The choice lives in `.ampleproj.user`.
- **AMPLE: Toggle colours between AMPLE palette and Visual Studio**: the colours of `.amplet` files follow the palette of the AMPLE editor (a dark and a light set); each kind of token is an item in Tools > Options > Environment > Fonts and Colors ("AMPLE keyword" and so on).

## Debugging the build

Playback runs no code, so debugging is about the **build** of a song: the helper code in a song and in component files (`.amplec`) is evaluated statement by statement.

1. Choose **Toggle F5 between play and debug build** (debug).
2. Set a breakpoint on a line of a helper or a component.
3. F5. Visual Studio stops there (break mode). **Call Stack** lists every caller with its call site, **Locals** shows the variables of the selected frame (notes as `2 notes: 1:0:1:60:100 ...`; secrets are hidden), Step Over and Continue work.

It is the Debug Adapter Protocol (`ample dap`) behind Visual Studio's Debug Adapter Host; `DebugAdapter.pkgdef` registers the engine.

## A project on a server (AMPLE on another machine)

**Tools > AMPLE: Connect to a server** makes a local copy of the project of a server's AMPLE (`ample serve --login`) and opens it:

- **Address and way**: straight to `https://host` (sign-in with the user and password of that AMPLE), or through an SSH tunnel (`ssh -L`, with your own keys, never a password prompt).
- **The password** goes to the Windows credential store; the rest of the settings to `.amplerm/remote.txt` in the folder of the copy.
- **Pull from server** / **Push to server** keep the copy in step. A manifest of SHA-256 per file tells what changed here and what changed there. A file changed on both sides is never overwritten: a pull moves your version to `_conflicts/<id>/`, a push does not send it, and the server itself refuses a write over a changed file (`ifMatch`).
- **F5 and Build current AMPLET** send what changed, build on the server and play the master here. **Debugging** runs on the local copy.
- A lost connection gets retries with messages in the status bar; the copy stays usable offline (plain files).

