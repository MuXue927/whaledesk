using System.Globalization;

namespace WhaleDesk.Interaction;

/// <summary>One thing a provider can offer the search palette.</summary>
/// <param name="Id">Stable identifier, also the key for the usage history.</param>
/// <param name="Title">Main line.</param>
/// <param name="Subtitle">Secondary line (type / preview).</param>
/// <param name="SearchText">
/// Everything that may be matched: title, subtitle, aliases, pinyin initials. Kept separate from
/// what is displayed so "yj" can match 剪贴板 without showing the initials.
/// </param>
/// <param name="ProviderWeight">Tie-breaker between providers (higher wins).</param>
internal sealed record SearchCandidate(string Id, string Title, string Subtitle, string SearchText, int ProviderWeight = 100);

/// <summary>A ranked result.</summary>
internal sealed record SearchHit(SearchCandidate Candidate, int MatchScore, double FinalScore);

/// <summary>
/// The matching/ranking core of the pet's search palette - the part worth copying from tools like
/// PowerToys Run: several providers feed candidates in, one scorer ranks them, and the user's own
/// history nudges the order.
///
/// Pure functions only (no UI, no IO) so <c>--selftest</c> pins the behaviour:
///  * exact / prefix / substring / acronym / subsequence matches, in that order of confidence;
///  * word-boundary and consecutive-run bonuses, gap penalties for scattered matches;
///  * a usage boost so a frequently used item rises without ever beating a clearly better match;
///  * an empty query returns the most-used items instead of nothing.
/// </summary>
internal static class SearchEngine
{
    /// <summary>
    /// Match <paramref name="query"/> against <paramref name="text"/>.
    /// Returns 0 when it does not match at all, otherwise a positive confidence score.
    /// </summary>
    public static int Match(string? query, string? text)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var t = (text ?? "").ToLowerInvariant();
        if (q.Length == 0) return 1;              // empty query matches everything weakly
        if (t.Length == 0) return 0;

        // 1. Whole-string equality is the strongest signal.
        if (t == q) return 1000;

        // 2. Prefix: what people usually type.
        if (t.StartsWith(q, StringComparison.Ordinal)) return 900 + Math.Min(90, q.Length * 10);

        // 3. Substring anywhere.
        var index = t.IndexOf(q, StringComparison.Ordinal);
        if (index >= 0) return 700 - Math.Min(200, index * 5) + Math.Min(60, q.Length * 10);

        // 4. Acronym: "vsc" -> "visual studio code". Only for 2+ characters.
        var acronym = Acronym(t);
        if (q.Length >= 2 && acronym.Contains(q, StringComparison.Ordinal)) return 600 + Math.Min(50, q.Length * 10);

