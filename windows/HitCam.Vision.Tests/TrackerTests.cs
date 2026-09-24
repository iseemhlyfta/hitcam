using System.Drawing;

namespace HitCam.Vision.Tests;

public sealed class TrackerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(100);

    private static Detection Person(float left, float top = 0.2f, float size = 0.2f, float score = 0.9f) =>
        new(1, "person", score, new RectangleF(left, top, size, size));

    private static Detection Cat(float left, float top = 0.2f, float size = 0.2f) =>
        new(17, "cat", 0.8f, new RectangleF(left, top, size, size));

    [Fact]
    public void A_track_is_shown_from_its_second_hit()
    {
        var tracker = new Tracker();

        Assert.Empty(tracker.Update([Person(0.1f)], Frame * 1));
        var track = Assert.Single(tracker.Update([Person(0.1f)], Frame * 2));

        Assert.Equal("person", track.Name);
        Assert.Equal("person 90%", track.Label);
    }

    [Fact]
    public void A_single_false_positive_never_appears()
    {
        var tracker = new Tracker();

        Assert.Empty(tracker.Update([Person(0.1f)], Frame * 1));
        Assert.Empty(tracker.Update([], Frame * 2));
        Assert.Empty(tracker.Update([Cat(0.6f)], Frame * 3));
    }

    [Fact]
    public void A_moving_object_keeps_its_id_and_colour()
    {
        var tracker = new Tracker();
        tracker.Update([Person(0.10f)], Frame * 1);
        var first = Assert.Single(tracker.Update([Person(0.12f)], Frame * 2));

        for (var i = 3; i < 20; i++)
        {
            var track = Assert.Single(tracker.Update([Person(0.1f + i * 0.02f)], Frame * i));
            Assert.Equal(first.Id, track.Id);
            Assert.Equal(first.Rgb, track.Rgb);
        }
    }

    [Fact]
    public void Boxes_are_smoothed()
    {
        var tracker = new Tracker(new TrackerOptions { Smoothing = 0.5f });
        tracker.Update([Person(0.1f)], Frame * 1);
        tracker.Update([Person(0.1f)], Frame * 2);

        var track = Assert.Single(tracker.Update([Person(0.2f)], Frame * 3));

        // Half way between the old box and the new detection.
        Assert.Equal(0.15f, track.Box.Left, 4);
        Assert.Equal(0.2f, track.Box.Width, 4);
    }

    [Fact]
    public void A_lost_track_stays_for_the_timeout_then_disappears()
    {
        var tracker = new Tracker(new TrackerOptions { Timeout = TimeSpan.FromSeconds(0.5) });
        tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(1.0));
        var seen = Assert.Single(tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(1.1)));

        // Not detected for 0.4 s: still shown where it was last seen.
        Assert.Equal(seen, Assert.Single(tracker.Update([], TimeSpan.FromSeconds(1.5))));
        // 0.6 s: gone.
        Assert.Empty(tracker.Update([], TimeSpan.FromSeconds(1.7)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Coming_back_after_the_timeout_is_a_new_track()
    {
        var tracker = new Tracker();
        tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(1.0));
        var before = Assert.Single(tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(1.1)));

        tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(3.0));
        var after = Assert.Single(tracker.Update([Person(0.1f)], TimeSpan.FromSeconds(3.1)));

        Assert.NotEqual(before.Id, after.Id);
    }

    [Fact]
    public void Two_separated_objects_never_swap_ids()
    {
        var tracker = new Tracker();
        // Two people, reported in alternating order, walking towards each other but never overlapping.
        tracker.Update([Person(0.05f), Person(0.70f)], Frame * 1);
        var start = tracker.Update([Person(0.70f), Person(0.05f)], Frame * 2);
        var leftId = start.Single(t => t.Box.Left < 0.5f).Id;
        var rightId = start.Single(t => t.Box.Left > 0.5f).Id;
        Assert.NotEqual(leftId, rightId);
        Assert.NotEqual(start[0].Rgb, start[1].Rgb);

        for (var i = 3; i < 12; i++)
        {
            var left = Person(0.05f + i * 0.02f);
            var right = Person(0.70f - i * 0.02f);
            var tracks = tracker.Update(i % 2 == 0 ? [left, right] : [right, left], Frame * i);

            Assert.Equal(2, tracks.Count);
            Assert.Equal(leftId, tracks.MinBy(t => t.Box.Left).Id);
            Assert.Equal(rightId, tracks.MaxBy(t => t.Box.Left).Id);
        }
    }

    [Fact]
    public void Tracks_only_continue_within_their_class()
    {
        var tracker = new Tracker();
        tracker.Update([Person(0.1f)], Frame * 1);
        var person = Assert.Single(tracker.Update([Person(0.1f)], Frame * 2));

        // A cat in the same place is another object; the person is still kept until the timeout.
        var tracks = tracker.Update([Cat(0.1f)], Frame * 3);

        Assert.Equal(person.Id, Assert.Single(tracks).Id);
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var tracker = new Tracker();
        tracker.Update([Person(0.1f)], Frame * 1);
        tracker.Update([Person(0.1f)], Frame * 2);

        tracker.Reset();

        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Update([Person(0.1f)], Frame * 3));
    }

    [Fact]
    public void Live_tracks_get_distinct_colours()
    {
        var tracker = new Tracker();
        var people = Enumerable.Range(0, 6).Select(i => Person(i * 0.15f, size: 0.1f)).ToList();
        tracker.Update(people, Frame * 1);

        var tracks = tracker.Update(people, Frame * 2);

        Assert.Equal(6, tracks.Select(t => t.Rgb).Distinct().Count());
        Assert.All(tracks, t => Assert.Contains(t.Rgb, Tracker.Palette));
    }

    [Fact]
    public void The_palette_keeps_white_text_readable()
    {
        Assert.InRange(Tracker.Palette.Count, 10, 16);
        Assert.Equal(Tracker.Palette.Count, Tracker.Palette.Distinct().Count());
        foreach (var rgb in Tracker.Palette)
            Assert.True(ContrastWithWhite(rgb) >= 3.5, $"{rgb:X6}: {ContrastWithWhite(rgb):0.0}");
    }

    // WCAG contrast ratio of white on the colour.
    private static double ContrastWithWhite(uint rgb)
    {
        static double Linear(uint channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(rgb >> 16 & 0xFF) + 0.7152 * Linear(rgb >> 8 & 0xFF) + 0.0722 * Linear(rgb & 0xFF);
        return 1.05 / (luminance + 0.05);
    }
}
