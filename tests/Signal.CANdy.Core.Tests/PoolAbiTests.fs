namespace Signal.CANdy.Core.Tests

open System
open Xunit
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.PoolAbi

module PoolAbiTests =

    let private unwrap result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private protectionPool =
        { Name = "protection_demo_pool"
          Signals =
            [ { Name = "RxValue"
                SemanticId = 1u
                Storage = U16
                Unit = ""
                Direction = Rx
                Min = None
                Max = None
                Default = None
                FreshnessMs = None }
              { Name = "TxValue"
                SemanticId = 2u
                Storage = U16
                Unit = ""
                Direction = Tx
                Min = None
                Max = None
                Default = Some 4660.0
                FreshnessMs = None }
              { Name = "MarkerA5"
                SemanticId = 3u
                Storage = U8
                Unit = ""
                Direction = Tx
                Min = None
                Max = None
                Default = Some 165.0
                FreshnessMs = None } ] }

    [<Fact>]
    let ``Protection pool ABI canonical bytes and hash are frozen`` () =
        let expectedBytes =
            Convert.FromHexString("5343504F4F4C41424900010003000000010000000100000002000000010100000300000000010000")

        Assert.Equal<byte>(expectedBytes, canonicalBytes protectionPool |> unwrap)

        Assert.Equal(
            "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
            compute protectionPool |> unwrap |> format
        )

    [<Fact>]
    let ``Pool ABI freezes all storage and direction byte codes`` () =
        let storages = [ U8; U16; U32; U64; I8; I16; I32; I64; F32; F64 ]

        let contract =
            { Name = "codes"
              Signals =
                storages
                |> List.mapi (fun index storage ->
                    { protectionPool.Signals.Head with
                        Name = sprintf "slot%d" index
                        SemanticId = uint32 (index + 1)
                        Storage = storage
                        Direction = if index % 2 = 0 then Rx else Tx }) }

        let bytes = canonicalBytes contract |> unwrap

        storages
        |> List.iteri (fun index _ ->
            let entry = 16 + index * 8
            Assert.Equal(byte index, bytes.[entry + 4])
            Assert.Equal(byte (index % 2), bytes.[entry + 5]))

    [<Fact>]
    let ``Pool ABI excludes authoring names and numeric policy`` () =
        let changed =
            { protectionPool with
                Name = "renamed_pool"
                Signals =
                    protectionPool.Signals
                    |> List.mapi (fun index signal ->
                        { signal with
                            Name = sprintf "Renamed%d" index
                            Min = Some -100.0
                            Max = Some 10000.0
                            Default = None
                            FreshnessMs = if signal.Direction = Rx then Some 25u else None }) }

        Assert.Equal(compute protectionPool |> unwrap |> format, compute changed |> unwrap |> format)

    [<Fact>]
    let ``Pool ABI includes authored order semantic type direction and exact unit bytes`` () =
        let original = compute protectionPool |> unwrap |> format
        let first = protectionPool.Signals.Head

        let mutations =
            [ { protectionPool with
                  Signals = List.rev protectionPool.Signals }
              { protectionPool with
                  Signals = { first with SemanticId = 99u } :: protectionPool.Signals.Tail }
              { protectionPool with
                  Signals = { first with Storage = U32 } :: protectionPool.Signals.Tail }
              { protectionPool with
                  Signals = { first with Direction = Tx } :: protectionPool.Signals.Tail }
              { protectionPool with
                  Signals = { first with Unit = "degC" } :: protectionPool.Signals.Tail }
              { protectionPool with
                  Signals = { first with Unit = "e\u0301" } :: protectionPool.Signals.Tail }
              { protectionPool with
                  Signals = { first with Unit = "é" } :: protectionPool.Signals.Tail } ]

        mutations
        |> List.map (compute >> unwrap >> format)
        |> List.iter (fun actual ->
            let unchanged = original = actual
            Assert.False(unchanged))

        let decomposed = mutations.[5] |> compute |> unwrap |> format
        let composed = mutations.[6] |> compute |> unwrap |> format
        let normalized = decomposed = composed
        Assert.False(normalized)

    [<Theory>]
    [<InlineData("sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", true)>]
    [<InlineData("SHA256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", false)>]
    [<InlineData("sha256:3CFF36849F7B67CAE1FA24A1EC6711993E1A4E2C477E613F3701FA41E005E947", false)>]
    [<InlineData("sha256:3cff", false)>]
    [<InlineData("md5:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", false)>]
    [<InlineData("3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", false)>]
    let ``Pool ABI text form is exact`` text expected =
        Assert.Equal(expected, parse text |> Result.isOk)

    [<Fact>]
    let ``Pool ABI rejects invalid contracts and oversized UTF8 units`` () =
        let duplicate =
            { protectionPool with
                Signals = protectionPool.Signals @ [ protectionPool.Signals.Head ] }

        canonicalBytes duplicate |> Result.isError |> Assert.True

        let oversized =
            { protectionPool with
                Signals =
                    { protectionPool.Signals.Head with
                        Unit = String('x', 65536) }
                    :: protectionPool.Signals.Tail }

        canonicalBytes oversized |> Result.isError |> Assert.True
