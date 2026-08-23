using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

public sealed class TextUtilTests
{
    [Fact]
    public void Html_escapes_the_three_characters_telegram_cares_about()
    {
        Assert.Equal("&lt;b&gt;x&lt;/b&gt; &amp; y", TextUtil.Html("<b>x</b> & y"));
    }

    [Fact]
    public void Html_escapes_the_ampersand_first()
    {
        // Escaping "<" before "&" would turn "<" into "&lt;" and then into "&amp;lt;".
        Assert.Equal("&amp;lt;", TextUtil.Html("&lt;"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    public void HumanBytes_reads_the_way_a_person_would(long bytes, string expected)
    {
        Assert.Equal(expected, TextUtil.HumanBytes(bytes));
    }

    [Fact]
    public void Short_text_is_not_split()
    {
        var pieces = TextUtil.SplitForTelegram("hello", 4000);
        Assert.Single(pieces);
        Assert.Equal("hello", pieces[0]);
    }

    [Fact]
    public void Empty_text_produces_nothing_to_send()
    {
        Assert.Empty(TextUtil.SplitForTelegram(""));
        Assert.Empty(TextUtil.SplitForTelegram(null));
    }

    [Fact]
    public void Splitting_never_loses_or_duplicates_a_character()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"line {i} of the report"));
        var pieces = TextUtil.SplitForTelegram(text, 200);

        Assert.True(pieces.Count > 1);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => Assert.True(p.Length <= 200, $"piece of {p.Length} exceeds the limit"));
    }

    [Fact]
    public void Splitting_prefers_line_boundaries()
    {
        var text = string.Join("\n", Enumerable.Repeat("0123456789", 40));
        var pieces = TextUtil.SplitForTelegram(text, 100);

        // Every piece but the last should end at a newline.
        foreach (var piece in pieces.Take(pieces.Count - 1))
            Assert.EndsWith("\n", piece);
    }

    [Fact]
    public void A_split_never_lands_inside_a_tag()
    {
        // One line, no spaces, longer than the limit: the only way through is the
        // fallback path, which still has to avoid cutting "<b>" in half.
        var text = "<b>" + new string('x', 60) + "</b>" + new string('y', 60);
        var pieces = TextUtil.SplitForTelegram(text, 40);

        Assert.Equal(text, string.Concat(pieces));
        foreach (var piece in pieces)
        {
            Assert.Equal(CountOf(piece, '<'), CountOf(piece, '>'));
            Assert.False(piece.EndsWith("<", StringComparison.Ordinal));
        }

        static int CountOf(string s, char c) => s.Count(ch => ch == c);
    }

    [Fact]
    public void A_split_never_lands_inside_an_entity()
    {
        var text = string.Concat(Enumerable.Repeat("&amp;", 60));
        var pieces = TextUtil.SplitForTelegram(text, 24);

        Assert.Equal(text, string.Concat(pieces));
        foreach (var piece in pieces)
        {
            // A piece that ends mid-entity would have an unterminated '&'.
            var lastAmp = piece.LastIndexOf('&');
            if (lastAmp >= 0)
                Assert.Contains(';', piece[lastAmp..]);
        }
    }

    [Fact]
    public void A_single_unbreakable_run_still_terminates()
    {
        // Pathological input: one "tag" far longer than the limit. The splitter must
        // make progress rather than looping, even though there is no safe boundary.
        var text = "<" + new string('a', 200);
        var pieces = TextUtil.SplitForTelegram(text, 32);

        Assert.Equal(text, string.Concat(pieces));
        Assert.True(pieces.Count >= 6);
    }

    [Fact]
    public void Clip_leaves_short_text_alone_and_marks_what_it_cuts()
    {
        Assert.Equal("hello", TextUtil.Clip("hello", 10));
        Assert.Equal("hel…", TextUtil.Clip("hello world", 4));
        Assert.Equal(string.Empty, TextUtil.Clip(null, 10));
        Assert.Equal(string.Empty, TextUtil.Clip("hello", 0));
    }

    [Fact]
    public void Clip_never_leaves_half_of_an_emoji_behind()
    {
        // "🎛" is a surrogate pair; cutting between its halves produces a lone
        // surrogate, which is not valid UTF-8 and which Telegram renders as a box.
        var text = "ab🎛cd";
        var clipped = TextUtil.Clip(text, 4);

        Assert.DoesNotContain(clipped, c => char.IsHighSurrogate(c) && clipped.IndexOf(c) == clipped.Length - 1);
        foreach (var (ch, i) in clipped.Select((c, i) => (c, i)))
        {
            if (char.IsHighSurrogate(ch))
                Assert.True(i + 1 < clipped.Length && char.IsLowSurrogate(clipped[i + 1]));
        }
    }

    [Theory]
    [InlineData(30, "0m 30s")]
    [InlineData(90 * 60, "1h 30m")]
    [InlineData(50 * 60 * 60, "2d 2h 0m")]
    public void HumanDuration_drops_units_that_do_not_matter(int seconds, string expected)
    {
        Assert.Equal(expected, TextUtil.HumanDuration(TimeSpan.FromSeconds(seconds)));
    }
}
