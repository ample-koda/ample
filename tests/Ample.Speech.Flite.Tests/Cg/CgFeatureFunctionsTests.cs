using Ample.Speech.Flite.Cg;
using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Tests.Cg;

public sealed class CgFeatureFunctionsTests
{
    [Fact]
    public void Frame_planning_is_bounded_and_attaches_frames_to_states()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cg", "flite-cg-slt.bin");
        FliteCgVoiceData voice = FliteCgVoiceData.Load(path);
        Utterance u = FliteProsodyVoice.Create(FliteEnglishData.Instance).NewUtterance();
        CgFeatureFunctions.Register(u.FFunctions);
        Item segment = u.RelationCreate("Segment").Append(null);
        Hrg.SetString(segment, "name", "pau");
        CgStates.Make(u, voice, CancellationToken.None);
        int count = CgFrames.Make(u, voice, 1, 10000, CancellationToken.None);
        Assert.True(count > 0);
        Assert.Equal(count, FeatureSet.Int(u.Features, "param_track_num_frames"));
        Assert.True(Hrg.FeatFloat(segment, "end") > 0);
        Assert.NotNull(Hrg.Parent(Hrg.ItemAs(u.GetRelation("mcep").Head, "mcep_link")));
        Assert.Throws<FliteCgBudgetException>(() => CgFrames.Make(u, voice, 1, 1, CancellationToken.None));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CgFrames.Make(u, voice, 1, 10000, cancelled.Token));
    }

    [Fact]
    public void States_follow_the_voice_table_and_keep_the_segment_link()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cg", "flite-cg-slt.bin");
        FliteCgVoiceData voice = FliteCgVoiceData.Load(path);
        IReadOnlyList<string> row = voice.PhoneStates.First(r => r[0] == "aa");
        Utterance u = new();
        Item segment = u.RelationCreate("Segment").Append(null);
        Hrg.SetString(segment, "name", "aa");
        CgStates.Make(u, voice, CancellationToken.None);
        Item? state = u.GetRelation("HMMstate").Head;
        for (int i = 1; i < row.Count; i++)
        {
            Assert.NotNull(state);
            Assert.Equal(row[i], Hrg.Name(state));
            Assert.Equal(i, Hrg.FeatInt(state, "statepos"));
            Assert.Same(segment.Contents, Hrg.Parent(Hrg.ItemAs(state, "segstate"))?.Contents);
            state = Hrg.Next(state);
        }

        Assert.Null(state);
    }

    [Fact]
    public void State_positions_and_pause_match_the_pinned_flite_types()
    {
        Utterance u = new();
        CgFeatureFunctions.Register(u.FFunctions);
        Relation frames = u.RelationCreate("mcep");
        Item first = frames.Append(null);
        Item middle = frames.Append(null);
        Item last = frames.Append(null);
        Item pause = frames.Append(null);
        foreach (Item item in new[] { first, middle, last }) Hrg.SetString(item, "name", "aa_1");
        Hrg.SetString(pause, "name", "pau");

        Assert.Equal("b", FeaturePath.FfeatureString(first, "lisp_cg_state_pos"));
        Assert.Equal("m", FeaturePath.FfeatureString(middle, "lisp_cg_state_pos"));
        Assert.Equal("e", FeaturePath.FfeatureString(last, "lisp_cg_state_pos"));
        Assert.IsType<StringValue>(FeaturePath.Ffeature(pause, "lisp_is_pau"));
        Assert.Equal("1", FeaturePath.FfeatureString(pause, "lisp_is_pau"));
        Assert.Equal("0", FeaturePath.FfeatureString(first, "lisp_is_pau"));
    }

    [Fact]
    public void State_frame_indices_use_the_shared_relation_and_handle_one_frame()
    {
        Utterance u = new();
        CgFeatureFunctions.Register(u.FFunctions);
        Relation frames = u.RelationCreate("mcep");
        Relation links = u.RelationCreate("mcep_link");
        Item state = links.Append(null);
        Item a = frames.Append(null);
        Item b = frames.Append(null);
        Item c = frames.Append(null);
        Hrg.SetInt(a, "frame_number", 10);
        Hrg.SetInt(b, "frame_number", 12);
        Hrg.SetInt(c, "frame_number", 14);
        Hrg.AddDaughter(state, a);
        Hrg.AddDaughter(state, b);
        Hrg.AddDaughter(state, c);

        Assert.Equal(0.5f, FeaturePath.FfeatureFloat(b, "lisp_cg_state_place"));
        Assert.Equal(2f, FeaturePath.FfeatureFloat(b, "lisp_cg_state_index"));
        Assert.Equal(2f, FeaturePath.FfeatureFloat(b, "lisp_cg_state_rindex"));
        Item solo = frames.Append(null);
        Hrg.SetInt(solo, "frame_number", 15);
        Hrg.AddDaughter(links.Append(null), solo);
        Assert.Equal(0f, FeaturePath.FfeatureFloat(solo, "lisp_cg_state_place"));
    }

    [Fact]
    public void Duration_and_phrase_number_follow_previous_items()
    {
        Utterance u = new();
        CgFeatureFunctions.Register(u.FFunctions);
        Relation relation = u.RelationCreate("Segment");
        Item a = relation.Append(null);
        Item b = relation.Append(null);
        Hrg.SetFloat(a, "end", 0.25f);
        Hrg.SetFloat(b, "end", 0.75f);
        Assert.Equal(0.25f, FeaturePath.FfeatureFloat(a, "lisp_cg_duration"));
        Assert.Equal(0.5f, FeaturePath.FfeatureFloat(b, "lisp_cg_duration"));
        Assert.Equal(1, FeaturePath.FfeatureInt(b, "lisp_cg_find_phrase_number"));
        Assert.Equal(-1f, FeaturePath.FfeatureFloat(b, "lisp_cg_position_in_phrasep"));
    }
}
