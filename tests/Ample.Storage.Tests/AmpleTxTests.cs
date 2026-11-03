using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Storage.Package;

namespace Ample.Core.Tests;

public sealed class AmpleTxTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ample-tx-" + Guid.NewGuid().ToString("N"));

    public AmpleTxTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Write(IReadOnlyList<TxSource> sources, string kind = "full")
    {
        string path = Path.Combine(folder, "package.ampletx");
        TxWriter.Write(path, kind, new JsonObject { ["project"] = "test" }, sources);
        return path;
    }

    [Fact]
    public void Text_and_binary_files_come_back_exactly_and_the_head_says_what_it_is()
    {
        byte[] binary = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251))];
        string path = Write([
            new TxSource("song.amplet", AmpleTx.Roles.Song, "text/plain", "song.amplet", "song.amplet", Text: "composition \"x\" { ample 1  tempo 90 }\n# a line\n"),
            new TxSource("notes.txt", AmpleTx.Roles.Source, "text/plain", "notes.txt", "notes.txt", Text: "no final newline"),
            new TxSource("crlf.txt", AmpleTx.Roles.Source, "text/plain", "crlf.txt", "crlf.txt", Text: "one\r\ntwo\r\n"),
            new TxSource("sound.wav", AmpleTx.Roles.Source, "audio/wav", "sound.wav", "samples/sound.wav", Bytes: binary),
        ]);
        string[] head = File.ReadLines(path).Take(3).ToArray();
        Assert.Equal("#AMPLETX 1", head[0]);
        Assert.Equal("#kind full", head[1]);
        Assert.StartsWith("#manifest ", head[2], StringComparison.Ordinal);
        TxPackage package = TxPackage.Open(path);
        Assert.Equal("full", package.Kind);
        Assert.Equal(4, package.Entries.Count);
        Assert.Equal("composition \"x\" { ample 1  tempo 90 }\n# a line\n", package.ReadText(package.Find("song.amplet")!));
        Assert.Equal("no final newline", package.ReadText(package.Find("notes.txt")!));
        Assert.Equal("one\r\ntwo\r\n", package.ReadText(package.Find("crlf.txt")!));   // a text with carriage returns is kept byte for byte (as Base64)
        Assert.Equal("base64", package.Find("crlf.txt")!.Encoding);
        Assert.Equal(binary, package.ReadBytes(package.Find("sound.wav")!));
        Assert.Equal("samples/sound.wav", package.Find("sound.wav")!.OriginalPath);
    }

    [Fact]
    public void A_damaged_file_is_noticed_by_its_hash()
    {
        string path = Write([new TxSource("a.bin", AmpleTx.Roles.Source, "application/octet-stream", "a.bin", "a.bin", Bytes: [.. Enumerable.Range(0, 3000).Select(i => (byte)i)])]);
        string text = File.ReadAllText(path);
        int at = text.IndexOf("\nAAEC", StringComparison.Ordinal) + 5;
        File.WriteAllText(path, text[..at] + (text[at] == 'A' ? 'B' : 'A') + text[(at + 1)..]);
        TxPackage package = TxPackage.Open(path);
        AmpleException ex = Assert.Throws<AmpleException>(() => package.ReadBytes(package.Find("a.bin")!));
        Assert.Equal("AMP5423", ex.DiagnosticCode);
    }

    [Fact]
    public void The_same_content_gives_the_same_package_and_two_files_cannot_share_a_path()
    {
        TxSource[] sources = [new TxSource("b.txt", AmpleTx.Roles.Source, "text/plain", "b", "b", Text: "x\n"), new TxSource("a.txt", AmpleTx.Roles.Source, "text/plain", "a", "a", Text: "y\n")];
        string first = Path.Combine(folder, "1.ampletx"), second = Path.Combine(folder, "2.ampletx");
        string shaA = TxWriter.Write(first, "full", new JsonObject(), sources);
        string shaB = TxWriter.Write(second, "full", new JsonObject(), [.. sources.AsEnumerable().Reverse()]);
        Assert.Equal(shaA, shaB);
        Assert.Throws<AmpleException>(() => TxWriter.Write(Path.Combine(folder, "3.ampletx"), "full", new JsonObject(), [sources[0], sources[0]]));
    }

    [Fact]
    public void A_package_in_a_zip_opens_like_the_plain_one_and_a_newer_format_is_refused()
    {
        string path = Write([new TxSource("song.amplet", AmpleTx.Roles.Song, "text/plain", "song.amplet", "song.amplet", Text: "hello\n")]);
        string zip = TxPackage.Compress(path);
        string plain = TxPackage.Unwrap(zip, Path.Combine(folder, "unzipped"));
        Assert.Equal(TxPackage.Open(path).FileSha256(), TxPackage.Open(plain).FileSha256());
        string newer = Path.Combine(folder, "newer.ampletx");
        File.WriteAllText(newer, File.ReadAllText(path).Replace("#AMPLETX 1", "#AMPLETX 99", StringComparison.Ordinal));
        Assert.Equal("AMP5422", Assert.Throws<AmpleException>(() => TxPackage.Open(newer)).DiagnosticCode);
        File.WriteAllText(Path.Combine(folder, "text.ampletx"), "just text\n");
        Assert.Equal("AMP5421", Assert.Throws<AmpleException>(() => TxPackage.Open(Path.Combine(folder, "text.ampletx"))).DiagnosticCode);
        _ = Encoding.UTF8;
    }
}
