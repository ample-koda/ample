namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (default_utt_break) and src/synth/flite.c (flite_ts_to_speech, the loop that cuts a token stream into utterances).
// Translated to C# and modified: the loop returns the tokens of every utterance and leaves the synthesis to the caller. See FLITE-NOTICE.md in the data folder.

/// <summary>
/// Cuts a text into utterances the way <c>flite -f</c> does (<c>flite_file_to_speech</c>): the tokens are read one by one and a new utterance starts at a token
/// where <c>default_utt_break</c> says so, at the end of the text and after 500 tokens. The text of <c>flite -t</c> is one utterance and does not pass here.
/// </summary>
internal static class UtteranceBreaks
{
    /// <summary>The number of tokens after which an utterance is cut whatever the text says (<c>num_tokens > 500</c> in <c>flite_ts_to_speech</c>).</summary>
    public const int MaxTokens = 500;

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";

    /// <summary><c>flite_ts_to_speech</c> without the audio: the tokens of every utterance, in order.</summary>
    /// <param name="byteText">The text as a byte string (one character per byte).</param>
    /// <param name="cancellation">Stops the reading between tokens.</param>
    /// <returns>The utterances; a text without tokens gives one utterance without tokens (C synthesises that one too, and it makes no sound).</returns>
    public static List<List<TokenRead>> Split(string byteText, CancellationToken cancellation = default)
    {
        List<List<TokenRead>> utterances = [];
        TokenStream ts = new(byteText);
        List<TokenRead> current = [];
        int numTokens = 0;
        while (!ts.Eof || numTokens > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            TokenRead token = ts.Get();
            if (token.Name.Length == 0 || numTokens > MaxTokens || (current.Count > 0 && Break(token, current[^1])))
            {
                // an end of utterance: the one that is read so far is finished
                utterances.Add(current);
                if (ts.Eof)
                {
                    return utterances;
                }

                current = [];
                numTokens = 0;
            }

            numTokens++;
            current.Add(token);
        }

        // the loop was not entered (an empty text) or ended without a break: C frees the utterance that it has without making sound for it
        if (current.Count > 0 || utterances.Count == 0)
        {
            utterances.Add(current);
        }

        return utterances;
    }

    /// <summary><c>default_utt_break</c>: whether the token that was just read starts a new utterance.</summary>
    /// <param name="token">The token that was just read (its <see cref="TokenRead.Whitespace"/> is the white space in front of it).</param>
    /// <param name="last">The last token of the utterance so far.</param>
    /// <returns>True when the utterance ends in front of <paramref name="token"/>.</returns>
    public static bool Break(TokenRead token, TokenRead last)
    {
        string postpunct = last.Postpunctuation;
        string ltoken = last.Name;
        string whitespace = token.Whitespace;
        char first = token.Name[0];

        // two new lines in the white space
        if (whitespace.IndexOf('\n') != whitespace.LastIndexOf('\n'))
        {
            return true;
        }

        // "Well, this is a little specific isn't it." in the C source
        if ((ltoken is "Yahoo" or "YAHOO" or "yahoo") && postpunct.Contains('!') && Lower.Contains(first))
        {
            return false;
        }

        if (postpunct.Contains(':') || postpunct.Contains('?') || postpunct.Contains('!'))
        {
            return true;
        }

        if (postpunct.Contains('.') && whitespace.Length > 1 && Upper.Contains(first))
        {
            return true;
        }

        // the next word starts with a capital and the last word is not an abbreviation
        if (postpunct.Contains('.') && Upper.Contains(first) && !IsAbbreviation(ltoken))
        {
            return true;
        }

        return false;
    }

    /// <summary>The test of <c>default_utt_break</c> for a last word that is an abbreviation: it ends in a capital, or it has fewer than four characters and starts with one.</summary>
    /// <remarks>An empty token cannot be in the text; in C its first character is the terminating zero, which <c>strchr</c> finds in the capitals, so it counts as an abbreviation here.</remarks>
    private static bool IsAbbreviation(string token) =>
        token.Length == 0 || Upper.Contains(token[^1]) || (token.Length < 4 && Upper.Contains(token[0]));
}
