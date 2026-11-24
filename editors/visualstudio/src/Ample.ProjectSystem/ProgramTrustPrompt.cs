using System.Windows.Forms;

namespace Ample.VisualStudio;

/// <summary>The question "may this project start this program?", asked once per project and program (see <see cref="AmpleCli.Ask"/>).</summary>
internal static class ProgramTrustPrompt
{
    /// <summary>Shows the question; true when the person says yes. The answer is remembered by <see cref="AmpleCli"/>.</summary>
    public static bool Ask(string projectFile, string program)
    {
        string text = "This AMPLE project wants to start a program of its own:\n\n" + program + "\n\nProject: " + projectFile
            + "\n\nOnly allow it when you trust whoever made the project: the program runs with your rights as soon as the project is open."
            + "\n\nAllow it? (No: AMPLE uses the program from AMPLE_CLI or the one on your PATH instead.)";
        return MessageBox.Show(text, "AMPLE: this project starts its own program", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
