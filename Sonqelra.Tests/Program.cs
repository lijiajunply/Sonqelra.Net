using Sonqelra.Core;
using Sonqelra.Lang;
using Sonqelra.Backends;
using System.Xml.Linq;

var tests = new (string Name, Action Test)[]
{
    ("Rational and pitch", () =>
    {
        Equal(new Rational(1, 2), new Rational(2, 4));
        Equal(new Rational(5, 6), new Rational(1, 2) + new Rational(1, 3));
        for (int i = 0; i < 128; i++)
        {
            Equal(i, Pitch.FromMidi(i).Midi);
            Equal(i, Pitch.FromMidi(i, Spelling.Flat).Midi);
        }

        Equal("Db4", Pitch.Parse("Db4").Transpose(0).Name);
        Equal("C#4", Pitch.Parse("Db4").Transpose(12).Transpose(-12).Name);
    }),
    ("Music composition", () =>
    {
        Music music = (Note.Create("C4", 1) + new Rest(new Rational(1, 2)) + Note.Create("D4", 1)).Repeat(2);
        Equal((Rational)5, music.Duration);
        Equal("0,3/2,5/2,4", string.Join(',', music.IterEvents().Select(e => e.Start)));
        Equal((Rational)2, (Note.Create("C4", 1) | Note.Create("E4", 2)).Duration);
        var lazy = Note.Create("C9", 1).Transpose(12);
        Throws<ArgumentOutOfRangeException>(() => lazy.Events());
    }),
    ("Lazy event expansion", () =>
    {
        var first = new CountingMusic();
        var second = new CountingMusic();
        Music music = new Sequence([first, second]).Repeat(2).Transpose(1);
        var iterator = music.IterEvents();
        Equal(0, first.Enumerations);
        Equal(0, second.Enumerations);
        var one = iterator.Take(1).Single();
        Equal(61, one.Pitch.Midi);
        Equal(1, first.Enumerations);
        Equal(0, second.Enumerations);
        var infinite = new InfiniteMusic();
        Equal(3, (new Sequence([infinite, second])).IterEvents().Take(3).Count());
        Equal(0, second.Enumerations);
    }),
    ("Language arrays and pipeline", () =>
    {
        var result = Compiler.CompileSource("""
                                            score "Example" {
                                             meter 4/4
                                             fn build(root: Pitch) -> Music {
                                              let pattern = [root, root + 4st, root + 7st,]
                                              return chord(pattern, velocity: 100, duration: 1/2) + sequence(pattern, duration: 1/8)
                                             }
                                             let part = build(C4)
                                               |> repeat(2)
                                             track piano {
                                              instrument "Piano"
                                              play part
                                             }
                                            }
                                            """);
        var score = result.Scores[0].Score;
        Equal(4, score.MeterMap.Meter[0].Signature.Numerator);
        Equal(4, score.MeterMap.Meter[0].Signature.Denominator);
        Equal((Rational)7, score.Tracks[0].Music.Duration);
        Equal("0,0,0,2,5/2,3", string.Join(',', score.Tracks[0].Music.IterEvents().Take(6).Select(e => e.Start)));
    }),
    ("Diagnostics", () =>
    {
        var e = Throws<SemanticException>(() =>
            Compiler.CompileSource("score \"x\" {\n tempo 100\n tempo 120\n}", "bad.sonqelra"));
        Equal(3, e.Span.Start.Line);
        Equal(2, e.Span.Start.Column);
        Throws<ParseException>(() => Compiler.CompileSource("score \"x\" {\n let x = note(C4, 1,)\n}"));
        Throws<SemanticException>(() =>
            Compiler.CompileSource("score \"x\" {\n fn bad(x: Pitch) -> Music {\n return x\n }\n}"));
    }),
    ("Language semantic rejection", () =>
    {
        string[] invalid =
        [
            "let x = chord([C4], duration: 1/4, duration: 1/8)", "let x = chord([C4], 1/4)",
            "let x = chord([C4], duration: 1/4, bogus: 1)", "let x = chord(duration: 1/4, [C4])", "let x = []",
            "let x = [C4, 2]", "fn bad(x: Pitch) -> Music {\n return missing(x)\n }",
            "fn bad(x: Pitch) -> Music {\n return x\n }"
        ];
        foreach (var body in invalid)
        {
            var source = $"score \"x\" {{\n{body}\n track t {{\n instrument \"Piano\"\n play rest(1)\n }}\n}}";
            Throws<SemanticException>(() => Compiler.CompileSource(source));
        }

        Throws<SemanticException>(() => Compiler.CompileSource("score \"x\" {\n melody m {\n rest:1/4@5\n }\n}"));
    }),
    ("MIDI and XML roundtrip", () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonqelra-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var track = new Track(Note.Create("C4", 1) + new Rest(1) + Note.Create("E4", new Rational(1, 2), 100),
                "Piano", "piano", false, [new ControlChange(0, 64, 127), new PitchBend(1, -100)]);
            var score = new Score([track], "Example",
                new TempoMap([(0, new Rational(240, 2)), (2, new Rational(300, 2))]),
                new MeterMap([(0, new TimeSignature(4, 4))]));
            var midi = Path.Combine(dir, "x.mid");
            Midi.Export(score, midi);
            var loaded = Midi.Import(midi);
            Equal("60,64", string.Join(',', loaded.Tracks[0].Music.IterEvents().Select(e => e.Pitch.Midi)));
            Equal(2, loaded.Tracks[0].Controls.Count);
            Equal((Rational)150, loaded.TempoMap.Tempo[1].Bpm);
            var xml = Path.Combine(dir, "x.musicxml");
            MusicXml.Export(score, xml);
            var doc = XDocument.Load(xml);
            Equal("Example", doc.Root!.Element("work")!.Element("work-title")!.Value);
            Equal("C", doc.Descendants("pitch").First().Element("step")!.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }),
    ("Export preflight and override", () =>
    {
        var source = """
                     score "x" {
                      track t {
                       instrument "Piano"
                       play note(C4, 1)
                      }
                      export midi "x.mid"
                      export custom "x.txt"
                     }
                     """;
        var result = Compiler.CompileSource(source);
        var dir = Path.Combine(Path.GetTempPath(), "sonqelra-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            Throws<ExportException>(() => Exports.Execute(result, dir));
            Equal(0, Directory.GetFiles(dir).Length);
            Exports.Execute(result, dir,
                new Dictionary<string, Action<Score, string>>
                    { { "custom", (score, path) => File.WriteAllText(path, score.Title) } });
            Equal("x", File.ReadAllText(Path.Combine(dir, "x.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }),
    ("Export rejection and time maps", () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonqelra-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var duplicate =
                Compiler.CompileSource(
                    "score \"x\" {\n track t {\n instrument \"Piano\"\n play note(C4, 1)\n }\n export midi \"x.mid\"\n export midi \"./x.mid\"\n}");
            Throws<ExportException>(() => Exports.Execute(duplicate, dir));
            Equal(0, Directory.GetFiles(dir).Length);
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path.Combine(dir, "actual"));
                Directory.CreateSymbolicLink(Path.Combine(dir, "alias"), Path.Combine(dir, "actual"));
                var linked =
                    Compiler.CompileSource(
                        "score \"x\" {\n track t {\n instrument \"Piano\"\n play note(C4, 1)\n }\n export midi \"actual/x.mid\"\n export midi \"alias/x.mid\"\n}");
                Throws<ExportException>(() => Exports.Execute(linked, dir));
            }

            var score = new Score([new Track(Note.Create("C4", new Rational(1, 7)), "Piano", "t")], "x");
            Throws<ArgumentException>(() => Midi.Export(score, Path.Combine(dir, "inexact.mid")));
            var overlapping = new Score([new Track(Note.Create("C4", 2) | Note.Create("C4", 1), "Piano", "t")], "x");
            Throws<ArgumentException>(() => Midi.Export(overlapping, Path.Combine(dir, "overlap.mid")));
            var badMeter = new Score([new Track(new Rest(8), "Piano", "t")], "x",
                meterMap: new MeterMap([(0, new TimeSignature(4, 4)), (2, new TimeSignature(3, 4))]));
            Throws<ArgumentException>(() => MusicXml.Export(badMeter, Path.Combine(dir, "bad.xml")));
            Throws<ArgumentException>(() =>
            {
                var tempoMap = new TempoMap([(1, 120)]);
            });
            Throws<ArgumentException>(() =>
            {
                var timeSignature = new TimeSignature(4, 3);
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }),
    ("Import external MIDI polyphony and controls", () =>
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var score = Midi.Import(Path.Combine(fixtures, "polyphony.mid"));
        Equal(2, score.Tracks.Count);
        Equal("Violin", score.Tracks[0].Instrument);
        Equal("Acoustic Grand Piano", score.Tracks[1].Instrument);
        Equal(3, score.Tracks[0].Music.Duration);
        Equal(new Rational(1, 2), score.Tracks[0].Music.Events()[0].Start);
        Equal(new Rational(3, 2), score.Tracks[1].Music.Events()[0].Duration);
        Equal(100, score.TempoMap.Tempo[0].Bpm);
        Equal(3, score.MeterMap.Meter[0].Signature.Numerator);
        var drums = Midi.Import(Path.Combine(fixtures, "drums.mid"));
        Equal(new Rational(60000000, 500001), drums.TempoMap.Tempo[0].Bpm);
        Equal(true, drums.Tracks[0].IsPercussion);
        Equal(3, drums.Tracks[0].Controls.Count);
        Equal(new PitchBend(new Rational(1, 2), -1024), drums.Tracks[0].Controls[1]);
    }),
    ("MusicXML voices and ties", () =>
    {
        Music part = (new Rest(3) + Note.Create("Db4", 2)) | Note.Create("G4", new Rational(1, 3)) |
                     Note.Create("E4", 1);
        var score = new Score([new Track(part, "Piano", "Keys"), new Track(Note.Create("C5", 1), "Flute", "Flute")],
            "标题", new TempoMap([(0, 120), (new Rational(1, 3), 90)]),
            new MeterMap([(0, new TimeSignature(4, 4)), (4, new TimeSignature(3, 4))]));
        var path = Path.GetTempFileName();
        try
        {
            MusicXml.Export(score, path);
            var root = XDocument.Load(path).Root!;
            Equal(2, root.Elements("part").Count());
            var measures = root.Element("part")!.Elements("measure").ToArray();
            Equal("3", measures[0].Element("attributes")!.Element("divisions")!.Value);
            Equal("1",
                measures[0].Elements("direction").First(d => d.Element("offset") != null).Element("offset")!.Value);
            Equal(true, measures[0].Element("backup") != null);
            Equal("-1",
                measures[0].Elements("note").First(n => n.Element("pitch")?.Element("step")?.Value == "D")
                    .Element("pitch")!.Element("alter")!.Value);
            Equal("start",
                measures[0].Elements("note").First(n => n.Element("tie") != null).Element("tie")!.Attribute("type")!
                    .Value);
            Equal("stop",
                measures[1].Elements("note").First(n => n.Element("tie") != null).Element("tie")!.Attribute("type")!
                    .Value);
            Equal("3", measures[1].Element("attributes")!.Element("time")!.Element("beats")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }),
    ("Existing example", () =>
    {
        var result =
            Compiler.CompileSource(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "asdf.sonqelra")));
        Equal(1, result.Scores.Count);
        Equal("E", result.Scores[0].Key!.Root);
        Equal("minor", result.Scores[0].Key!.Mode);
        var lines = new List<string>();
        foreach (var sr in result.Scores)
        {
            lines.Add($"S|{sr.Score.Title}|{sr.Key?.Root}|{sr.Key?.Mode}");
            foreach (var track in sr.Score.Tracks)
            {
                lines.Add($"T|{track.Name}|{track.Instrument}|{track.IsPercussion}|{track.Music.Duration}");
                foreach (var e in track.Music.IterEvents().OrderBy(e => e.Start).ThenBy(e => e.Pitch.Midi)
                             .ThenBy(e => e.Duration)
                             .ThenBy(e => e.Velocity))
                    lines.Add($"E|{e.Start}|{e.Duration}|{e.Pitch.Midi}|{e.Velocity}");
            }
        }

        Equal(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "asdf.expected")).Replace("\r\n", "\n"),
            string.Join('\n', lines) + "\n");
    })
};
int failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

return failures == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}

static T Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T e)
    {
        return e;
    }

    throw new Exception($"Expected {typeof(T).Name}");
}

sealed class CountingMusic : Music
{
    public int Enumerations { get; private set; }
    public override Rational Duration => 1;

    public override IEnumerable<Event> IterEvents()
    {
        Enumerations++;
        yield return new Event(0, 1, Pitch.Parse("C4"));
    }
}

sealed class InfiniteMusic : Music
{
    public override Rational Duration => 1;

    public override IEnumerable<Event> IterEvents()
    {
        while (true) yield return new Event(0, 1, Pitch.Parse("C4"));
    }
}