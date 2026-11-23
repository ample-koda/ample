using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace Ample.VisualStudio;

[Export]
[AppliesTo(UniqueCapability)]
[AmpleTemplateRegistration]
[ProjectTypeRegistration(ProjectTypeGuid, "AMPLE", "AMPLE Project Files (*.ampleproj);*.ampleproj", ProjectExtension, Language, resourcePackageGuid: AmplePackage.PackageGuid,
    PossibleProjectExtensions = ProjectExtension,
    Capabilities = UniqueCapability,
    ProjectTemplatesDir = @"ProjectTemplates\Ample")]
internal sealed class AmpleUnconfiguredProject
{
    internal const string ProjectTypeGuid = "{2B33138A-7D9A-4AAC-9636-0C5798A1E7C2}";
    internal const string ProjectExtension = "ampleproj";
    internal const string UniqueCapability = "AmpleProject";
    internal const string Language = "AMPLE";
}
