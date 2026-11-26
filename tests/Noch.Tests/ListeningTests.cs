using Noch.Core;
using Xunit;

namespace Noch.Tests;

public class ListeningTests
{
    static float[] Tone(double seconds, double loud) =>
        [.. Enumerable.Range(0, (int)(seconds * Chunker.Rate)).Select(i => (float)(loud * Math.Sin(i * 2 * Math.PI * 220 / Chunker.Rate)))];

    [Fact]
    public void Speech_between_pauses_comes_out_as_one_piece_each()
    {
        var c = new Chunker();
        var out1 = c.Feed(Tone(1, .001));       // the room
        var out2 = c.Feed(Tone(2, .2));         // someone talks
        var out3 = c.Feed(Tone(1, .001));       // a pause
        var out4 = c.Feed(Tone(1.5, .2));       // more talk
        var out5 = c.Feed(Tone(1, .001));
        Assert.Empty(out1); Assert.Empty(out2); Assert.Empty(out4);
        var first = Assert.Single(out3); var second = Assert.Single(out5);
        Assert.InRange(first.Length / (double)Chunker.Rate, 2, 3.2);
        Assert.InRange(second.Length / (double)Chunker.Rate, 1.5, 2.7);
    }

    [Fact]
    public void Someone_who_never_stops_is_cut_every_fifteen_seconds()
    {
        var c = new Chunker();
        var pieces = c.Feed(Tone(40, .2));
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.InRange(p.Length / (double)Chunker.Rate, 14.9, 15.1));
        Assert.NotNull(c.Flush());
    }

    [Fact]
    public void A_cough_is_not_a_sentence()
    {
        var c = new Chunker();
        c.Feed(Tone(1, .001)); c.Feed(Tone(.1, .3));
        Assert.Empty(c.Feed(Tone(1, .001)));
    }

    [Fact]
    public void Real_questions_are_found_and_small_talk_is_not()
    {
        Assert.Equal(["What was our revenue growth last quarter?"],
            Questions.In("Thanks everyone. Can you hear me? What was our revenue growth last quarter? Right? I think it was good."));
        Assert.Empty(Questions.In("Does that make sense? Okay? We ship on Friday."));
        Assert.Empty(Questions.In("Can you hear me okay? Can everyone see my screen now? Is my audio working?"));
        Assert.Equal(["How does the new caching layer handle invalidation?", "Is it ready?"],
            Questions.In("How does the new caching layer handle invalidation? Is it ready?"));
    }
}
