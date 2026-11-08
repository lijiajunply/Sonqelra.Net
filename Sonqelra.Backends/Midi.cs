using System.Numerics;
using System.Text;
using Sonqelra.Core;

namespace Sonqelra.Backends;

public static class Midi
{
    private sealed record Message(long Tick, int Order, byte[] Data);

    private static byte[] Be(int value, int length)
    {
        var b = new byte[length];
        for (int i = length - 1; i >= 0; i--)
        {
            b[i] = (byte)value;
            value >>= 8;
        }

        return b;
    }

    private static byte[] Var(long value)
    {
        if (value < 0 || value > 0x0fffffff)
            throw new ArgumentOutOfRangeException(nameof(value), "MIDI delta out of range");
        var list = new List<byte> { (byte)(value & 127) };
        while ((value >>= 7) > 0) list.Add((byte)((value & 127) | 128));
        list.Reverse();
        return list.ToArray();
    }

    private static byte[] Meta(byte type, byte[] data) =>
        [0xff, type, .. Var(data.Length), .. data];

    private static long ToTicks(Rational value, int division = 480)
    {
        var ticks = value * division;
        if (ticks < 0 || ticks.Denominator != BigInteger.One || ticks.Numerator > long.MaxValue)
            throw new ArgumentException(
                $"Time {value} cannot be represented exactly at {division} ticks per quarter note");
        return (long)ticks.Numerator;
    }

