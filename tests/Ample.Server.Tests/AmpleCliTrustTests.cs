using Ample.VisualStudio;

namespace Ample.Server.Tests;

/// <summary>A project may name its own program (AmpleExecutable), but only after the person allowed that program for that project.</summary>
[Collection("AmpleCli statics")]
public sealed class AmpleCliTrustTests
{
    private static (string Folder, string Project, string Program) MakeProject(string content)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        string program = Path.Combine("bin", "tool.exe");
        File.WriteAllText(Path.Combine(folder, program), content);
        string project = Path.Combine(folder, "demo.ampleproj");
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(project + ".user", "<Project><PropertyGroup><AmpleExecutable>" + program + "</AmpleExecutable></PropertyGroup></Project>");
        return (folder, project, program);
    }

    [Fact]
    public void A_program_named_by_a_project_is_not_started_until_the_person_allows_it_and_the_answer_is_remembered()
    {
        (string folder, string project, string program) = MakeProject("v1");
        string trustFile = Path.Combine(folder, "trusted.txt");
        int asked = 0;
        bool answer = false;
        System.Func<string, string, bool> oldAsk = AmpleCli.Ask;
        string oldFile = AmpleCli.TrustFile;
        AmpleCli.TrustFile = trustFile;
        AmpleCli.Ask = (_, _) =>
        {
            asked++;
            return answer;
        };
        try
        {
            // no: the project's program is ignored, and the question comes only once for this session
            Assert.NotEqual(program, AmpleCli.For(project));
            Assert.NotEqual(program, AmpleCli.For(project));
            Assert.Equal(1, asked);
            Assert.False(File.Exists(trustFile));

            // a changed program is a new question; yes: it is used and written to the trust file
            File.WriteAllText(Path.Combine(folder, program), "v2");
            answer = true;
            Assert.Equal(program, AmpleCli.For(project));
            Assert.Equal(program, AmpleCli.For(project));
            Assert.Equal(2, asked);
            Assert.Single(File.ReadAllLines(trustFile));

            // the answer lives in the file: a new session (empty memory is simulated by another project path with the same content) is not the same key,
            // but the same project and program need no question when the file already has the line
            string remembered = File.ReadAllLines(trustFile)[0];
            Assert.Contains("demo.ampleproj", remembered, StringComparison.Ordinal);
            Assert.Contains(program, remembered, StringComparison.Ordinal);

            // changed content after it was allowed: asks again (and says no here)
            File.WriteAllText(Path.Combine(folder, program), "v3 swapped");
            answer = false;
            Assert.NotEqual(program, AmpleCli.For(project));
            Assert.Equal(3, asked);
        }
        finally
        {
            AmpleCli.Ask = oldAsk;
            AmpleCli.TrustFile = oldFile;
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void By_default_nothing_is_allowed_so_a_project_cannot_start_a_program_before_the_package_has_set_the_question()
    {
        (string folder, string project, string program) = MakeProject("v1");
        string oldFile = AmpleCli.TrustFile;
        AmpleCli.TrustFile = Path.Combine(folder, "trusted.txt");
        try
        {
            Assert.NotEqual(program, AmpleCli.For(project));
        }
        finally
        {
            AmpleCli.TrustFile = oldFile;
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }
}
