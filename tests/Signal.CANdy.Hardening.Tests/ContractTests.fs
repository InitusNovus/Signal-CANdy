namespace Signal.CANdy.Hardening.Tests

open System
open Xunit
open Signal.CANdy.Hardening

module ContractTests =

    [<Fact>]
    let ``SplitMix64 and independent derivation are deterministic`` () =
        Assert.Equal(0x5343494D47323501UL, Contract.RootSeed)

        let firstState, first = SplitMix64.next Contract.RootSeed
        let secondState, second = SplitMix64.next firstState

        Assert.Equal(0xF17AC306C67CB116UL, firstState)
        Assert.Equal(0x01CA6CB6E72C06C6UL, first)
        Assert.Equal(0x8FB23CC045C72D2BUL, secondState)
        Assert.Equal(0x2DC200B039CD8B78UL, second)

        let independently = Contract.deriveSeed 3 417
        Assert.Equal(independently, Contract.deriveSeed 3 417)
        Assert.NotEqual(independently, Contract.deriveSeed 3 418)
        Assert.NotEqual(independently, Contract.deriveSeed 4 417)

    [<Fact>]
    let ``six pinned bases allocate exactly ten thousand cases`` () =
        let expected =
            [ "legacy-rx", 2000, 500, 250, 1250, 376, "d25dc336c2eb44b39873c2cfa45f8cca00fce54558ea793840f682fd0414726b"
              "tx", 2000, 500, 250, 1250, 432, "681fa350bf5fc1ac4c248ac7ec8bbb1d0962a958d774c8b8ae04abfe723ba013"
              "rxq-nested",
              2000,
              500,
              250,
              1250,
              372,
              "1e5f2348ce5474a33a8eda4aa8a7a101a7bafe55450a219ec73a3b75f05f767f"
              "protection",
              2000,
              500,
              250,
              1250,
              428,
              "26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df"
              "activation-a",
              1000,
              250,
              125,
              625,
              444,
              "9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e"
              "activation-b",
              1000,
              250,
              125,
              625,
              444,
              "6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2" ]

        Assert.Equal(6, Contract.bases.Length)
        Assert.Equal(10000, Contract.bases |> List.sumBy _.Cases)

        Assert.Equal<(string * int * int * int * int * int * string) list>(
            expected,
            Contract.bases
            |> List.map (fun value ->
                value.Id,
                value.Cases,
                value.FieldCases,
                value.StructuralCases,
                value.BoundedCases,
                value.Bytes,
                value.Sha256)
        )

    [<Fact>]
    let ``case plans have stable IDs direct replay and exact class totals`` () =
        Assert.Equal(10000, Contract.cases.Length)
        Assert.Equal(10000, Contract.cases |> List.map _.Id |> Set.ofList |> Set.count)

        for plan in Contract.cases do
            Assert.Matches("^[a-z0-9-]+/(field|structural|bounded)/[^/]+/[0-9]{4}/[0-9a-f]{16}$", plan.Id)

            Assert.Equal(Some plan, Contract.replay plan.Id)
            Assert.Equal(plan, Contract.caseAt plan.Base plan.Ordinal)

        let totals mutationClass =
            Contract.cases
            |> List.filter (fun plan -> plan.Class = mutationClass)
            |> List.length

        Assert.Equal(2500, totals MutationClass.Field)
        Assert.Equal(1250, totals MutationClass.Structural)
        Assert.Equal(6250, totals MutationClass.Bounded)

    [<Fact>]
    let ``bounded mutation families have the fixed 625 and 1250 mixes`` () =
        let expected625 =
            Map
                [ "single-bit-flip", 125
                  "byte-replacement", 125
                  "range-xor", 100
                  "range-fill", 75
                  "insertion", 75
                  "removal", 75
                  "coherent-multi-field", 50 ]

        Assert.Equal<Map<string, int>>(expected625, Map Contract.boundedMix625)

        for baseSpec in Contract.bases do
            let actual =
                Contract.cases
                |> List.filter (fun plan -> plan.Base.Id = baseSpec.Id && plan.Class = MutationClass.Bounded)
                |> List.countBy _.Target
                |> Map

            let scale = baseSpec.BoundedCases / 625
            Assert.Equal<Map<string, int>>(expected625 |> Map.map (fun _ count -> count * scale), actual)

    [<Fact>]
    let ``field catalog and minimizer retain every fixed family`` () =
        let paths = Contract.fieldCatalog |> List.map _.Path
        Assert.Equal<string list>(List.sort paths, paths)
        Assert.Equal(paths.Length, paths |> Set.ofList |> Set.count)

        let required =
            [ "header.magic"
              "header.reserved[0]"
              "directory.msg.offset"
              "directory.sym.size"
              "msg.canId"
              "prg.muxValue"
              "cnv.reserved[6]"
              "cnv.factor"
              "sym.name.malformedUtf8"
              "sym.finalPadding"
              "ex01.end"
              "nmx.predicate[3].value"
              "quality.freshnessMs"
              "pr01.reserved[15]"
              "protectionPlan.dataId"
              "protectionPlan.reserved[1]"
              "rxCounter.reserved[2]"
              "coverageSpan.reserved[1]"
              "tx01.templateSize"
              "txMessage.templateOffset"
              "txp.muxValue"
              "txCounter.initialValue"
              "txTemplate.bytes"
              "footer.crc32" ]

        for path in required do
            Assert.Contains(path, paths)

        Assert.Equal<MinimizePhase list>(
            [ MinimizePhase.Regions
              MinimizePhase.Ranges
              MinimizePhase.Bytes
              MinimizePhase.FieldValues ],
            Contract.minimizationOrder
        )

        Assert.Equal<string list>(
            [ "zero"
              "one"
              "valid-maximum"
              "boundary-plus-one"
              "all-bits-set"
              "sentinel-alternative"
              "actual-minus-one"
              "actual-plus-one" ],
            Contract.fieldValueFamilies
        )

    [<Fact>]
    let ``parser boundary catalog pins equality and plus one`` () =
        Assert.Equal(18, Contract.parserBoundaries.Length)

        for name, equality, plusOne in Contract.parserBoundaries do
            Assert.False(String.IsNullOrWhiteSpace(name))
            Assert.Equal(equality + 1L, plusOne)
