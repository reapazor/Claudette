using Claudette.App.Mascot;
using Claudette.App.Tests.Support;

namespace Claudette.App.Tests;

/// <summary>
/// The README's animation of Claudette on the composer (DESIGN.md §5), drawn from her own art and steps, so it always
/// shows what she does. With <c>CLAUDETTE_MASCOT_GIF=&lt;path&gt;</c> it's written there (<c>screenshots.ps1</c> does);
/// with <c>CLAUDETTE_MASCOT_SHEETS=&lt;folder&gt;</c>, every one of her antics, frame by frame, to look over.
/// </summary>
public class MascotPictureTests
{
    /// <summary>Her patch on the canvas, home near its right, as over Send.</summary>
    private static readonly MascotRoom Room = new(30, 94, Home: 60) { EdgeLeft = 4 };

    /// <summary>What the animation shows, after she climbs up.</summary>
    private static IEnumerable<IEnumerable<MascotStep>> Playlist() =>
    [
        MascotAntics.LookAround(new Random(1)),
        MascotAntics.Dance(),
        MascotAntics.TossBall(),
        MascotAntics.Sit(new Random(2)),
        MascotAntics.Juggle(3),
        MascotAntics.Hammer(),
        MascotAntics.Confetti(),
        MascotAntics.Twirl(),
        MascotAntics.BlowHeart(),
        [.. MascotAntics.TopplesOff(new Random(3)), .. MascotAntics.ClimbUp()],
    ];

    [Fact]
    public void The_READMEs_animation_is_her_own_steps_drawn_cell_by_cell()
    {
        var canvas = new MascotCanvas();
        var frames = Animation(canvas);
        var gif = GifWriter.Write(canvas.Width, canvas.Height, canvas.Palette, frames);

        Assert.True(frames.Count > 100, $"{frames.Count} frames");
        Assert.InRange(frames.Sum(f => f.Duration.TotalSeconds), 20, 60);
        // What's written reads back as it was drawn.
        var decoded = GifReader.Frames(gif);
        Assert.Equal(frames.Count, decoded.Count);
        foreach (var i in new[] { 0, frames.Count / 3, frames.Count / 2, frames.Count - 1 })
        {
            Assert.Equal(frames[i].Pixels, decoded[i]);
        }
        if (Environment.GetEnvironmentVariable("CLAUDETTE_MASCOT_GIF") is { Length: > 0 } path)
        {
            File.WriteAllBytes(path, gif);
        }
    }

