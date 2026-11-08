namespace Sonqelra.Core;

public abstract record TrackControl(Rational Start);

public sealed record ControlChange : TrackControl
{
    public int Control { get; }
    public int Value { get; }

    public ControlChange(Rational start, int control, int value) : base(start)
    {
        if (start < 0 || control is < 0 or > 127 || value is < 0 or > 127)
            throw new ArgumentException("Invalid MIDI control change");
        Control = control;
        Value = value;
    }
}

public sealed record PitchBend : TrackControl
{
    public int Pitch { get; }

    public PitchBend(Rational start, int pitch) : base(start)
    {
        if (start < 0 || pitch is < -8192 or > 8191) throw new ArgumentException("Invalid MIDI pitch bend");
        Pitch = pitch;
    }
}

public sealed class Track
{
    public Music Music { get; }
    public string Instrument { get; }
    public string Name { get; }
    public bool IsPercussion { get; }
    public IReadOnlyList<TrackControl> Controls { get; }

    public Track(Music music, string instrument, string name, bool isPercussion = false,
        IEnumerable<TrackControl>? controls = null)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Track name must not be empty");
        if (string.IsNullOrEmpty(instrument)) throw new ArgumentException("Track instrument must not be empty");
        Music = music;
        Instrument = instrument;
        Name = name;
        IsPercussion = isPercussion;
        Controls = Array.AsReadOnly([.. controls ?? []]);
    }

    public int InstrumentToMidiProgram() => Instruments.ProgramFor(Instrument);
}

public sealed record TimeSignature
{
    public int Numerator { get; }
    public int Denominator { get; }

    public TimeSignature(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0 || (denominator & (denominator - 1)) != 0)
            throw new ArgumentException("Invalid time signature");
        Numerator = numerator;
        Denominator = denominator;
    }
}

internal static class ChangeMapValidation
{
    public static void Validate(IEnumerable<Rational> positions)
    {
        var a = positions.ToArray();
        if (a.Length == 0 || a[0] != 0) throw new ArgumentException("Change map must begin at position 0");
        for (int i = 1; i < a.Length; i++)
            if (a[i] <= a[i - 1])
                throw new ArgumentException("Change positions must be strictly increasing");
    }
}

public sealed class TempoMap
{
    public IReadOnlyList<(Rational Start, Rational Bpm)> Tempo { get; }

    public TempoMap(IEnumerable<(Rational Start, Rational Bpm)> tempo)
    {
        var a = tempo.ToArray();
        ChangeMapValidation.Validate(a.Select(x => x.Start));
        if (a.Any(x => x.Bpm <= 0)) throw new ArgumentException("Tempo must be positive");
        Tempo = Array.AsReadOnly(a);
    }
}

public sealed class MeterMap
{
    public IReadOnlyList<(Rational Start, TimeSignature Signature)> Meter { get; }

    public MeterMap(IEnumerable<(Rational Start, TimeSignature Signature)> meter)
    {
        var a = meter.ToArray();
        ChangeMapValidation.Validate(a.Select(x => x.Start));
        Meter = Array.AsReadOnly(a);
    }
}

public sealed class Score
{
    public IReadOnlyList<Track> Tracks { get; }
    public string Title { get; }
    public TempoMap TempoMap { get; }
    public MeterMap MeterMap { get; }

    public Score(IEnumerable<Track> tracks, string title, TempoMap? tempoMap = null, MeterMap? meterMap = null)
    {
        var a = tracks.ToArray();
        if (a.Length == 0) throw new ArgumentException("Score must have at least one track");
        Tracks = Array.AsReadOnly(a);
        Title = title;
        TempoMap = tempoMap ?? new([(0, 120)]);
        MeterMap = meterMap ?? new([(0, new TimeSignature(4, 4))]);
    }
}