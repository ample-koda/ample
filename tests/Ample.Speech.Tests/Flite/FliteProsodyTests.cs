using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Tests.Flite;

/// <summary>Flite M5: the edges of the prosody modules that the corpus does not reach, from hand-built utterances.</summary>
public sealed class FliteProsodyTests
{
    [Fact]
    public void Without_a_phrasing_tree_every_word_is_in_one_phrase_called_B()
    {
        Utterance u = new();
        Relation words = u.RelationCreate("Word");
        for (int i = 0; i < 3; i++)
        {
            Hrg.SetString(words.Append(null), "name", "w" + i);
        }

        FliteProsody.Phrasing(u);
        Item? phrase = u.GetRelation("Phrase").Head;
        Assert.NotNull(phrase);
        Assert.Null(Hrg.Next(phrase));
        Assert.Equal("B", Hrg.Name(phrase));
        Assert.Equal(3, Daughters(phrase).Count);
    }

    [Fact]
    public void Pause_insertion_in_an_empty_utterance_makes_one_silence()
    {
        FliteProsodyVoice voice = FliteProsodyVoice.Create(FliteEnglishData.Instance);
        Utterance u = voice.NewUtterance();
        u.RelationCreate("Word");
        u.RelationCreate("Segment");
        FliteProsody.Phrasing(u);
        FliteProsody.PauseInsertion(u);
        Item? head = u.GetRelation("Segment").Head;
        Assert.NotNull(head);
        Assert.Equal("pau", Hrg.Name(head));
        Assert.Null(Hrg.Next(head));
    }

    [Fact]
    public void An_utterance_without_syllables_gets_a_flat_target_from_the_start_to_the_end()
    {
        FliteProsodyVoice voice = FliteProsodyVoice.Create(FliteEnglishData.Instance);
        Utterance u = voice.NewUtterance();
        u.RelationCreate("Syllable");
        Item pause = u.RelationCreate("Segment").Append(null);
        Hrg.SetString(pause, "name", "pau");
        Hrg.SetFloat(pause, "end", 0.25f);
        FliteProsody.F0Model(u, FliteEnglishData.Instance.F0Terms);
        List<(float Pos, float F0)> targets = [];
        for (Item? t = u.GetRelation("Target").Head; t is not null; t = Hrg.Next(t))
        {
            targets.Add((Hrg.FeatFloat(t, "pos"), Hrg.FeatFloat(t, "f0")));
        }

        Assert.Equal([(0f, 95f), (0.25f, 95f)], targets);
    }

    [Fact]
    public void The_duration_table_has_41_phones_and_starts_with_uh_the_fallback()
    {
        IReadOnlyList<FliteDurationStat> stats = FliteEnglishData.Instance.DurationStats;
        Assert.Equal(41, stats.Count);
        Assert.Equal("uh", stats[0].Phone);
        Assert.Contains(stats, s => s.Phone == "pau");
    }

    [Fact]
    public void The_voice_carries_the_kal16_prosody_parameters()
    {
        FliteProsodyVoice voice = FliteProsodyVoice.Create(FliteEnglishData.Instance);
        Assert.Equal(95.0f, FeatureSet.Float(voice.Features, "int_f0_target_mean"));
        Assert.Equal(11.0f, FeatureSet.Float(voice.Features, "int_f0_target_stddev"));
        Assert.Equal(1.1f, FeatureSet.Float(voice.Features, "duration_stretch"));
        Assert.Equal("pau", FeatureSet.String(voice.Features, "silence"));
    }

    private static List<Item> Daughters(Item parent)
    {
        List<Item> items = [];
        for (Item? d = Hrg.Daughter(parent); d is not null; d = Hrg.Next(d))
        {
            items.Add(d);
        }

        return items;
    }
}
