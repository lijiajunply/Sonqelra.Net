using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Sonqelra.Core;

namespace Sonqelra.Backends;

public static partial class MusicXml
{
    /// <summary>Read timed notes and metadata from an uncompressed partwise MusicXML file.</summary>
    public static Score Import(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new ArgumentException("Empty MusicXML document");
        if (root.Name.LocalName != "score-partwise") throw new ArgumentException("Only MusicXML score-partwise is supported");
        IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
        XElement? Child(XElement e, string name) => Children(e, name).FirstOrDefault();
        string? Text(XElement e, string name) => Child(e, name)?.Value;
        int Int(string? value) => int.Parse(value ?? throw new ArgumentException("Missing MusicXML value"), CultureInfo.InvariantCulture);
        Rational DecimalRational(string value)
        {
            decimal number = decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var bits = decimal.GetBits(number);
            var integer = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
            if ((bits[3] & int.MinValue) != 0) integer = -integer;
            return new Rational(integer, BigInteger.Pow(10, (bits[3] >> 16) & 0x7f));
        }
        var definitions = Children(Child(root, "part-list") ?? throw new ArgumentException("Missing part-list"), "score-part")
            .ToDictionary(x => (string?)x.Attribute("id") ?? throw new ArgumentException("Missing part id"));
        var tempos = new SortedDictionary<Rational, Rational> { [0] = 120 };
        var meters = new SortedDictionary<Rational, TimeSignature> { [0] = new(4, 4) };
        var tracks = new List<Track>();
        foreach (var part in Children(root, "part"))
        {
            var id = (string?)part.Attribute("id") ?? throw new ArgumentException("Missing part id");
            if (!definitions.TryGetValue(id, out var info)) throw new ArgumentException("MusicXML part has no score-part definition");
            var midi = Child(info, "midi-instrument");
            int program = midi == null ? 1 : Int(Text(midi, "midi-program") ?? "1");
            int channel = midi == null ? 1 : Int(Text(midi, "midi-channel") ?? "1");
            if (program is < 1 or > 128 || channel is < 1 or > 16) throw new ArgumentException("Invalid MusicXML MIDI program or channel");
            int divisions = 0;
            Rational position = 0;
            var notes = new List<Music>();
            var ties = new Dictionary<(string Voice, Pitch Pitch), (Rational Start, Rational Duration)>();
            foreach (var measure in Children(part, "measure"))
            {
                var start = position;
                var cursor = start;
                var end = start;
                Rational? lastStart = null;
                foreach (var item in measure.Elements())
                {
                    switch (item.Name.LocalName)
                    {
                        case "attributes":
                            if (Text(item, "divisions") is { } div)
                            {
                                divisions = Int(div);
                                if (divisions <= 0) throw new ArgumentException("MusicXML divisions must be positive");
                            }
                            if (Child(item, "time") is { } time)
                                meters[cursor] = new TimeSignature(Int(Text(time, "beats")), Int(Text(time, "beat-type")));
                            break;
                        case "direction":
                            if (Child(item, "sound")?.Attribute("tempo") is { } bpm)
                            {
                                int offset = Int(Text(item, "offset") ?? "0");
                                if (offset != 0 && divisions == 0) throw new ArgumentException("MusicXML divisions are required");
                                tempos[cursor + (offset == 0 ? 0 : new Rational(offset, divisions))] = DecimalRational(bpm.Value);
                            }
                            break;
                        case "backup":
                        case "forward":
                        case "note":
                            var kind = item.Name.LocalName;
                            if (kind == "note" && Child(item, "grace") != null) throw new ArgumentException("MusicXML grace notes are not supported");
                            if (divisions == 0) throw new ArgumentException("MusicXML divisions are required before durations");
                            var duration = new Rational(BigInteger.Parse(Text(item, "duration") ?? throw new ArgumentException("Missing duration"), CultureInfo.InvariantCulture), divisions);
                            if (duration <= 0) throw new ArgumentException("MusicXML duration must be positive");
                            if (kind == "backup")
                            {
                                cursor -= duration;
                                if (cursor < start) throw new ArgumentException("MusicXML backup precedes measure");
                                break;
                            }
                            if (kind == "forward")
                            {
                                cursor += duration;
                                if (cursor > end) end = cursor;
                                break;
                            }
                            bool chord = Child(item, "chord") != null;
                            if (chord && lastStart == null) throw new ArgumentException("MusicXML chord has no preceding note");
                            var noteStart = chord ? lastStart!.Value : cursor;
                            if (!chord) { lastStart = cursor; cursor += duration; }
                            if (noteStart + duration > end) end = noteStart + duration;
                            if (cursor > end) end = cursor;
                            if (Child(item, "pitch") is { } pitchElement)
                            {
                                var alter = DecimalRational(Text(pitchElement, "alter") ?? "0");
                                if (alter.Denominator != BigInteger.One) throw new ArgumentException("Microtonal MusicXML pitches are not supported");
                                var step = Text(pitchElement, "step") ?? "";
                                if (step.Length != 1) throw new ArgumentException("Invalid pitch step");
                                var pitch = new Pitch(step[0], (int)alter.Numerator, Int(Text(pitchElement, "octave")));
                                _ = pitch.Midi;
                                var key = (Text(item, "voice") ?? "1", pitch);
                                var types = Children(item, "tie").Select(t => (string?)t.Attribute("type")).ToHashSet();
                                if (types.Contains("stop"))
                                {
                                    if (!ties.Remove(key, out var old)) throw new ArgumentException("Unmatched MusicXML tie stop");
                                    if (old.Start + old.Duration != noteStart) throw new ArgumentException("Non-contiguous MusicXML tie");
                                    noteStart = old.Start;
                                    duration += old.Duration;
                                }
                                if (types.Contains("start")) ties[key] = (noteStart, duration);
                                else notes.Add(new Sequence([new Rest(noteStart), new Note(pitch, duration, 80)]));
                            }
                            else if (Child(item, "rest") == null) throw new ArgumentException("Unsupported MusicXML note without pitch or rest");
                            break;
                    }
                }
                position = end;
            }
            if (ties.Count != 0) throw new ArgumentException("Unclosed MusicXML tie");
            notes.Add(new Rest(position));
            tracks.Add(new Track(new Sonqelra.Core.Parallel(notes), Instruments.GeneralMidi[program - 1], Text(info, "part-name") ?? id, channel == 10));
        }
        if (tracks.Count == 0) throw new ArgumentException("MusicXML score contains no parts");
        var work = Child(root, "work");
        string title = (work == null ? null : Text(work, "work-title")) ?? Text(root, "movement-title") ?? Path.GetFileNameWithoutExtension(path);
        return new Score(tracks, title, new TempoMap(tempos.Select(x => (x.Key, x.Value))), new MeterMap(meters.Select(x => (x.Key, x.Value))));
    }
}
