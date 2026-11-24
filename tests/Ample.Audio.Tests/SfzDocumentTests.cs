using Ample.Audio.Engine.Sfz;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

[Trait("Category", "Sampler")]
public sealed class SfzDocumentTests
{
    private static IReadOnlyList<SfzDocument.SfzRegion> Parse(string text, Dictionary<string, string>? includes = null) =>
        SfzDocument.Parse(text, path => includes is not null && includes.TryGetValue(path, out string? body) ? body : throw new AmpleException("AMP5395", $"missing include {path}"));

    [Fact]
    public void Empty_or_comment_only_file_has_no_regions()
    {
        Assert.Empty(Parse("// just a comment\n   \n"));
    }

    [Fact]
    public void A_region_inherits_group_opcodes_it_does_not_override()
    {
        var regions = Parse("<group> ampeg_release=1 lovel=0 <region> sample=a.wav lovel=5");
        Assert.Single(regions);
        Assert.Equal("1", regions[0].Opcodes["ampeg_release"]);
        Assert.Equal("5", regions[0].Opcodes["lovel"]); // region overrides the group
    }

    [Fact]
    public void Global_master_and_group_all_contribute_and_region_wins_ties()
    {
        var regions = Parse("<global> a=global <master> a=master b=master <group> a=group c=group <region> sample=x.wav a=region");
        Assert.Equal("region", regions[0].Opcodes["a"]);
        Assert.Equal("master", regions[0].Opcodes["b"]);
        Assert.Equal("group", regions[0].Opcodes["c"]);
    }

    [Fact]
    public void A_region_with_no_group_or_master_still_sees_global()
    {
        var regions = Parse("<global> tune=3 <region> sample=a.wav");
        Assert.Equal("3", regions[0].Opcodes["tune"]);
    }

    [Fact]
    public void Value_containing_a_space_is_kept_whole()
    {
        var regions = Parse("<region> sample=Kick In.wav lovel=0");
        Assert.Equal("Kick In.wav", regions[0].Opcodes["sample"]);
        Assert.Equal("0", regions[0].Opcodes["lovel"]);
    }

    [Fact]
    public void One_level_of_include_is_expanded()
    {
        var regions = Parse("<group> ampeg_release=2 #include \"regions.txt\"", new() { ["regions.txt"] = "<region> sample=a.wav" });
        Assert.Single(regions);
        Assert.Equal("2", regions[0].Opcodes["ampeg_release"]);
    }

    [Fact]
    public void Nested_includes_resolve_depth_first()
    {
        var includes = new Dictionary<string, string>
        {
            ["a.txt"] = "<group> g=1 #include \"b.txt\"",
            ["b.txt"] = "<region> sample=a.wav #include \"c.txt\"",
            ["c.txt"] = "n=9",
        };
        var regions = Parse("#include \"a.txt\"", includes);
        Assert.Single(regions);
        Assert.Equal("1", regions[0].Opcodes["g"]);
        Assert.Equal("9", regions[0].Opcodes["n"]);
    }

    [Fact]
    public void Define_substitutes_before_and_inside_an_include()
    {
        var regions = Parse(
            "#define $ROOT 60\n<region> sample=a.wav pitch_keycenter=$ROOT #include \"more.txt\"",
            new() { ["more.txt"] = "<region> sample=b.wav pitch_keycenter=$ROOT" });
        Assert.Equal(2, regions.Count);
        Assert.Equal("60", regions[0].Opcodes["pitch_keycenter"]);
        Assert.Equal("60", regions[1].Opcodes["pitch_keycenter"]);
    }

    [Fact]
    public void Include_relative_to_the_including_files_own_folder()
    {
        var includes = new Dictionary<string, string>
        {
            ["dir/a.sfz"] = "#include \"b.sfz\"",
            ["dir/b.sfz"] = "<region> sample=x.wav",
        };
        var regions = Parse("#include \"dir/a.sfz\"", includes);
        Assert.Single(regions);
    }

    // security finding 46: a small file made the expansion double with every #define (29 defines ran out of memory) or with every #include level (17 levels took 22 s)

    [Fact]
    public void Defines_that_double_the_text_are_refused_quickly_and_not_expanded_until_memory_runs_out()
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < 29; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"#define $a{i:00} $a{i + 1:00}$a{i + 1:00}\n");
        }

        text.Append("#define $a29 xxxxxxxx\n<region> sample=$a00\n");
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        AmpleException refused = Assert.Throws<AmpleException>(() => Parse(text.ToString()));
        Assert.Equal("AMP5395", refused.DiagnosticCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void An_include_that_includes_the_next_file_twice_is_refused_by_the_total_and_not_by_the_depth()
    {
        Dictionary<string, string> includes = [];
        for (int i = 0; i < 30; i++)
        {
            includes[$"i{i}.sfz"] = $"#include \"i{i + 1}.sfz\"\n#include \"i{i + 1}.sfz\"\n";
        }

        includes["i30.sfz"] = "<region> sample=a.wav\n";
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        AmpleException refused = Assert.Throws<AmpleException>(() => Parse("#include \"i0.sfz\"", includes));
        Assert.Equal("AMP5395", refused.DiagnosticCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void More_defines_than_the_limit_are_refused()
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i <= SfzDocument.MaxDefines; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"#define $d{i} {i}\n");
        }

        Assert.Equal("AMP5395", Assert.Throws<AmpleException>(() => Parse(text.ToString())).DiagnosticCode);
    }

    [Fact]
    public void An_include_chain_and_a_few_defines_below_the_limits_still_work()
    {
        Dictionary<string, string> includes = [];
        for (int i = 0; i < 20; i++)
        {
            includes[$"c{i}.sfz"] = $"#include \"c{i + 1}.sfz\"\n";
        }

        includes["c20.sfz"] = "<region> sample=$name.wav\n";
        var regions = Parse("#define $name kick\n#include \"c0.sfz\"", includes);
        Assert.Equal("kick.wav", Assert.Single(regions).Opcodes["sample"]);
    }
}