        // 5. Subsequence with bonuses/penalties; requires every character in order.
        return Subsequence(q, t);
    }

    private static int Subsequence(string query, string text)
    {
        var score = 300;
        var textIndex = 0;
        var previousMatch = -2;
        foreach (var ch in query)
        {
            var found = text.IndexOf(ch, textIndex);
            if (found < 0) return 0;
            if (found == previousMatch + 1) score += 25;                     // consecutive run
            if (found == 0 || IsBoundary(text, found)) score += 35;          // word start
            score -= Math.Min(20, found - textIndex);                        // gap penalty
            previousMatch = found;
            textIndex = found + 1;
        }
        // Longer queries matching exactly the same text are more convincing.
        return Math.Max(1, score);
    }

    private static bool IsBoundary(string text, int index) =>
        index > 0 && (text[index - 1] is ' ' or '-' or '_' or '/' or '\\' or '.' or '(' or '[');

    /// <summary>First letter of every word: "visual studio code" -> "vsc".</summary>
    public static string Acronym(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new System.Text.StringBuilder();
        var atWordStart = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '\\' or '.')
            {
                atWordStart = true;
                continue;
            }
            if (atWordStart && char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            atWordStart = false;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Score one candidate: match confidence, then provider weight, then the usage boost.
    /// The usage boost is multiplicative but bounded, so it can reorder similar results without
    /// ever lifting a poor match over an exact one.
    /// </summary>
    public static double Score(SearchCandidate candidate, string? query, double usageBoost)
    {
        var match = Match(query, candidate.SearchText);
        if (match <= 0) return 0;
        var providerFactor = 1.0 + Math.Clamp(candidate.ProviderWeight, 0, 200) / 1000.0;
        var usageFactor = 1.0 + Math.Clamp(usageBoost, 0, 1.5);
        return match * providerFactor * usageFactor;
    }

    /// <summary>
    /// Rank candidates for a query. <paramref name="usageBoost"/> maps an id to 0..1.5.
    /// An empty query returns the highest-boosted items (recent/frequent), which is what the
    /// palette shows before the user types anything.
    /// </summary>
    public static IReadOnlyList<SearchHit> Rank(
        string? query,
        IEnumerable<SearchCandidate> candidates,
        Func<string, double>? usageBoost = null,
        int limit = 8)
    {
        var boost = usageBoost ?? (_ => 0);
        var hits = new List<SearchHit>();
        foreach (var candidate in candidates)
        {
            var match = Match(query, candidate.SearchText);
            if (match <= 0) continue;
            var score = Score(candidate, query, boost(candidate.Id));
            if (score <= 0) continue;
            hits.Add(new SearchHit(candidate, match, score));
        }
        return hits
            .OrderByDescending(h => h.FinalScore)
            .ThenByDescending(h => h.MatchScore)
            .ThenBy(h => h.Candidate.Title, StringComparer.CurrentCulture)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>
    /// Small arithmetic evaluator for the "calculator" results. Supports + - * / % ( ) and
    /// decimals - deliberately no variables, no functions, no code execution.
    /// </summary>
    public static bool TryEvaluate(string? expression, out double value)
    {
        value = 0;
        var text = (expression ?? "").Trim();
        if (text.Length == 0) return false;
        // Only digits, operators, spaces, dots and parentheses reach the parser.
        foreach (var ch in text)
        {
            if (!(char.IsDigit(ch) || ch is '+' or '-' or '*' or '/' or '%' or '(' or ')' or '.' or ' ' or ',')) return false;
        }
        var parser = new Parser(text.Replace(",", ""));
        try
        {
            var result = parser.ParseExpression();
            parser.SkipSpaces();
            if (!parser.AtEnd) return false;
            if (double.IsNaN(result) || double.IsInfinity(result)) return false;
            value = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text) => _text = text;

        public bool AtEnd => _pos >= _text.Length;

        public void SkipSpaces()
        {
            while (_pos < _text.Length && _text[_pos] == ' ') _pos++;
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (_pos >= _text.Length) return value;
                var op = _text[_pos];
                if (op != '+' && op != '-') return value;
                _pos++;
                var rhs = ParseTerm();
                value = op == '+' ? value + rhs : value - rhs;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipSpaces();
                if (_pos >= _text.Length) return value;
                var op = _text[_pos];
                if (op != '*' && op != '/' && op != '%') return value;
                _pos++;
                var rhs = ParseFactor();
                value = op switch
                {
                    '*' => value * rhs,
                    '/' => rhs == 0 ? throw new DivideByZeroException() : value / rhs,
                    _ => rhs == 0 ? throw new DivideByZeroException() : value % rhs,
                };
            }
        }

        private double ParseFactor()
        {
            SkipSpaces();
            if (_pos >= _text.Length) throw new FormatException("unexpected end");
            if (_text[_pos] == '(')
            {
                _pos++;
                var inner = ParseExpression();
                SkipSpaces();
                if (_pos >= _text.Length || _text[_pos] != ')') throw new FormatException("missing )");
                _pos++;
                return inner;
            }
            if (_text[_pos] == '-')
            {
                _pos++;
                return -ParseFactor();
            }
            if (_text[_pos] == '+')
            {
                _pos++;
                return ParseFactor();
            }
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
            if (_pos == start) throw new FormatException("expected a number");
            var number = _text[start.._pos];
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                throw new FormatException("bad number");
            return parsed;
        }
    }
}
