using System.Text.RegularExpressions;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Tests.Text;

/// <summary>
/// Milestone M3 (plan/speech/flite/PLAN.md): the steps <c>tokens</c> and <c>words</c> of the front end equal the C program (the pinned commit, built with the dumper of
/// <c>plan/speech/flite/reference/m3</c>): the Token and Word relations, the questions to the lexicon, the regular expressions and the expanders.
/// </summary>
public sealed class FliteTextParityTests
{
    // the texts for which the C program itself crashes (it indexes outside a table, see UsExpand.Pick); the port stops with an error there
    private static readonly string[] CTextsThatCrash = ["1,,000kg"];

    private static readonly Lazy<(int Texts, int Lines, List<string> Differences)> Corpus = new(CompareCorpus);

    private static (int Texts, int Lines, List<string> Differences) CompareCorpus()
    {
        List<TextFixtures.TextRecord> records = TextFixtures.ParseText(TextFixtures.ReadGz("flite-m3-text.golden.gz"));
        List<string> differences = [];
        int compared = 0;
        foreach (TextFixtures.TextRecord record in records)
        {
            if (record.Crashed)
            {
                if (!CTextsThatCrash.Contains(record.Escaped, StringComparer.Ordinal))
                {
                    differences.Add($"unexpected crash of the C program on '{record.Escaped}'");
                }

                continue;
            }

            string text = TextFixtures.Unescape(record.Escaped);
            RecordedLexicon lexicon = new(record.Lex);
            List<string> actual;
            try
            {
                actual = TextFixtures.DumpTokensAndWords(new UsEnglishFrontEnd(lexicon).Analyse(text));
            }
            catch (Exception e)
            {
                differences.Add($"'{record.Escaped}': {e.GetType().Name}: {e.Message}");
                continue;
            }

            compared += record.Lines.Count;
            if (!actual.SequenceEqual(record.Lines, StringComparer.Ordinal))
            {
                int i = 0;
                while (i < actual.Count && i < record.Lines.Count && actual[i] == record.Lines[i])
                {
                    i++;
                }

                differences.Add($"'{record.Escaped}': first difference at line {i}: C '{(i < record.Lines.Count ? record.Lines[i] : "(none)")}' port '{(i < actual.Count ? actual[i] : "(none)")}'");
            }
            else if (!lexicon.Asked.SequenceEqual(record.Lex, StringComparer.Ordinal))
            {
                differences.Add($"'{record.Escaped}': questions to the lexicon differ: C [{string.Join(" ", record.Lex)}] port [{string.Join(" ", lexicon.Asked)}]");
            }
        }

        return (records.Count, compared, differences);
    }

    private static string Report(List<string> differences) => $"{differences.Count} differences, the first ones:\n" + string.Join('\n', differences.Take(25));

    [Fact]
    public void Tokens_words_and_the_questions_to_the_lexicon_equal_the_C_program_on_the_whole_text_corpus()
    {
        (int texts, int lines, List<string> differences) = Corpus.Value;
        Assert.True(texts > 5000 && lines > 50000, $"the corpus has {texts} texts and {lines} lines of tokens and words");
        Assert.True(differences.Count == 0, Report(differences));
    }

    private static readonly (string Name, Regex Regex)[] RegexTable =
    [
        ("digits", UsRegexes.Digits), ("alpha", UsRegexes.Alpha), ("double", UsRegexes.Double), ("commaint", UsRegexes.CommaInt),
        ("ordinal_number", UsRegexes.OrdinalNumber), ("hasvowel", UsRegexes.HasVowel), ("usmoney", UsRegexes.UsMoney), ("illion", UsRegexes.Illion),
        ("romannums", UsRegexes.RomanNums), ("drst", UsRegexes.DrSt), ("numess", UsRegexes.NumEss), ("leadingzerodigits", UsRegexes.LeadingZeroDigits),
        ("sevenphonenumber", UsRegexes.SevenPhoneNumber), ("fourdigits", UsRegexes.FourDigits), ("threedigits", UsRegexes.ThreeDigits),
        ("numbertime", UsRegexes.NumberTime), ("numbertimexm", UsRegexes.NumberTimeXm), ("dottedabbrevs", UsRegexes.DottedAbbrevs),
        ("digitsslashdigits", UsRegexes.DigitsSlashDigits), ("digits2dash", UsRegexes.Digits2Dash), ("wandm", UsRegexes.WeightsAndMeasures),
    ];

    [Fact]
    public void The_regular_expressions_equal_the_compiled_programs_on_every_probe_string()
    {
        string[] golden = TextFixtures.ReadGz("flite-m3-rx.golden.gz");
        Assert.True(golden.Length > 5000);
        List<string> differences = [];
        int matches = 0;
        foreach (string line in golden)
        {
            string[] parts = line.Split('\t');
            string probe = TextFixtures.Unescape(parts[1]);
            for (int k = 0; k < RegexTable.Length; k++)
            {
                bool expected = parts[2][k] == '1';
                matches += expected ? 1 : 0;
                if (UsRegexes.Match(RegexTable[k].Regex, probe) != expected)
                {
                    differences.Add($"{RegexTable[k].Name} on '{parts[1]}': the C program says {expected}");
                }
            }
        }

        Assert.True(matches > 500, $"only {matches} matches in the probes: they do not test the expressions");
        Assert.True(differences.Count == 0, Report(differences));
    }

    private static string Words(List<ExpWord> words) => string.Join(' ', words.Select(w => TextFixtures.Escape(w.Name)));

    [Fact]
    public void The_expanders_equal_the_C_program_on_every_digit_string_and_probe()
    {
        string[] golden = TextFixtures.ReadGz("flite-m3-exp.golden.gz");
        Assert.True(golden.Length > 5000);
        List<string> differences = [];
        int numbers = 0;
        foreach (string line in golden)
        {
            string[] parts = line.Split('\t');
            string input = TextFixtures.Unescape(parts[1]);
            List<string> expected = [];
            if (input.All(c => c is >= '0' and <= '9'))
            {
                numbers++;
                expected.Add("number:" + Words(UsExpand.Number(input)));
                expected.Add("ordinal:" + Words(UsExpand.Ordinal(input)));
                expected.Add("id:" + Words(UsExpand.Id(input)));
                expected.Add("real:" + Words(UsExpand.Real(input)));
                expected.Add("digits:" + Words(UsExpand.Digits(input)));
            }

            expected.Add("letters:" + Words(UsExpand.Letters(input)));
            expected.Add("roman:" + UsExpand.Roman(input).ToString(System.Globalization.CultureInfo.InvariantCulture));
            string[] actual = parts[2..];
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                differences.Add($"'{parts[1]}': C [{string.Join(" | ", actual)}] port [{string.Join(" | ", expected)}]");
            }
        }

        Assert.True(numbers > 500, $"only {numbers} digit strings");
        Assert.True(differences.Count == 0, Report(differences));
    }
}
