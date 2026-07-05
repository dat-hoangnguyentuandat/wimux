namespace Wimux.Core.Services;

/// <summary>
/// Score-based fuzzy matching ported from Warp's fuzzy_match crate.
/// Supports smart case, consecutive bonus, and matched-index highlighting.
/// </summary>
public static class FuzzyMatcher
{
    // Scoring constants
    private const int ScoreMatch = 16;
    private const int BonusConsecutive = 8;
    private const int BonusWordBoundary = 12;
    private const int BonusFirstChar = 8;
    private const int PenaltyGap = -1;

    public readonly struct MatchResult
    {
        public int Score { get; init; }
        public IReadOnlyList<int> MatchedIndices { get; init; }

        public MatchResult(int score, IReadOnlyList<int> matchedIndices)
        {
            Score = score;
            MatchedIndices = matchedIndices;
        }
    }

    /// <summary>
    /// Attempts to fuzzy-match <paramref name="query"/> against <paramref name="text"/>.
    /// Smart case: if query contains any uppercase letter, matching is case-sensitive.
    /// Returns null if no match.
    /// </summary>
    public static MatchResult? Match(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return new MatchResult(0, []);
        if (string.IsNullOrEmpty(text)) return null;

        bool caseSensitive = query.Any(char.IsUpper);
        return MatchInternal(text, query, caseSensitive);
    }

    /// <summary>
    /// Case-insensitive variant — ignores uppercase in query.
    /// </summary>
    public static MatchResult? MatchCaseInsensitive(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return new MatchResult(0, []);
        if (string.IsNullOrEmpty(text)) return null;
        return MatchInternal(text, query, caseSensitive: false);
    }

    private static MatchResult? MatchInternal(string text, string query, bool caseSensitive)
    {
        var textSpan = text.AsSpan();
        var querySpan = query.AsSpan();

        // Quick subsequence check first (O(n+m))
        if (!IsSubsequence(textSpan, querySpan, caseSensitive))
            return null;

        // Dynamic programming for optimal match positions
        int tLen = text.Length;
        int qLen = query.Length;

        // dp[i][j] = best score matching query[0..j] against text[0..i]
        // We only need current and previous row
        var scores = new int[tLen + 1, qLen + 1];
        var prevMatch = new bool[tLen + 1, qLen + 1];

        for (int i = 1; i <= tLen; i++)
        {
            for (int j = 1; j <= qLen; j++)
            {
                char tc = caseSensitive ? text[i - 1] : char.ToLowerInvariant(text[i - 1]);
                char qc = caseSensitive ? query[j - 1] : char.ToLowerInvariant(query[j - 1]);

                if (tc == qc)
                {
                    int bonus = 0;

                    // First character bonus
                    if (j == 1 && i == 1) bonus += BonusFirstChar;

                    // Word boundary bonus: text char is start of word
                    if (i > 1 && IsWordBoundary(text[i - 2], text[i - 1]))
                        bonus += BonusWordBoundary;

                    // Consecutive match bonus
                    if (j > 1 && prevMatch[i - 1, j - 1])
                        bonus += BonusConsecutive;

                    int matchScore = scores[i - 1, j - 1] + ScoreMatch + bonus;
                    int skipScore = scores[i - 1, j] + PenaltyGap;

                    if (matchScore >= skipScore)
                    {
                        scores[i, j] = matchScore;
                        prevMatch[i, j] = true;
                    }
                    else
                    {
                        scores[i, j] = skipScore;
                        prevMatch[i, j] = false;
                    }
                }
                else
                {
                    scores[i, j] = scores[i - 1, j] + PenaltyGap;
                    prevMatch[i, j] = false;
                }
            }
        }

        int totalScore = scores[tLen, qLen];
        if (totalScore <= 0) return null;

        // Backtrack to find matched indices
        var indices = new List<int>(qLen);
        int ti = tLen, qi = qLen;
        while (ti > 0 && qi > 0)
        {
            if (prevMatch[ti, qi])
            {
                indices.Add(ti - 1);
                ti--;
                qi--;
            }
            else
            {
                ti--;
            }
        }

        indices.Reverse();
        return new MatchResult(totalScore, indices);
    }

    private static bool IsSubsequence(ReadOnlySpan<char> text, ReadOnlySpan<char> query, bool caseSensitive)
    {
        int qi = 0;
        for (int ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            char tc = caseSensitive ? text[ti] : char.ToLowerInvariant(text[ti]);
            char qc = caseSensitive ? query[qi] : char.ToLowerInvariant(query[qi]);
            if (tc == qc) qi++;
        }
        return qi == query.Length;
    }

    private static bool IsWordBoundary(char prev, char curr)
    {
        // Transition from non-letter to letter, or lowercase to uppercase
        if (!char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(curr)) return true;
        if (char.IsLower(prev) && char.IsUpper(curr)) return true;
        if (prev is '_' or '-' or '.' or '/' or '\\') return true;
        return false;
    }

    /// <summary>
    /// Sorts a list of items by fuzzy match score descending.
    /// Items that don't match are excluded.
    /// </summary>
    public static List<(T Item, MatchResult Match)> RankMatches<T>(
        IEnumerable<T> items,
        string query,
        Func<T, string> getText)
    {
        if (string.IsNullOrWhiteSpace(query))
            return items.Select(i => (i, new MatchResult(0, (IReadOnlyList<int>)Array.Empty<int>()))).ToList();

        var results = new List<(T Item, MatchResult Match)>();
        foreach (var item in items)
        {
            var result = Match(getText(item), query);
            if (result.HasValue)
                results.Add((item, result.Value));
        }

        results.Sort((a, b) => b.Match.Score.CompareTo(a.Match.Score));
        return results;
    }
}
