using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), lang/usenglish/us_aswd.c (the functions; the machines are data). Translated to C# and modified: see UsText.cs.

/// <summary>"Is this word pronounceable": two finite state machines over the shape of the word (<c>us_aswd</c>).</summary>
internal static class UsAswd
{
    /// <summary><c>fsm_transition</c>: the state after a symbol, or -1 when the state has no transition for it.</summary>
    /// <param name="fsm">The machine.</param>
    /// <param name="state">The state (the index of its first transition).</param>
    /// <param name="symbol">The symbol (a C <c>char</c>: bytes above 127 are negative).</param>
    /// <returns>The next state or -1.</returns>
    public static int Transition(FliteFsm fsm, int state, int symbol)
    {
        IReadOnlyList<ushort> t = fsm.Transitions;
        for (int i = state; t[i] != 0; i++)
        {
            if (t[i] % fsm.VocabularySize == symbol)
            {
                return t[i] / fsm.VocabularySize;
            }
        }

        return -1;
    }

    private static int Symbol(char c) => c is 'n' or 'm' ? 'N' : "aeiouy".Contains(c, StringComparison.Ordinal) ? 'V' : (sbyte)(byte)c;

    private static bool IsWordPre(FliteFsm start, string word)
    {
        int state = Transition(start, 0, '#');
        foreach (char c in word)
        {
            int symbol = Symbol(c);
            state = Transition(start, state, symbol);
            if (state == -1)
            {
                return false;
            }

            if (symbol == 'V')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordSuf(FliteFsm start, FliteFsm end, string word)
    {
        // as in Flite, the first transition (the word boundary) is taken in the start machine
        int state = Transition(start, 0, '#');
        for (int i = word.Length - 1; i >= 0; i--)
        {
            int symbol = Symbol(word[i]);
            state = Transition(end, state, symbol);
            if (state == -1)
            {
                return false;
            }

            if (symbol == 'V')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>us_aswd</c>: whether the word looks pronounceable.</summary>
    /// <param name="word">The word (a byte string).</param>
    /// <param name="data">The front-end data with the two machines.</param>
    /// <returns>True when it looks pronounceable.</returns>
    public static bool IsWord(string word, FliteEnglishData data)
    {
        string dc = ByteText.Downcase(word);
        return IsWordPre(data.WordStartMachine, dc) && IsWordSuf(data.WordStartMachine, data.WordEndMachine, dc);
    }
}
