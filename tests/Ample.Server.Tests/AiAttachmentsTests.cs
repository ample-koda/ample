using Ample.Core.Abstractions;

namespace Ample.Server.Tests;

/// <summary>The files that a user attaches to a message to Koda or the assistant.</summary>
public sealed class AiAttachmentsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ample-attach-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void A_file_is_kept_found_again_and_a_picture_is_known_as_one()
    {
        AiAttachments store = new(folder);
        AiAttachment picture = store.Save("Score screenshot.PNG", [1, 2, 3]);
        Assert.True(picture.IsImage);
        Assert.Equal("Score screenshot.PNG", picture.Name);
        AiAttachment? found = store.Find(picture.Id);
        Assert.NotNull(found);
        Assert.Equal(3, found!.Size);
        Assert.False(store.Save("notes.txt", [65]).IsImage);
    }

    [Fact]
    public void A_name_cannot_walk_out_of_the_folder_and_an_id_is_never_a_path()
    {
        string bs = ((char)92).ToString();
        Assert.Equal("x.png", AiAttachments.CleanName(".." + bs + ".." + bs + "evil/x.png"));
        Assert.Equal("attachment", AiAttachments.CleanName("..."));
        Assert.Equal("a_b.txt", AiAttachments.CleanName("a<b.txt"));
        AiAttachments store = new(folder);
        Assert.Null(store.Find(".." + bs + ".." + bs + "x"));
        Assert.Null(store.Find("../x"));
        Assert.Null(store.Find("0123456789abcdef0123456789abcdeg"));
        Assert.Null(store.Find(null));
    }

    [Fact]
    public void Empty_big_and_program_files_are_refused()
    {
        AiAttachments store = new(folder);
        Assert.Throws<AmpleException>(() => store.Save("a.txt", []));
        Assert.Throws<AmpleException>(() => store.Save("a.txt", new byte[AiAttachments.MaxBytes + 1]));
        Assert.Throws<AmpleException>(() => store.Save("run.exe", [1]));
        Assert.Throws<AmpleException>(() => store.Save("x.ps1", [1]));
    }

    [Fact]
    public void A_pdf_that_cannot_be_drawn_gives_no_pictures_and_no_error()
    {
        string pdf = Path.Combine(folder, "broken.pdf");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(pdf, [37, 80, 68, 70, 45, 49, 46, 52]);   // "%PDF-1.4" and nothing else
        Assert.Empty(AiAttachments.RenderPdf(pdf, Path.Combine(folder, "pages")));
    }
}
