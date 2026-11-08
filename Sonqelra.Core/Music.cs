using System.Text.RegularExpressions;

namespace Sonqelra.Core;

public enum Spelling
{
    Sharp,
    Flat
}

public sealed record Pitch
{
    private static readonly Dictionary<char, int> Steps = new()
        { ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11 };

    private static readonly (char, int)[] Sharp =
    [
        ('C', 0), ('C', 1), ('D', 0), ('D', 1), ('E', 0), ('F', 0), ('F', 1), ('G', 0), ('G', 1), ('A', 0), ('A', 1),
        ('B', 0)
    ];

    private static readonly (char, int)[] Flat =
    [
        ('C', 0), ('D', -1), ('D', 0), ('E', -1), ('E', 0), ('F', 0), ('G', -1), ('G', 0), ('A', -1), ('A', 0),
        ('B', -1), ('B', 0)
    ];

    public char Step { get; }
    public int Accidental { get; }
    public int Octave { get; }

    public Pitch(char step, int accidental, int octave)
    {
        if (!Steps.ContainsKey(step)) throw new ArgumentException("Invalid pitch step");
        Step = step;
        Accidental = accidental;
        Octave = octave;
    }

    public int Midi
    {
        get
        {
            var value = 12L * (Octave + 1L) + Steps[Step] + Accidental;
            if (value is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(Midi), "Pitch outside MIDI range");
            return (int)value;
        }
    }

    public string Name => $"{Step}{new string(Accidental >= 0 ? '#' : 'b', Math.Abs(Accidental))}{Octave}";
    public override string ToString() => Name;

    public static Pitch Parse(string name)
    {
        var m = Regex.Match(name, @"\A([A-G])(#*|b*)(-?\d+)\z", RegexOptions.CultureInvariant);
        if (!m.Success) throw new ArgumentException($"Invalid pitch: {name}");
        var marks = m.Groups[2].Value;
        return new(m.Groups[1].Value[0], marks.StartsWith('b') ? -marks.Length : marks.Length,
            int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static Pitch FromMidi(int midi, Spelling spelling = Spelling.Sharp)
    {
        if (midi is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midi));
        var table = spelling switch
        {
            Spelling.Sharp => Sharp, Spelling.Flat => Flat, _ => throw new ArgumentException("Unknown spelling")
        };
        var (step, acc) = table[midi % 12];
        return new(step, acc, midi / 12 - 1);
    }

    public Pitch Transpose(int semitones, Spelling spelling = Spelling.Sharp)
    {
        var midi = Midi;
        return semitones == 0 ? this : FromMidi(checked(midi + semitones), spelling);
    }
}

public sealed record Event
{
    public Rational Start { get; }
    public Rational Duration { get; }
    public Pitch Pitch { get; }
    public int Velocity { get; }

    public Event(Rational start, Rational duration, Pitch pitch, int velocity = 80)
    {
        if (duration <= 0) throw new ArgumentException("Event duration must be positive");
        if (velocity is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity));
        Start = start;
        Duration = duration;
        Pitch = pitch;
        Velocity = velocity;
    }

    public Event Shifted(Rational offset) => new(Start + offset, Duration, Pitch, Velocity);
}

public abstract class Music
{
    public abstract Rational Duration { get; }
    public abstract IEnumerable<Event> IterEvents();
    public IReadOnlyList<Event> Events() => [.. IterEvents()];
    public static Sequence operator +(Music left, Music right) => new([left, right]);
    public static Parallel operator |(Music left, Music right) => new([left, right]);
    public Repeat Repeat(int count) => new(this, count);
    public Transpose Transpose(int semitones, Spelling spelling = Spelling.Sharp) => new(this, semitones, spelling);
}

public sealed class Note : Music
{
    public Pitch Pitch { get; }
    public Rational Length { get; }
    public int Velocity { get; }

    public Note(Pitch pitch, Rational length, int velocity = 80)
    {
        if (length <= 0) throw new ArgumentException("Note duration must be positive");
        Pitch = pitch;
        Length = length;
        Velocity = velocity;
    }

    public static Note Create(string pitch, Rational length, int velocity = 80) =>
        new(Pitch.Parse(pitch), length, velocity);

    public override Rational Duration => Length;

    public override IEnumerable<Event> IterEvents()
    {
        yield return new(0, Length, Pitch, Velocity);
    }
}

public sealed class Rest : Music
{
    public Rational Length { get; }

    public Rest(Rational length)
    {
        if (length < 0) throw new ArgumentException("Negative rest duration");
        Length = length;
    }

    public override Rational Duration => Length;

    public override IEnumerable<Event> IterEvents()
    {
        yield break;
    }
}

public sealed class Sequence(IEnumerable<Music> children) : Music
{
    public IReadOnlyList<Music> Children { get; } = Array.AsReadOnly([.. children]);
    public override Rational Duration => Children.Aggregate((Rational)0, (a, c) => a + c.Duration);

    public override IEnumerable<Event> IterEvents()
    {
        Rational offset = 0;
        foreach (var c in Children)
        {
            foreach (var e in c.IterEvents()) yield return e.Shifted(offset);
            offset += c.Duration;
        }
    }
}

public sealed class Parallel(IEnumerable<Music> children) : Music
{
    public IReadOnlyList<Music> Children { get; } = Array.AsReadOnly(children.ToArray());
    public override Rational Duration => Children.Select(c => c.Duration).DefaultIfEmpty(0).Max();

    public override IEnumerable<Event> IterEvents()
    {
        foreach (var c in Children)
        foreach (var e in c.IterEvents())
            yield return e;
    }
}

public sealed class Repeat : Music
{
    public Music Source { get; }
    public int Count { get; }

    public Repeat(Music source, int count)
    {
        if (count < 0) throw new ArgumentException("Negative repeat count");
        Source = source;
        Count = count;
    }

    public override Rational Duration => Source.Duration * Count;

    public override IEnumerable<Event> IterEvents()
    {
        for (int i = 0; i < Count; i++)
            foreach (var e in Source.IterEvents())
                yield return e.Shifted(Source.Duration * i);
    }
}

public sealed class Transpose : Music
{
    public Music Source { get; }
    public int Semitones { get; }
    public Spelling Spelling { get; }

    public Transpose(Music source, int semitones, Spelling spelling = Spelling.Sharp)
    {
        if (!Enum.IsDefined(spelling)) throw new ArgumentException("Unknown spelling");
        Source = source;
        Semitones = semitones;
        Spelling = spelling;
    }

    public override Rational Duration => Source.Duration;

    public override IEnumerable<Event> IterEvents()
    {
        foreach (var e in Source.IterEvents())
            yield return new(e.Start, e.Duration, e.Pitch.Transpose(Semitones, Spelling), e.Velocity);
    }
}