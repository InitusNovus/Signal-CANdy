namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Errors

module ConfigTests =

    /// Helper: write content to a temp file and return its path
    let private createTempFile (content: string) (extension: string) =
        let tempPath = Path.ChangeExtension(Path.GetTempFileName(), extension)
        File.WriteAllText(tempPath, content)
        tempPath

    /// A known-valid default config for testing
    let private validConfig =
        { PhysType = "float"
          PhysMode = "double"
          RangeCheck = false
          Dispatch = "binary_search"
          CrcCounterCheck = false
          MotorolaStartBit = "msb"
          FilePrefix = "sc_" }

    // -------------------------------------------------------
    // Config.validate tests
    // -------------------------------------------------------

    [<Fact>]
    let ``validate succeeds for valid config`` () =
        let result = validate validConfig

        match result with
        | Ok cfg -> cfg |> should equal validConfig
        | Error e -> failwithf "Expected Ok, got: %A" e

    [<Fact>]
    let ``validate succeeds for all valid PhysType and PhysMode combos`` () =
        let combos =
            [ ("float", "double")
              ("float", "float")
              ("fixed", "fixed_double")
              ("fixed", "fixed_float") ]

        for (pt, pm) in combos do
            let cfg =
                { validConfig with
                    PhysType = pt
                    PhysMode = pm }

            match validate cfg with
            | Ok _ -> ()
            | Error e -> failwithf "Expected Ok for (%s, %s), got: %A" pt pm e

    [<Fact>]
    let ``validate rejects invalid PhysType`` () =
        let cfg =
            { validConfig with
                PhysType = "invalid" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "phys_type"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for invalid PhysType"

    [<Fact>]
    let ``validate rejects invalid PhysMode`` () =
        let cfg =
            { validConfig with
                PhysMode = "bad_mode" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "phys_mode"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for invalid PhysMode"

    [<Fact>]
    let ``validate rejects invalid Dispatch`` () =
        let cfg =
            { validConfig with
                Dispatch = "round_robin" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "dispatch"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for invalid Dispatch"

    [<Fact>]
    let ``validate rejects invalid MotorolaStartBit`` () =
        let cfg =
            { validConfig with
                MotorolaStartBit = "middle" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "motorola_start_bit"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for invalid MotorolaStartBit"

    [<Fact>]
    let ``validate rejects invalid FilePrefix starting with digit`` () =
        let cfg =
            { validConfig with
                FilePrefix = "123bad" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "file_prefix"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for invalid FilePrefix"

    [<Fact>]
    let ``validate rejects empty FilePrefix`` () =
        let cfg = { validConfig with FilePrefix = "" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "file_prefix"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for empty FilePrefix"

    [<Fact>]
    let ``validate rejects FilePrefix with spaces`` () =
        let cfg = { validConfig with FilePrefix = "a b" }

        match validate cfg with
        | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "file_prefix"
        | Error e -> failwithf "Expected InvalidValue, got: %A" e
        | Ok _ -> failwith "Expected error for FilePrefix with spaces"

    // -------------------------------------------------------
    // Config.loadFromYaml tests
    // -------------------------------------------------------

    [<Fact>]
    let ``loadFromYaml loads valid YAML with snake_case keys`` () =
        let yaml =
            """
phys_type: fixed
phys_mode: fixed_float
range_check: true
dispatch: direct_map
crc_counter_check: false
motorola_start_bit: lsb
file_prefix: fw_
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Ok cfg ->
                cfg.PhysType |> should equal "fixed"
                cfg.PhysMode |> should equal "fixed_float"
                cfg.RangeCheck |> should equal true
                cfg.Dispatch |> should equal "direct_map"
                cfg.CrcCounterCheck |> should equal false
                cfg.MotorolaStartBit |> should equal "lsb"
                cfg.FilePrefix |> should equal "fw_"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml loads valid YAML with PascalCase keys`` () =
        let yaml =
            """
PhysType: fixed
PhysMode: fixed_double
RangeCheck: true
Dispatch: direct_map
CrcCounterCheck: true
MotorolaStartBit: msb
FilePrefix: my_
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Ok cfg ->
                cfg.PhysType |> should equal "fixed"
                cfg.PhysMode |> should equal "fixed_double"
                cfg.RangeCheck |> should equal true
                cfg.Dispatch |> should equal "direct_map"
                cfg.CrcCounterCheck |> should equal true
                cfg.MotorolaStartBit |> should equal "msb"
                cfg.FilePrefix |> should equal "my_"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml infers PhysMode double for PhysType float`` () =
        let yaml =
            """
phys_type: float
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Ok cfg ->
                cfg.PhysType |> should equal "float"
                cfg.PhysMode |> should equal "double"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml infers PhysMode fixed_double for PhysType fixed`` () =
        let yaml =
            """
phys_type: fixed
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Ok cfg ->
                cfg.PhysType |> should equal "fixed"
                cfg.PhysMode |> should equal "fixed_double"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml returns all defaults for empty map YAML`` () =
        // Use an explicit empty map "{}" so YamlDotNet returns an empty dictionary (not null)
        let yaml = "{}"
        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Ok cfg ->
                cfg.PhysType |> should equal "float"
                cfg.PhysMode |> should equal "double"
                cfg.RangeCheck |> should equal false
                cfg.Dispatch |> should equal "binary_search"
                cfg.CrcCounterCheck |> should equal false
                cfg.MotorolaStartBit |> should equal "msb"
                cfg.FilePrefix |> should equal "sc_"
            | Error e -> failwithf "Expected Ok for empty map YAML, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml returns IoError for comment-only YAML`` () =
        // A YAML file with only comments deserializes to null, causing an NRE caught as IoError
        let yaml =
            """
# empty config with only comments
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Error(ValidationError.IoError _) -> () // expected: null dictionary → NRE → IoError
            | Error e -> failwithf "Expected IoError for comment-only YAML, got: %A" e
            | Ok _ -> failwith "Expected IoError for comment-only YAML, got Ok"
        finally
            File.Delete(path)

    [<Fact>]
    let ``loadFromYaml returns IoError for non-existent path`` () =
        match loadFromYaml "nonexistent_config_file.yaml" with
        | Error(ValidationError.IoError _) -> () // expected
        | Error e -> failwithf "Expected IoError, got: %A" e
        | Ok _ -> failwith "Expected IoError for non-existent file"

    [<Fact>]
    let ``loadFromYaml returns validation error for invalid value in YAML`` () =
        let yaml =
            """
phys_type: BOGUS
"""

        let path = createTempFile yaml ".yaml"

        try
            match loadFromYaml path with
            | Error(ValidationError.InvalidValue msg) -> msg |> should haveSubstring "phys_type"
            | Error e -> failwithf "Expected InvalidValue, got: %A" e
            | Ok _ -> failwith "Expected validation error for invalid YAML value"
        finally
            File.Delete(path)
