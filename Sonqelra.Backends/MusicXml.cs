using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Sonqelra.Core;

namespace Sonqelra.Backends;

public static class MusicXml
{
    private sealed record Bar(Rational Start, Rational End, TimeSignature Meter, int MeterIndex);

    public static void Export(Score score, string path)
    {
        var events = score.Tracks.Select(t => t.Music.IterEvents().ToArray()).ToArray();
        var end = score.Tracks.Select(t => t.Music.Duration).DefaultIfEmpty(0).Max();
        var metaEnd = new[] { score.TempoMap.Tempo[^1].Start, score.MeterMap.Meter[^1].Start }.Max();
        var meters = score.MeterMap.Meter;
        var bars = new List<Bar>();
        Rational position = 0;
        int meterIndex = 0;
        while (bars.Count == 0 || position < end || position <= metaEnd)
        {
            if (meterIndex + 1 < meters.Count && meters[meterIndex + 1].Start == position) meterIndex++;
            var signature = meters[meterIndex].Signature;
            var stop = position + new Rational(4 * signature.Numerator, signature.Denominator);
            if (meterIndex + 1 < meters.Count && meters[meterIndex + 1].Start < stop)
                throw new ArgumentException("MusicXML meter changes must fall on measure boundaries");
            bars.Add(new(position, stop, signature, meterIndex));
            position = stop;
        }

        var times = new List<Rational> { end };
        times.AddRange(score.TempoMap.Tempo.Select(t => t.Start));
        times.AddRange(meters.Select(t => t.Start));
        foreach (var bar in bars)
        {
            times.Add(bar.Start);
            times.Add(bar.End);
        }

        foreach (var e in events.SelectMany(e => e))
        {
            if (e.Start < 0 || e.Start + e.Duration > end)
                throw new ArgumentException("Event lies outside the score duration");
            times.Add(e.Start);
            times.Add(e.Duration);
        }

        BigInteger divisions = 1;
        foreach (var time in times)
            divisions = BigInteger.Abs(divisions / BigInteger.GreatestCommonDivisor(divisions, time.Denominator) *
                                       time.Denominator);
        XElement E(string name, object? value = null) => value is null ? new(name) : new(name, value);

        string T(Rational n) =>
            (n.Numerator * divisions / n.Denominator).ToString(CultureInfo.InvariantCulture);

        string Bpm(Rational n) => ((double)n).ToString("G15", CultureInfo.InvariantCulture);
        var root = new XElement("score-partwise", new XAttribute("version", "4.0"),
            new XElement("work", E("work-title", score.Title)));
        var partList = new XElement("part-list");
        root.Add(partList);
        for (int i = 0; i < score.Tracks.Count; i++)
        {
            var track = score.Tracks[i];
            var id = i + 1;
            var info = new XElement("score-part", new XAttribute("id", $"P{id}"), E("part-name", track.Name),
                new XElement("score-instrument", new XAttribute("id", $"I{id}"),
                    E("instrument-name", track.Instrument)));
            try
            {
                info.Add(new XElement("midi-instrument", new XAttribute("id", $"I{id}"),
                    E("midi-program", track.InstrumentToMidiProgram() + 1)));
            }
            catch (ArgumentException)
            {
            }

            partList.Add(info);
        }

        for (int i = 0; i < events.Length; i++)
        {
            var part = new XElement("part", new XAttribute("id", $"P{i + 1}"));
            root.Add(part);
            var voices = new List<List<Event>>();
            var ends = new List<Rational>();
            foreach (var note in events[i].OrderBy(e => e.Start).ThenBy(e => e.Pitch.Midi).ThenBy(e => e.Duration))
            {
                int voice = ends.FindIndex(last => last <= note.Start);
                if (voice < 0)
                {
                    voice = ends.Count;
                    ends.Add(0);
                    voices.Add([]);
                }

                voices[voice].Add(note);
                ends[voice] = note.Start + note.Duration;
            }

            if (voices.Count == 0) voices.Add([]);
            for (int j = 0; j < bars.Count; j++)
            {
                var bar = bars[j];
                var measure = new XElement("measure", new XAttribute("number", j + 1));
                part.Add(measure);
                if (j == 0 || meters[bar.MeterIndex].Start == bar.Start)
                {
                    var attr = new XElement("attributes");
                    if (j == 0) attr.Add(E("divisions", divisions));
                    attr.Add(new XElement("time", E("beats", bar.Meter.Numerator),
                        E("beat-type", bar.Meter.Denominator)));
                    if (j == 0) attr.Add(new XElement("clef", E("sign", "G"), E("line", 2)));
                    measure.Add(attr);
                }

                foreach (var (start, bpm) in score.TempoMap.Tempo.Where(t => bar.Start <= t.Start && t.Start < bar.End))
                {
                    var direction = new XElement("direction",
                        new XElement("direction-type",
                            new XElement("metronome", E("beat-unit", "quarter"), E("per-minute", Bpm(bpm)))));
                    if (start != bar.Start) direction.Add(E("offset", T(start - bar.Start)));
                    direction.Add(new XElement("sound", new XAttribute("tempo", Bpm(bpm))));
                    measure.Add(direction);
                }

                for (int vi = 0; vi < voices.Count; vi++)
                {
                    if (vi > 0) measure.Add(new XElement("backup", E("duration", T(bar.End - bar.Start))));
                    var cursor = bar.Start;
                    foreach (var note in voices[vi])
                    {
                        var noteEnd = note.Start + note.Duration;
                        if (note.Start >= bar.End || noteEnd <= bar.Start) continue;
                        var onset = note.Start > bar.Start ? note.Start : bar.Start;
                        var offset = noteEnd < bar.End ? noteEnd : bar.End;
                        if (onset > cursor) WriteNote(measure, null, onset - cursor, divisions, vi + 1, false, false);
                        WriteNote(measure, note, offset - onset, divisions, vi + 1, note.Start < onset,
                            noteEnd > offset);
                        cursor = offset;
                    }

                    if (cursor < bar.End) WriteNote(measure, null, bar.End - cursor, divisions, vi + 1, false, false);
                }
            }
        }

        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    private static void WriteNote(XElement parent, Event? e, Rational duration, BigInteger divisions, int voice,
        bool tieStop, bool tieStart)
    {
        var note = new XElement("note");
        parent.Add(note);
        if (e is null) note.Add(new XElement("rest"));
        else
        {
            var pitch = new XElement("pitch", new XElement("step", e.Pitch.Step));
            if (e.Pitch.Accidental != 0) pitch.Add(new XElement("alter", e.Pitch.Accidental));
            pitch.Add(new XElement("octave", e.Pitch.Octave));
            note.Add(pitch);
        }

        note.Add(new XElement("duration", duration.Numerator * divisions / duration.Denominator));
        if (tieStop) note.Add(new XElement("tie", new XAttribute("type", "stop")));
        if (tieStart) note.Add(new XElement("tie", new XAttribute("type", "start")));
        note.Add(new XElement("voice", voice));
        var types = new (string Name, Rational Duration)[]
        {
            ("maxima", 32), ("long", 16), ("breve", 8), ("whole", 4), ("half", 2), ("quarter", 1),
            ("eighth", new(1, 2)), ("16th", new(1, 4)), ("32nd", new(1, 8)), ("64th", new(1, 16)),
            ("128th", new(1, 32)), ("256th", new(1, 64)), ("512th", new(1, 128)), ("1024th", new(1, 256))
        };
        var multipliers = new Rational[] { 1, new(3, 2), new(7, 4), new(15, 8) };
        string? chosen = null;
        int dots = 0;
        foreach (var (name, basis) in types)
        {
            for (int d = 0; d < multipliers.Length; d++)
                if (duration == basis * multipliers[d])
                {
                    chosen = name;
                    dots = d;
                    break;
                }

            if (chosen != null) break;
        }

        Rational? ratio = null;
        if (chosen is null)
        {
            var closest = types.MinBy(x => Math.Abs((double)(duration / x.Duration) - 1));
            chosen = closest.Name;
            ratio = duration / closest.Duration;
        }

        note.Add(new XElement("type", chosen));
        for (int d = 0; d < dots; d++) note.Add(new XElement("dot"));
        if (ratio is { } r)
            note.Add(new XElement("time-modification", new XElement("actual-notes", r.Denominator),
                new XElement("normal-notes", r.Numerator)));
        if (tieStop || tieStart)
        {
            var notation = new XElement("notations");
            if (tieStop) notation.Add(new XElement("tied", new XAttribute("type", "stop")));
            if (tieStart) notation.Add(new XElement("tied", new XAttribute("type", "start")));
            note.Add(notation);
        }
    }
}