    [Fact]
    public void Every_antic_can_be_looked_over_frame_by_frame()
    {
        var antics = new (string Name, IEnumerable<MascotStep> Steps)[]
        {
            ("climb", MascotAntics.ClimbUp()), ("topple", MascotAntics.TopplesOff(new Random(1))), ("lean", MascotAntics.Lean(new Random(1))),
            ("sit", MascotAntics.Sit(new Random(1))), ("dance", MascotAntics.Dance()), ("twirl", MascotAntics.Twirl()), ("yawn", MascotAntics.Yawn()),
            ("tap", MascotAntics.TapFoot()), ("toss", MascotAntics.TossBall()), ("puzzled", MascotAntics.Puzzled()), ("heart", MascotAntics.BlowHeart()),
            ("sneeze", MascotAntics.Sneeze()), ("brow", MascotAntics.WipeBrow()), ("startled", MascotAntics.Startled()), ("grumpy", MascotAntics.Grumpy()),
            ("typing", MascotAntics.Typing(new Random(1))), ("writing", MascotAntics.Typing(new Random(1), planning: true)), ("magnify", MascotAntics.Magnify()),
            ("hammer", MascotAntics.Hammer()), ("terminal", MascotAntics.Terminal(new Random(1))), ("globe", MascotAntics.Globe()), ("juggle", MascotAntics.Juggle(3)),
            ("coffee", MascotAntics.FetchCoffee()), ("sweep", MascotAntics.Sweep()), ("point", MascotAntics.PointToSidebar("")), ("dizzy", MascotAntics.Dizzy()),
            ("confetti", MascotAntics.Confetti()), ("stamp", MascotAntics.Stamp()), ("plane", MascotAntics.PaperPlane()), ("catch", MascotAntics.CatchFile()),
            ("crate", MascotAntics.HeavyCrate()), ("doze", MascotAntics.Doze(new Random(1), hourglass: true, settle: true)), ("excited", MascotAntics.Excited()),
        };
        var canvas = new MascotCanvas();
        var rows = antics.Select(antic => Sample(canvas, antic.Steps)).ToList();
        Assert.All(rows, row => Assert.NotEmpty(row));
        foreach (var hat in MascotArt.Hats.Keys)
        {
            rows.Add([canvas.Draw(new MascotFrame("stand", 40, 0, []) { Hat = hat })]);
        }

        if (Environment.GetEnvironmentVariable("CLAUDETTE_MASCOT_SHEETS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var (sheet, i) in rows.Chunk(6).Select((chunk, i) => (chunk, i)))
            {
                File.WriteAllBytes(Path.Combine(folder, $"antics-{i}.png"), Sheet(canvas, sheet));
            }
        }
    }

    /// <summary>The animation's frames: she climbs up, then the playlist, a moment between each, run through her director.</summary>
    private static List<(byte[] Pixels, TimeSpan Duration)> Animation(MascotCanvas canvas)
    {
        using var stage = new MascotStage(seed: 4);
        stage.Show(Room);
        stage.Run(TimeSpan.FromSeconds(4.5), TimeSpan.FromMilliseconds(5));
        foreach (var steps in Playlist())
        {
            var list = steps.ToList();
            stage.Director.Perform(list);
            stage.Run(list.Aggregate(TimeSpan.Zero, (sum, step) => sum + step.Duration) + TimeSpan.FromSeconds(0.6), TimeSpan.FromMilliseconds(5));
        }
        var frames = new List<(byte[] Pixels, TimeSpan Duration)>();
        for (var i = 0; i < stage.Frames.Count; i++)
        {
            var (at, frame) = stage.Frames[i];
            var duration = (i + 1 < stage.Frames.Count ? stage.Frames[i + 1].At : at + TimeSpan.FromSeconds(1)) - at;
            var pixels = canvas.Draw(frame);
            if (frames.Count > 0 && (frames[^1].Pixels.AsSpan().SequenceEqual(pixels) || frames[^1].Duration < TimeSpan.FromMilliseconds(20)))
            {
                // The same picture, or one too short to show: one frame for both.
                frames[^1] = (pixels, frames[^1].Duration + duration);
                continue;
            }
            frames.Add((pixels, duration));
        }
        return frames;
    }

    /// <summary>Up to ten frames of <paramref name="steps"/>, evenly spaced.</summary>
    private static List<byte[]> Sample(MascotCanvas canvas, IEnumerable<MascotStep> steps)
    {
        var x = 40;
        var frames = steps.Select(step =>
        {
            x += step.Move;
            return canvas.Draw(new MascotFrame(step.Pose, x, step.Drop, step.Props ?? []));
        }).ToList();
        var every = Math.Max(1, frames.Count / 10);
        return [.. frames.Where((_, i) => i % every == 0).Take(10)];
    }

    /// <summary>Rows of frames, each cropped to around her.</summary>
    private static byte[] Sheet(MascotCanvas canvas, IReadOnlyList<List<byte[]>> rows)
    {
        const int cropLeft = (MascotCanvas.BoxLeft + 20) * MascotCanvas.Cell;
        const int cropWidth = 52 * MascotCanvas.Cell;
        var (width, height) = (cropWidth * 10, canvas.Height * rows.Count);
        var pixels = new byte[width * height];
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
            {
                for (var y = 0; y < canvas.Height; y++)
                {
                    Array.Copy(rows[row][column], y * canvas.Width + cropLeft, pixels, (row * canvas.Height + y) * width + column * cropWidth, cropWidth);
                }
            }
        }
        return PngWriter.Write(width, height, canvas.Palette, pixels);
    }

    /// <summary>Enough of a GIF reader to check what <see cref="GifWriter"/> writes: each frame's palette indices.</summary>
    private static class GifReader
    {
        public static List<byte[]> Frames(byte[] gif)
        {
            var frames = new List<byte[]>();
            var at = 13 + 3 * (1 << ((gif[10] & 7) + 1));
            while (gif[at] != 0x3B)
            {
                if (gif[at] == 0x21)
                {
                    at += 2;
                    while (gif[at] != 0)
                    {
                        at += gif[at] + 1;
                    }
                    at++;
                    continue;
                }
                var width = gif[at + 5] | gif[at + 6] << 8;
                var height = gif[at + 7] | gif[at + 8] << 8;
                var minimumBits = gif[at + 10];
                at += 11;
                var data = new List<byte>();
                while (gif[at] != 0)
                {
                    data.AddRange(gif.AsSpan(at + 1, gif[at]).ToArray());
                    at += gif[at] + 1;
                }
                at++;
                frames.Add(Decode([.. data], minimumBits, width * height));
            }
            return frames;
        }

        private static byte[] Decode(byte[] data, int minimumBits, int count)
        {
            var clear = 1 << minimumBits;
            var end = clear + 1;
            var output = new List<byte>(count);
            var table = new List<byte[]>();
            void Reset()
            {
                table.Clear();
                for (var i = 0; i < clear; i++)
                {
                    table.Add([(byte)i]);
                }
                table.Add([]);
                table.Add([]);
            }
            Reset();
            int width = minimumBits + 1, bit = 0;
            byte[]? previous = null;
            while (true)
            {
                var code = 0;
                for (var i = 0; i < width; i++, bit++)
                {
                    code |= ((data[bit >> 3] >> (bit & 7)) & 1) << i;
                }
                if (code == clear)
                {
                    Reset();
                    width = minimumBits + 1;
                    previous = null;
                    continue;
                }
                if (code == end)
                {
                    break;
                }
                byte[] entry = code < table.Count ? table[code] : [.. previous!, previous![0]];
                output.AddRange(entry);
                if (previous is not null && table.Count < 4096)
                {
                    table.Add([.. previous, entry[0]]);
                }
                previous = entry;
                if (table.Count == 1 << width && width < 12)
                {
                    width++;
                }
            }
            return [.. output];
        }
    }
}
