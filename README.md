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
MusicXml.Export(score, "output.musicxml");
```

Times are exact `Rational` values in quarter notes. `Music.IterEvents()` enumerates events lazily. Custom export formats can be registered (or built-in formats overridden) by passing a dictionary of `Action<Score, string>` to `Exports.Execute`.

The MIDI backend supports type 0 and 1 files with quarter-note time division; export uses 480 ticks per quarter note and rejects timing it cannot represent exactly. MusicXML meter changes must occur at measure boundaries. Exporting multiple files is not transactional.
