using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

// the project wizard of the New Project template is loaded by assembly name; without a code base Visual Studio does not find it in the extension folder
[assembly: ProvideCodeBase(CodeBase = @"$PackageFolder$\Ample.ProjectSystem.dll")]

namespace Ample.VisualStudio;

/// <summary>Registers resources for the AMPLE project template.</summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuid)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class AmplePackage : AsyncPackage
{
    internal const string PackageGuid = "EC0B786C-D9A2-47AE-BAC2-ED5C00E09EE1";

    /// <summary>Registers the Build current AMPLET commands.</summary>
    protected override async System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken cancellationToken, System.IProgress<ServiceProgressData> progress)
    {
        AmpleCli.Ask = ProgramTrustPrompt.Ask;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService)) is OleMenuCommandService commands) AmpleCommands.Register(commands);
    }
}
