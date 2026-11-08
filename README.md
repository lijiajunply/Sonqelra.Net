# Sonqelra for .NET

C# implementation of the Sonqelra music model, language, and MIDI/MusicXML backends. Requires .NET 10.

```sh
dotnet build Sonqelra.slnx
dotnet run --project Sonqelra.Tests
dotnet run --project Sonqelra.ConsoleApp -- path/to/piece.sonqelra
```

The CLI resolves relative export paths against the source file directory. Compilation alone performs no file writes.

```csharp
using Sonqelra.Lang;
using Sonqelra.Backends;

var result = Compiler.CompileSource(source, "piece.sonqelra");
var score = result.Scores[0].Score;
Exports.Execute(result, baseDirectory);
Midi.Import("input.mid");
MusicXml.Import("input.musicxml");
MusicXml.Export(score, "output.musicxml");
```

Times are exact `Rational` values in quarter notes. `Music.IterEvents()` enumerates events lazily. Custom export formats can be registered (or built-in formats overridden) by passing a dictionary of `Action<Score, string>` to `Exports.Execute`.

The MIDI backend supports type 0 and 1 files with quarter-note time division; export uses 480 ticks per quarter note and rejects timing it cannot represent exactly. MusicXML meter changes must occur at measure boundaries. Exporting multiple files is not transactional.

## Import syntax

Import files at the top level, then select a function or track where needed:

```sonqelra
import sonqelra "parts/guitar.sonqelra"
import midi "melody.mid"

score "Combined" {
    track "Guitar" {
        instrument "Distortion Guitar"
        play module("guitar")."Electric Road".fn.power_chord(E3, B3)
    }
    track "Melody" {
        instrument "Piano"
        play module("melody")."melody".track."Original track name"
    }
}
```

Module names are filenames without extensions. Scores and tracks are selected by their original names. MIDI uses the filename as its score title; MusicXML uses its title or the filename if absent. MIDI and MusicXML contain no Sonqelra functions. Selecting a track returns its `Music`, without its instrument or controls; specify the instrument on the destination track.

A later score can declare `import score "Title"` and use `score("Title").fn.function(...)` or `score("Title").track."Track"` to access an earlier score in the same file. Imports register references and never automatically add tracks. Script functions retain their declaration closures. Files are read at compile time, but imported export plans are not executed. Relative paths are resolved against the source file containing the import; pass `filename` to `Compiler.CompileSource` for relative imports. Nested imports are supported, cycles and duplicate module or score names are rejected.

MusicXML import accepts uncompressed `score-partwise` documents, including pitched notes, rests, chords, multiple voices, ties and MIDI instruments. Note velocity defaults to 80. Grace notes, microtones and unpitched percussion notes are unsupported; repeat markings, lyrics and other notation are not converted.
