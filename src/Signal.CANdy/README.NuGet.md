# Signal.CANdy

C#-friendly facade over Signal.CANdy Core. Wraps Result-based F# API with exceptions and .NET-friendly types.

- Repo: https://github.com/InitusNovus/Signal-CANdy
- License: MIT

## Install

```
dotnet add package Signal.CANdy --version 0.2.1-preview.0
```

## Quick start (C#)

```csharp
using System.Threading.Tasks;
using Signal.CANdy;

class Demo
{
    static async Task Main()
    {
        var facade = new GeneratorFacade();
        var files = await facade.GenerateFromPathsAsync(
            dbcPath: "examples/sample.dbc",
            outputPath: "gen",
            configPath: null
        );
    System.Console.WriteLine($"Headers: {files.Headers.Count}, Sources: {files.Sources.Count}, Others: {files.Others.Count}");
    }
}
```

## What's inside
- Exceptions for validation/parse/codegen errors
- Simple async path-based API
- Access to Core IR and fine-grained APIs if needed
