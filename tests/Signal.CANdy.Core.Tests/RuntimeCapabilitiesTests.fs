namespace Signal.CANdy.Core.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.RuntimeCapabilities

module RuntimeCapabilitiesTests =

    let private limitNames =
        [ "maxImageBytes"
          "maxRuntimeStateBytes"
          "maxRuntimeScratchBytes"
          "maxRxMessages"
          "maxRxPrograms"
          "maxTxMessages"
          "maxTxPrograms"
          "maxPoolSlots"
          "maxConversions"
          "maxNestedMuxRecords"
          "maxMuxDepth"
          "maxQualityEntries"
          "maxProtectionPlans"
          "maxTxCounters"
          "maxRxCounters"
          "maxCoverageSpans"
          "maxTxTemplateBytes"
          "maxPayloadBytes" ]

    let private validJson =
        """{
  "format": "sc.runtime-capabilities/v1",
  "runtimeImageMajor": 1,
  "runtimeImageMinor": 0,
  "runtimeAbi": "ilp32",
  "features": ["rx", "tx"],
  "poolAbiHash": "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
  "limits": {
    "maxImageBytes": 428,
    "maxRuntimeStateBytes": 28,
    "maxRuntimeScratchBytes": 8,
    "maxRxMessages": 1,
    "maxRxPrograms": 1,
    "maxTxMessages": 1,
    "maxTxPrograms": 2,
    "maxPoolSlots": 3,
    "maxConversions": 1,
    "maxNestedMuxRecords": 0,
    "maxMuxDepth": 0,
    "maxQualityEntries": 0,
    "maxProtectionPlans": 2,
    "maxTxCounters": 1,
    "maxRxCounters": 1,
    "maxCoverageSpans": 2,
    "maxTxTemplateBytes": 8,
    "maxPayloadBytes": 8
  }
}
"""

    let private unwrap result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private withoutRootKey key =
        let root = JsonNode.Parse(validJson).AsObject()
        root.Remove(key) |> ignore
        root.ToJsonString()

    let private withoutLimitKey key =
        let root = JsonNode.Parse(validJson).AsObject()
        root.["limits"].AsObject().Remove(key) |> ignore
        root.ToJsonString()

    [<Fact>]
    let ``Capability parser accepts the exact v1 object and optional hash omission`` () =
        let parsed = parse validJson |> unwrap
        Assert.Equal(1us, parsed.RuntimeImageMajor)
        Assert.Equal(0us, parsed.RuntimeImageMinor)
        Assert.Equal(Ilp32, parsed.RuntimeAbi)
        Assert.True(set [ Rx; Tx ] = parsed.Features)
        Assert.True(parsed.PoolAbiHash.IsSome)

        validJson.Replace(
            "  \"poolAbiHash\": \"sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947\",\n",
            ""
        )
        |> parse
        |> unwrap
        |> fun capability -> Assert.True(capability.PoolAbiHash.IsNone)

    [<Fact>]
    let ``Capability parser rejects every missing required root and limit key`` () =
        [ "format"
          "runtimeImageMajor"
          "runtimeImageMinor"
          "runtimeAbi"
          "features"
          "limits" ]
        |> List.iter (fun key -> Assert.True(parse (withoutRootKey key) |> Result.isError, sprintf "root key %s" key))

        limitNames
        |> List.iter (fun key -> Assert.True(parse (withoutLimitKey key) |> Result.isError, sprintf "limit key %s" key))

    [<Fact>]
    let ``Capability parser rejects unknown and duplicate keys at both levels`` () =
        let unknownRoot =
            validJson.Replace("  \"format\"", "  \"future\": 1,\n  \"format\"")

        let duplicateRoot =
            validJson.Replace("  \"format\"", "  \"format\": \"sc.runtime-capabilities/v1\",\n  \"format\"")

        let unknownLimit =
            validJson.Replace("    \"maxImageBytes\"", "    \"maxFuture\": 1,\n    \"maxImageBytes\"")

        let duplicateLimit =
            validJson.Replace("    \"maxImageBytes\"", "    \"maxImageBytes\": 428,\n    \"maxImageBytes\"")

        [ unknownRoot; duplicateRoot; unknownLimit; duplicateLimit ]
        |> List.iter (fun json -> parse json |> Result.isError |> Assert.True)

    [<Theory>]
    [<InlineData("0", true)>]
    [<InlineData("65535", true)>]
    [<InlineData("-1", false)>]
    [<InlineData("65536", false)>]
    [<InlineData("1.0", false)>]
    [<InlineData("1e0", false)>]
    [<InlineData("\"1\"", false)>]
    [<InlineData("null", false)>]
    let ``Image versions require lexical uint16 integers`` replacement expected =
        Assert.Equal(
            expected,
            validJson.Replace("\"runtimeImageMajor\": 1", "\"runtimeImageMajor\": " + replacement)
            |> parse
            |> Result.isOk
        )

    [<Theory>]
    [<InlineData("0", true)>]
    [<InlineData("4294967295", true)>]
    [<InlineData("-1", false)>]
    [<InlineData("4294967296", false)>]
    [<InlineData("1.0", false)>]
    [<InlineData("1e0", false)>]
    [<InlineData("\"1\"", false)>]
    [<InlineData("null", false)>]
    let ``Limits require lexical uint32 integers`` replacement expected =
        Assert.Equal(
            expected,
            validJson.Replace("\"maxImageBytes\": 428", "\"maxImageBytes\": " + replacement)
            |> parse
            |> Result.isOk
        )

    [<Fact>]
    let ``Capability parser rejects wrong root shapes comments and trailing commas`` () =
        let wrongShapes =
            [ "[]"
              "null"
              validJson.Replace("\"format\": \"sc.runtime-capabilities/v1\"", "\"format\": 1")
              validJson.Replace("\"runtimeAbi\": \"ilp32\"", "\"runtimeAbi\": 1")
              validJson.Replace("\"features\": [\"rx\", \"tx\"]", "\"features\": \"rx\"")
              validJson.Replace(
                  "\"poolAbiHash\": \"sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947\"",
                  "\"poolAbiHash\": null"
              )
              validJson.Replace("\"limits\": {", "\"limits\": [")
              validJson.Replace("  }\n}", "  ]\n}")
              validJson.Replace("{\n", "{\n  // forbidden\n")
              validJson.Replace("  }\n}", "  },\n}") ]

        wrongShapes
        |> List.iter (fun json -> parse json |> Result.isError |> Assert.True)

    [<Theory>]
    [<InlineData("sc.runtime-capabilities/v1", true)>]
    [<InlineData("sc.runtime-capabilities/v2", false)>]
    [<InlineData("SC.RUNTIME-CAPABILITIES/V1", false)>]
    [<InlineData("", false)>]
    let ``Capability format token is exact`` token expected =
        Assert.Equal(expected, validJson.Replace("sc.runtime-capabilities/v1", token) |> parse |> Result.isOk)

    [<Theory>]
    [<InlineData("[\"rx\", \"tx\"]", true)>]
    [<InlineData("[\"tx\", \"rx\"]", true)>]
    [<InlineData("[\"rx\", \"rx\"]", false)>]
    [<InlineData("[\"rx\", \"future\"]", false)>]
    [<InlineData("[\"RX\"]", false)>]
    [<InlineData("[null]", false)>]
    [<InlineData("[1]", false)>]
    let ``Feature tokens are known unique strings`` features expected =
        Assert.Equal(expected, validJson.Replace("[\"rx\", \"tx\"]", features) |> parse |> Result.isOk)

    [<Theory>]
    [<InlineData("sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", true)>]
    [<InlineData("sha256:3CFF36849F7B67CAE1FA24A1EC6711993E1A4E2C477E613F3701FA41E005E947", false)>]
    [<InlineData("sha256:3cff", false)>]
    [<InlineData("sha512:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", false)>]
    let ``Capability pool hash is strict lowercase SHA256`` hash expected =
        Assert.Equal(
            expected,
            validJson.Replace("sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947", hash)
            |> parse
            |> Result.isOk
        )

    [<Fact>]
    let ``All v1 feature tokens parse and write in canonical order`` () =
        let tokens =
            [ "rx"
              "tx"
              "multiplexing"
              "nested-mux"
              "rx-quality"
              "can-fd"
              "extended-can"
              "motorola"
              "affine"
              "crc8-sae-j1850"
              "crc16-ccitt-false"
              "crc-data-id"
              "rx-counter"
              "tx-counter" ]

        let featureJson =
            tokens
            |> List.rev
            |> List.map (sprintf "\"%s\"")
            |> String.concat ", "
            |> sprintf "[%s]"

        let canonical =
            validJson.Replace("[\"rx\", \"tx\"]", featureJson)
            |> parse
            |> unwrap
            |> writeCanonical
            |> unwrap

        let positions =
            tokens
            |> List.map (fun token -> canonical.IndexOf(sprintf "\"%s\"" token, StringComparison.Ordinal))

        Assert.Equal(tokens.Length, positions |> List.filter (fun index -> index >= 0) |> List.length)
        let ordered = List.sort positions = positions
        Assert.True(ordered)

    [<Fact>]
    let ``Nested mux capability requires multiplexing and RX quality`` () =
        validJson.Replace("[\"rx\", \"tx\"]", "[\"nested-mux\"]")
        |> parse
        |> Result.isError
        |> Assert.True

        validJson.Replace("[\"rx\", \"tx\"]", "[\"rx-quality\", \"nested-mux\", \"multiplexing\"]")
        |> parse
        |> Result.isOk
        |> Assert.True

    [<Fact>]
    let ``Canonical capability writer freezes root limit feature and LF order`` () =
        let hash =
            Signal.CANdy.Core.PoolAbi.parse "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947"
            |> unwrap

        let limits =
            { MaxImageBytes = 428u
              MaxRuntimeStateBytes = 28u
              MaxRuntimeScratchBytes = 8u
              MaxRxMessages = 1u
              MaxRxPrograms = 1u
              MaxTxMessages = 1u
              MaxTxPrograms = 2u
              MaxPoolSlots = 3u
              MaxConversions = 1u
              MaxNestedMuxRecords = 0u
              MaxMuxDepth = 0u
              MaxQualityEntries = 0u
              MaxProtectionPlans = 2u
              MaxTxCounters = 1u
              MaxRxCounters = 1u
              MaxCoverageSpans = 2u
              MaxTxTemplateBytes = 8u
              MaxPayloadBytes = 8u }

        let capability =
            { RuntimeImageMajor = 1us
              RuntimeImageMinor = 0us
              RuntimeAbi = Ilp32
              Features = set [ TxCounter; Crc16CcittFalse; Rx; Crc8SaeJ1850; Tx; RxCounter ]
              PoolAbiHash = Some hash
              Limits = limits }

        let expected =
            """{
  "format": "sc.runtime-capabilities/v1",
  "runtimeImageMajor": 1,
  "runtimeImageMinor": 0,
  "runtimeAbi": "ilp32",
  "features": [
    "rx",
    "tx",
    "crc8-sae-j1850",
    "crc16-ccitt-false",
    "rx-counter",
    "tx-counter"
  ],
  "poolAbiHash": "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
  "limits": {
    "maxImageBytes": 428,
    "maxRuntimeStateBytes": 28,
    "maxRuntimeScratchBytes": 8,
    "maxRxMessages": 1,
    "maxRxPrograms": 1,
    "maxTxMessages": 1,
    "maxTxPrograms": 2,
    "maxPoolSlots": 3,
    "maxConversions": 1,
    "maxNestedMuxRecords": 0,
    "maxMuxDepth": 0,
    "maxQualityEntries": 0,
    "maxProtectionPlans": 2,
    "maxTxCounters": 1,
    "maxRxCounters": 1,
    "maxCoverageSpans": 2,
    "maxTxTemplateBytes": 8,
    "maxPayloadBytes": 8
  }
}
"""

        let actual = writeCanonical capability |> unwrap
        Assert.Equal(expected, actual)
        Assert.DoesNotContain("\r", actual)
        Assert.False(actual.StartsWith("\uFEFF", StringComparison.Ordinal))
        Assert.EndsWith("\n", actual)