    public static void Export(Score score, string path)
    {
        var channels = Enumerable.Range(0, 16).Where(c => c != 9).ToArray();
        if (score.Tracks.Count(t => !t.IsPercussion) > 15)
            throw new ArgumentException("MIDI export supports at most 15 melodic tracks");
        if (score.Tracks.Where(t => t.IsPercussion).Select(t => t.InstrumentToMidiProgram()).Distinct().Count() > 1)
            throw new ArgumentException("MIDI percussion tracks sharing channel 10 must use the same program");
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        stream.Write(Be(6, 4));
        stream.Write(Be(1, 2));
        stream.Write(Be(score.Tracks.Count, 2));
        stream.Write(Be(480, 2));
        int next = 0;
        for (int index = 0; index < score.Tracks.Count; index++)
        {
            var track = score.Tracks[index];
            int channel = track.IsPercussion ? 9 : channels[next++];
            var events = new List<Message>();
            void Add(long tick, int order, params byte[] data) => events.Add(new(tick, order, data));
            Add(0, 0, Meta(3, Encoding.UTF8.GetBytes(track.Name)));
            Add(0, 0, (byte)(0xc0 | channel), (byte)track.InstrumentToMidiProgram());
            foreach (var c in track.Controls)
            {
                if (c is ControlChange cc)
                    Add(ToTicks(cc.Start), 0, (byte)(0xb0 | channel), (byte)cc.Control, (byte)cc.Value);
                else if (c is PitchBend pb)
                {
                    int value = pb.Pitch + 8192;
                    Add(ToTicks(pb.Start), 0, (byte)(0xe0 | channel), (byte)(value & 127), (byte)(value >> 7));
                }
                else throw new ArgumentException("Unsupported track control");
            }

            if (index == 0)
            {
                foreach (var (start, bpm) in score.TempoMap.Tempo)
                {
                    var us = new Rational(60_000_000) / bpm;
                    var tempo = (int)Math.Round((double)us, MidpointRounding.ToEven);
                    if (tempo is < 1 or > 0xffffff) throw new ArgumentException("MIDI tempo out of range");
                    Add(ToTicks(start), 0, Meta(0x51, Be(tempo, 3)));
                }

                foreach (var (start, meter) in score.MeterMap.Meter)
                {
                    var exponent = BitOperations.Log2((uint)meter.Denominator);
                    if (meter.Numerator > 255 || exponent > 255)
                        throw new ArgumentException("MIDI time signature out of range");
                    Add(ToTicks(start), 0, Meta(0x58, [(byte)meter.Numerator, (byte)exponent, 24, 8]));
                }
            }

            var intervals = new Dictionary<int, List<(long Start, long End)>>();
            foreach (var note in track.Music.IterEvents())
            {
                if (note.Velocity == 0) throw new ArgumentException("MIDI note-on velocity must be between 1 and 127");
                long start = ToTicks(note.Start), end = ToTicks(note.Start + note.Duration);
                int pitch = note.Pitch.Midi;
                if (!intervals.TryGetValue(pitch, out var spans)) intervals[pitch] = spans = [];
                spans.Add((start, end));
                Add(start, 1, (byte)(0x90 | channel), (byte)pitch, (byte)note.Velocity);
                Add(end, 0, (byte)(0x80 | channel), (byte)pitch, 0);
            }

            foreach (var spans in intervals.Values)
            {
                var sorted = spans.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
                for (int j = 1; j < sorted.Length; j++)
                    if (sorted[j].Start < sorted[j - 1].End)
                        throw new ArgumentException("Overlapping notes with the same MIDI pitch");
            }

            long endTick = Math.Max(ToTicks(track.Music.Duration), events.Max(e => e.Tick));
            Add(endTick, 2, Meta(0x2f, []));
            using var body = new MemoryStream();
            long previous = 0;
            foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
            {
                body.Write(Var(e.Tick - previous));
                body.Write(e.Data);
                previous = e.Tick;
            }

            stream.Write("MTrk"u8);
            stream.Write(Be(checked((int)body.Length), 4));
            body.Position = 0;
            body.CopyTo(stream);
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    private sealed record MidiEvent(long Tick, int Track, int Kind, int Channel, int A, int B, string? Text = null);

    private sealed class Reader(byte[] bytes)
    {
        private int _offset;
        public int Offset => _offset;
        public int Remaining => bytes.Length - _offset;

        public int Byte()
        {
            if (_offset >= bytes.Length) throw new InvalidDataException("Unexpected end of MIDI file");
            return bytes[_offset++];
        }

        public int Big(int n)
        {
            int result = 0;
            for (int i = 0; i < n; i++) result = checked(result * 256 + Byte());
            return result;
        }

        public long Variable()
        {
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = Byte();
                value = (value << 7) | (uint)(b & 127);
                if ((b & 128) == 0) return value;
            }

            throw new InvalidDataException("Invalid MIDI variable length quantity");
        }

        public byte[] Bytes(int n)
        {
            if (n < 0 || n > Remaining) throw new InvalidDataException("Truncated MIDI file");
            var r = bytes.AsSpan(_offset, n).ToArray();
            _offset += n;
            return r;
        }
    }

    public static Score Import(string path)
    {
        var reader = new Reader(File.ReadAllBytes(path));
        if (Encoding.ASCII.GetString(reader.Bytes(4)) != "MThd") throw new InvalidDataException("Invalid MIDI header");
        int size = reader.Big(4);
        if (size < 6) throw new InvalidDataException("Invalid MIDI header");
        int format = reader.Big(2), trackCount = reader.Big(2), division = reader.Big(2);
        reader.Bytes(size - 6);
        if (format == 2) throw new ArgumentException("MIDI type 2 contains independent timelines");
        if (format is not (0 or 1)) throw new ArgumentException("Unsupported MIDI type");
        if ((division & 0x8000) != 0 || division == 0)
            throw new ArgumentException("SMPTE time division is not supported");
        var all = new List<MidiEvent>();
        var trackEnds = new long[trackCount];
        var names = new string[trackCount];
        for (int tr = 0; tr < trackCount; tr++)
        {
            if (Encoding.ASCII.GetString(reader.Bytes(4)) != "MTrk")
                throw new InvalidDataException("Expected MIDI track");
            int length = reader.Big(4);
            var r = new Reader(reader.Bytes(length));
            long tick = 0;
            int running = 0;
            while (r.Remaining > 0)
            {
                tick = checked(tick + r.Variable());
                int status = r.Byte();
                int first = -1;
                if (status < 128)
                {
                    if (running == 0) throw new InvalidDataException("Invalid running status");
                    first = status;
                    status = running;
                }
                else if (status < 0xf0) running = status;
                else running = 0;

                if (status == 0xff)
                {
                    int type = r.Byte();
                    int len = checked((int)r.Variable());
                    var data = r.Bytes(len);
                    if (type == 3) names[tr] = Encoding.UTF8.GetString(data);
                    else if (type == 0x51 && len == 3)
                        all.Add(new(tick, tr, 0x51, 0, (data[0] << 16) | (data[1] << 8) | data[2], 0));
                    else if (type == 0x58 && len >= 2)
                    {
                        if (data[1] > 30) throw new InvalidDataException("Invalid MIDI meter");
                        all.Add(new(tick, tr, 0x58, 0, data[0], 1 << data[1]));
                    }

                    if (type == 0x2f) break;
                    continue;
                }

                if (status is 0xf0 or 0xf7)
                {
                    r.Bytes(checked((int)r.Variable()));
                    continue;
                }

                if (status >= 0xf0) throw new InvalidDataException("Unsupported MIDI message");
                int kind = status & 0xf0, ch = status & 15;
                int a = first >= 0 ? first : r.Byte();
                int b = kind is 0xc0 or 0xd0 ? 0 : r.Byte();
                if (a > 127 || b > 127) throw new InvalidDataException("Invalid MIDI data");
                all.Add(new(tick, tr, kind, ch, a, b));
            }

            trackEnds[tr] = tick;
        }

        var tempos = new SortedDictionary<long, Rational> { { 0, 120 } };
        var meters = new SortedDictionary<long, TimeSignature> { { 0, new(4, 4) } };
        foreach (var e in all.Where(e => e.Kind is 0x51 or 0x58).OrderBy(e => e.Tick).ThenBy(e => e.Track))
        {
            if (e.Kind == 0x51)
            {
                if (e.A == 0) throw new ArgumentException("MIDI tempo must be positive");
                tempos[e.Tick] = new(60_000_000, e.A);
            }
            else meters[e.Tick] = new(e.A, e.B);
        }

        var controls = new Dictionary<int, List<(long Tick, int Track, TrackControl Control)>>();
        var controlParts = new Dictionary<int, (int Track, int Program)>();
        var parts = new List<Dictionary<(int Channel, int Program), List<Music>>>();
        for (int tr = 0; tr < trackCount; tr++)
        {
            var programs = new int[16];
            var active = new Dictionary<(int, int), Queue<(long Start, int Velocity, int Program)>>();
            var part = new Dictionary<(int, int), List<Music>>();
            parts.Add(part);
            foreach (var e in all.Where(e => e.Track == tr && e.Kind < 0xf0))
            {
                if (e.Kind == 0xc0) programs[e.Channel] = e.A;
                else if (e.Kind is 0xb0 or 0xe0)
                {
                    TrackControl c = e.Kind == 0xb0
                        ? new ControlChange(new(e.Tick, division), e.A, e.B)
                        : new PitchBend(new(e.Tick, division), (e.A | (e.B << 7)) - 8192);
                    if (!controls.TryGetValue(e.Channel, out var list)) controls[e.Channel] = list = [];
                    list.Add((e.Tick, tr, c));
                    controlParts.TryAdd(e.Channel, (tr, programs[e.Channel]));
                }
                else if (e is { Kind: 0x90, B: > 0 })
                {
                    var key = (e.Channel, e.A);
                    if (!active.TryGetValue(key, out var q)) active[key] = q = new();
                    q.Enqueue((e.Tick, e.B, programs[e.Channel]));
                }
                else if (e.Kind == 0x80 || e is { Kind: 0x90, B: 0 })
                {
                    var key = (e.Channel, e.A);
                    if (!active.TryGetValue(key, out var q) || q.Count == 0)
                        throw new ArgumentException($"Unmatched note-off in MIDI track {tr}");
                    var (start, velocity, program) = q.Dequeue();
                    if (e.Tick <= start) throw new ArgumentException("Non-positive note duration");
                    if (!part.TryGetValue((e.Channel, program), out var notes)) part[(e.Channel, program)] = notes = [];
                    notes.Add(new Sequence([
                        new Rest(new(start, division)),
                        new Note(Pitch.FromMidi(e.A), new(e.Tick - start, division), velocity)
                    ]));
                }
            }

            if (active.Values.Any(q => q.Count > 0)) throw new ArgumentException($"Unclosed notes in MIDI track {tr}");
        }

        var noteChannels = parts.SelectMany(p => p.Keys).Select(k => k.Channel).ToHashSet();
        foreach (var (channel, part) in controlParts)
            if (!noteChannels.Contains(channel))
                parts[part.Track][(channel, part.Program)] = [];
        var tracks = new List<Track>();
        for (int tr = 0; tr < trackCount; tr++)
            foreach (var (key, notes) in parts[tr])
            {
                string name = string.IsNullOrEmpty(names[tr]) ? $"Track {tr + 1}" : names[tr];
                if (parts[tr].Count > 1) name += $" (channel {key.Channel + 1}, program {key.Program + 1})";
                var music = new Core.Parallel(notes.Append(new Rest(new Rational(trackEnds[tr], division))));
                var cs = controls.TryGetValue(key.Channel, out var list)
                    ? list.OrderBy(c => c.Tick).ThenBy(c => c.Track).Select(c => c.Control)
                    : [];
                tracks.Add(new(music, Instruments.GeneralMidi[key.Program], name, key.Channel == 9, cs));
            }

        if (tracks.Count == 0) tracks.Add(new(new Rest(0), "Piano", "Track 1"));
        return new Score(tracks, Path.GetFileNameWithoutExtension(path),
            new TempoMap(tempos.Select(t => (new Rational(t.Key, division), t.Value))),
            new MeterMap(meters.Select(m => (new Rational(m.Key, division), m.Value))));
    }
}