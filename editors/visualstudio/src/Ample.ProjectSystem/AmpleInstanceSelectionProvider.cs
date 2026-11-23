using System.ComponentModel.Composition;
using System.Runtime.Serialization;
using Microsoft.Build.Framework.XamlTypes;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Ample.VisualStudio;

[ExportDynamicEnumValuesProvider("AmpleInstanceSelectionProvider")]
[AppliesTo(AmpleUnconfiguredProject.UniqueCapability)]
internal sealed class AmpleInstanceSelectionProvider : IDynamicEnumValuesProvider
{
    private readonly string _projectRoot;

    [ImportingConstructor]
    public AmpleInstanceSelectionProvider(ConfiguredProject configuredProject) => _projectRoot = Path.GetDirectoryName(configuredProject.UnconfiguredProject.FullPath)!;

    public Task<IDynamicEnumValuesGenerator> GetProviderAsync(IList<NameValuePair>? options) => Task.FromResult<IDynamicEnumValuesGenerator>(new AmpleInstanceSelectionGenerator(_projectRoot));
}

internal sealed class AmpleInstanceSelectionGenerator(string projectRoot) : IDynamicEnumValuesGenerator
{
    public bool AllowCustomValues => false;

    public async Task<ICollection<IEnumValue>> GetListedValuesAsync()
    {
        List<IEnumValue> values = [Make("managed", "Managed (start if needed)", true)];
        try
        {
            foreach (AmpleLocalInstance instance in await AmpleInstanceDiscovery.ListAsync())
            {
                if (SameFolder(instance.Folder, projectRoot)) values.Add(Make(instance.ProjectId, $"{instance.Name} — {instance.Version} (port {instance.Port})", false));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or SerializationException) { }
        return values;
    }

    public async Task<IEnumValue?> TryCreateEnumValueAsync(string userSuppliedValue)
    {
        if (userSuppliedValue == "managed") return Make("managed", "Managed (start if needed)", true);
        try
        {
            foreach (AmpleLocalInstance instance in await AmpleInstanceDiscovery.ListAsync())
            {
                if (instance.ProjectId == userSuppliedValue && SameFolder(instance.Folder,
                    projectRoot)) return Make(instance.ProjectId, $"{instance.Name} — {instance.Version} (port {instance.Port})", false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or SerializationException) { }
        return Guid.TryParse(userSuppliedValue, out _) ? Make(userSuppliedValue, $"Unavailable ({userSuppliedValue})", false) : null;
    }

    private static bool SameFolder(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase);

    private static PageEnumValue Make(string name, string displayName, bool isDefault) => new(new EnumValue { Name = name, DisplayName = displayName, IsDefault = isDefault });
}
