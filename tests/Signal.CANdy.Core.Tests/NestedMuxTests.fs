namespace Signal.CANdy.Core.Tests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Codegen
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Dbc
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Core.Wire

module NestedMuxTests =

    let private withDbc content assertion =
        let path = Path.ChangeExtension(Path.GetTempFileName(), ".dbc")
        File.WriteAllText(path, content)

        try
            assertion path
        finally
            File.Delete(path)

    let private dbc signals declarations =
        sprintf
            """
VERSION ""
NS_ :
 SG_MUL_VAL_
BS_:

BO_ 804 NESTED: 8 Vector__XXX
%s
%s
"""
            signals
            declarations

    let private twoLevelDbc =
        dbc
            """ SG_ Outer M : 0|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ Inner m1M : 2|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ Leaf m2 : 16|8@1+ (1,0) [0|255] "" Vector__XXX"""
            """SG_MUL_VAL_ 804 Inner Outer 1-1;
SG_MUL_VAL_ 804 Leaf Inner 2-2;"""

    let private parse path =
        match parseDbcFile path with
        | Ok ir -> ir
        | Error error -> failwithf "Expected nested DBC to parse, got %A" error

    let private expectInvalid content =
        withDbc content (fun path ->
            match parseDbcFile path with
            | Error(ParseError.InvalidDbc _) -> ()
            | Error error -> failwithf "Expected InvalidDbc, got %A" error
            | Ok _ -> failwith "Expected nested mux DBC rejection")

    [<Fact>]
    let ``NestedMux DBC SG_MUL_VAL builds canonical two level path`` () =
        withDbc twoLevelDbc (fun path ->
            let ir = parse path
            let message = ir.Messages |> List.exactlyOne
            let inner = message.Signals |> List.find (fun signal -> signal.Name = "Inner")
            let leaf = message.Signals |> List.find (fun signal -> signal.Name = "Leaf")

            inner.ExtendedMuxParent.Value.SelectorSignalName |> should equal "Outer"
            inner.ExtendedMuxParent.Value.Expected |> should equal 1u
            leaf.ExtendedMuxParent.Value.SelectorSignalName |> should equal "Inner"
            leaf.ExtendedMuxParent.Value.Expected |> should equal 2u

            match toWireModel ir with
            | Error errors -> failwithf "Expected canonical Wire IR, got %A" errors
            | Ok wire ->
                let signals = wire.Messages.Head.Signals
                let outer = signals |> List.find (fun signal -> signal.Name = "Outer")
                let canonicalInner = signals |> List.find (fun signal -> signal.Name = "Inner")
                let canonicalLeaf = signals |> List.find (fun signal -> signal.Name = "Leaf")

                outer.IsMuxSelector |> should equal true
                outer.MuxPath |> should be Empty
                canonicalInner.IsMuxSelector |> should equal true

                canonicalLeaf.MuxPath
                |> List.map (fun predicate -> predicate.SelectorSignalName, predicate.Expected)
                |> should equal [ "Outer", 1u; "Inner", 2u ])

    [<Fact>]
    let ``NestedMux canonical path accepts maximum depth four`` () =
        let content =
            dbc
                """ SG_ S0 M : 0|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S1 m1M : 2|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S2 m2M : 4|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S3 m3M : 6|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ Leaf m1 : 16|8@1+ (1,0) [0|255] "" Vector__XXX"""
                """SG_MUL_VAL_ 804 S1 S0 1-1;
SG_MUL_VAL_ 804 S2 S1 2-2;
SG_MUL_VAL_ 804 S3 S2 3-3;
SG_MUL_VAL_ 804 Leaf S3 1-1;"""

        withDbc content (fun path ->
            match parse path |> toWireModel with
            | Error errors -> failwithf "Expected depth-four path, got %A" errors
            | Ok wire ->
                let leaf =
                    wire.Messages.Head.Signals |> List.find (fun signal -> signal.Name = "Leaf")

                leaf.MuxPath.Length |> should equal 4)

    [<Fact>]
    let ``NestedMux rejects unequal SG_MUL_VAL range`` () =
        twoLevelDbc.Replace("Leaf Inner 2-2", "Leaf Inner 1-2") |> expectInvalid

    [<Fact>]
    let ``NestedMux rejects SG_MUL_VAL list`` () =
        twoLevelDbc.Replace("Leaf Inner 2-2", "Leaf Inner 1-1, 2-2") |> expectInvalid

    [<Fact>]
    let ``NestedMux rejects missing parent`` () =
        twoLevelDbc.Replace("Leaf Inner 2-2", "Leaf Missing 2-2") |> expectInvalid

    [<Fact>]
    let ``NestedMux rejects selector cycle`` () =
        let content = twoLevelDbc.Replace("Inner Outer 1-1", "Inner Leaf 1-1")

        expectInvalid content

    [<Fact>]
    let ``NestedMux rejects self parent`` () =
        twoLevelDbc.Replace("Leaf Inner 2-2", "Leaf Leaf 2-2") |> expectInvalid

    [<Fact>]
    let ``NestedMux rejects depth five`` () =
        let content =
            dbc
                """ SG_ S0 M : 0|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S1 m1M : 2|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S2 m2M : 4|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S3 m3M : 6|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ S4 m1M : 8|2@1+ (1,0) [0|3] "" Vector__XXX
 SG_ Leaf m2 : 16|8@1+ (1,0) [0|255] "" Vector__XXX"""
                """SG_MUL_VAL_ 804 S1 S0 1-1;
SG_MUL_VAL_ 804 S2 S1 2-2;
SG_MUL_VAL_ 804 S3 S2 3-3;
SG_MUL_VAL_ 804 S4 S3 1-1;
SG_MUL_VAL_ 804 Leaf S4 2-2;"""

        expectInvalid content

    let private poolSignal name semanticId freshness direction =
        { Name = name
          SemanticId = semanticId
          Storage = U8
          Unit = ""
          Direction = direction
          Min = None
          Max = None
          Default = None
          FreshnessMs = freshness }

    let private wireSignal name startBit selector path =
        { Name = name
          StartBit = startBit
          LengthBits = 2us
          ByteOrder = Little
          IsSigned = false
          Factor = 1.0
          Offset = 0.0
          Unit = ""
          Min = None
          Max = None
          IsMuxSelector = selector
          MuxPath = path
          Receivers = [] }

    let private predicate selector expected : MuxPredicate =
        { SelectorSignalName = selector
          Expected = expected }

    [<Fact>]
    let ``NestedMux linker resolves full selector paths and freshness`` () =
        let pool =
            { Name = "NestedPool"
              Signals =
                [ poolSignal "Outer" 1u None Rx
                  poolSignal "Inner" 2u None Rx
                  poolSignal "Leaf" 3u (Some 200u) Rx ] }

        let wire =
            { Messages =
                [ { Name = "NestedFrame"
                    CanId = 0x324u
                    IsExtended = false
                    LengthBytes = 8us
                    Signals =
                      [ wireSignal "Outer" 0us true []
                        wireSignal "Inner" 2us true [ predicate "Outer" 1u ]
                        wireSignal "Leaf" 16us false [ predicate "Outer" 1u; predicate "Inner" 2u ] ] } ] }

        let bindings =
            { Bindings =
                [ for name in [ "Outer"; "Inner"; "Leaf" ] do
                      { PoolSignalName = name
                        MessageName = "NestedFrame"
                        WireSignalName = name
                        Conversion = Identity } ]
              TxMessages = [] }

        match link pool wire bindings with
        | Error errors -> failwithf "Expected nested schema to link, got %A" errors
        | Ok schema ->
            schema.PoolSlots.[2].FreshnessMs |> should equal (Some 200u)

            let leaf =
                schema.Messages.Head.Plans
                |> List.find (fun plan -> plan.WireSignalName = "Leaf")

            leaf.MuxPath
            |> List.map (fun predicate -> predicate.SelectorProgramName, predicate.SelectorSlot, predicate.Expected)
            |> should equal [ "Outer", 0us, 1u; "Inner", 1us, 2u ]

            match lower schema with
            | Error errors -> failwithf "Expected nested quality lowering, got %A" errors
            | Ok image ->
                image.QualityEntries.[2].FreshnessMs |> should equal 200u
                image.NestedMuxRecords.Head.TargetProgramIndex |> should equal 2us

                image.NestedMuxRecords.Head.Predicates
                |> List.map (fun predicate ->
                    predicate.SelectorProgramIndex, predicate.SelectorSlot, predicate.Expected)
                |> should equal [ 0us, 0us, 1u; 1us, 1us, 2u ]

    [<Fact>]
    let ``NestedMux AOT generator rejects extended mux instead of flattening it`` () =
        withDbc twoLevelDbc (fun path ->
            let ir = parse path
            let output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))

            let config: Config =
                { PhysType = "float"
                  PhysMode = "double"
                  RangeCheck = false
                  Dispatch = "binary_search"
                  CrcCounterCheck = false
                  MotorolaStartBit = "msb"
                  FilePrefix = "sc_"
                  CrcCounter = None }

            try
                match generate ir output config with
                | Error(CodeGenError.UnsupportedFeature details) ->
                    details.ToLowerInvariant() |> should haveSubstring "nested"
                | Error error -> failwithf "Expected UnsupportedFeature, got %A" error
                | Ok _ -> failwith "AOT must not silently flatten nested mux paths"
            finally
                if Directory.Exists(output) then
                    Directory.Delete(output, true))
