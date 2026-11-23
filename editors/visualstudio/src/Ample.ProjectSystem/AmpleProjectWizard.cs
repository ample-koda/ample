using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TemplateWizard;

namespace Ample.VisualStudio;

/// <summary>Initializes the AMPLE project created from the Visual Studio template.</summary>
public sealed class AmpleProjectWizard : IWizard
{
    /// <inheritdoc />
    public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
    {
        string folder = replacementsDictionary["$destinationdirectory$"];
        string name = replacementsDictionary["$projectname$"];
        Directory.CreateDirectory(folder);
        ProcessStartInfo startInfo = new(AmpleCli.For(null), $"new {Quote(folder)} --name {Quote(name)}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder
        };

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The AMPLE CLI did not start.");
            StringBuilder output = new();
            StringBuilder error = new();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string outputText = output.ToString().Trim();
                string errorText = error.ToString().Trim();
                string details = string.IsNullOrWhiteSpace(errorText) ? outputText : string.IsNullOrWhiteSpace(outputText) ? errorText : errorText + Environment.NewLine + outputText;
                if (details.Length > 2000) details = details.Substring(0, 2000);
                throw new WizardBackoutException($"AMPLE project creation failed (exit code {process.ExitCode}).{(details.Length == 0 ? string.Empty : Environment.NewLine + details)}");
            }
        }
        catch (Win32Exception)
        {
            throw new WizardBackoutException("The AMPLE CLI was not found. Install AMPLE or add ample.exe to PATH, then create the project again.");
        }
    }

    /// <inheritdoc />
    public void ProjectFinishedGenerating(EnvDTE.Project project) { }
    /// <inheritdoc />
    public void ProjectItemFinishedGenerating(EnvDTE.ProjectItem projectItem) { }
    /// <inheritdoc />
    public void BeforeOpeningFile(EnvDTE.ProjectItem projectItem) { }
    /// <inheritdoc />
    public void RunFinished() { }
    /// <inheritdoc />
    public bool ShouldAddProjectItem(string filePath) => true;

    internal static string Quote(string value)
    {
        StringBuilder result = new("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else
            {
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    result.Append('\\', backslashes).Append(character);
                }

                backslashes = 0;
            }
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}
