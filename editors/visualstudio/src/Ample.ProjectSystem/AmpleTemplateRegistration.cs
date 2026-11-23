using Microsoft.VisualStudio.Shell;

namespace Ample.VisualStudio;

internal sealed class AmpleTemplateRegistrationAttribute : RegistrationAttribute
{
    public override void Register(RegistrationContext context)
    {
        using Key key = context.CreateKey($@"NewProjectTemplates\TemplateDirs\{{{AmplePackage.PackageGuid}}}\/1");
        key.SetValue(string.Empty, "AMPLE");
        key.SetValue("TemplatesDir", @"$PackageFolder$\ProjectTemplates\Ample");
        key.SetValue("SortPriority", 100);
    }

    public override void Unregister(RegistrationContext context)
    {
        context.RemoveKey($@"NewProjectTemplates\TemplateDirs\{{{AmplePackage.PackageGuid}}}");
    }
}
