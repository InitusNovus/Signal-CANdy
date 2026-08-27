namespace Signal.CANdy.Core

open System
open System.IO
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.ProjectManifest
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.RuntimeRequirements
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Core.Wire

module RuntimeBuild =

    type RuntimeBuildInputs =
        { Pool: PoolContract
          Wires: (string * WireIr) list
          Bindings: BindingSet }

    type CompiledRuntime =
        { Pool: PoolContract
          Linked: LinkedSchema
          Image: RuntimeImage
          ImageBytes: byte array
          InspectJson: string
          PoolAbiHash: PoolAbiHash
          Requirements: RuntimeRequirements }

    type RuntimeBuildError = RuntimeBuildError of string

    type RuntimeResource =
        | ImageBytes
        | RuntimeStateBytes
        | RuntimeScratchBytes
        | RxMessages
        | RxPrograms
        | TxMessages
        | TxPrograms
        | PoolSlots
        | Conversions
        | NestedMuxRecords
        | MuxDepth
        | QualityEntries
        | ProtectionPlans
        | TxCounters
        | RxCounters
        | CoverageSpans
        | TxTemplateBytes
        | PayloadBytes

    type CapabilityMismatch =
        | RuntimeVersionMismatch of uint16 * uint16 * uint16 * uint16
        | MissingRuntimeFeature of RuntimeFeature
        | RuntimeLimitExceeded of RuntimeResource * uint32 * uint32
        | PoolAbiMismatch of string * string

    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay generator = generator
        member _.Run generator = generator ()
        member _.Combine(first, second) = Result.bind (fun () -> second ()) first

        member _.For(values, body) =
            values
            |> Seq.fold (fun state value -> Result.bind (fun () -> body value) state) (Ok())

    let private result = ResultBuilder()

    type ProjectBuildError =
        | InputIo of string * string
        | InputValidation of string
        | RuntimeFailure of RuntimeBuildError
        | TargetMismatch of CapabilityMismatch

    let private buildError errors =
        errors |> List.map (sprintf "%A" >> RuntimeBuildError)

    let compile (inputs: RuntimeBuildInputs) =
        result {
            let! wire = Wire.merge inputs.Wires |> Result.mapError buildError
            let! linked = Linked.link inputs.Pool wire inputs.Bindings |> Result.mapError buildError
            let! image = Scimg.lower linked |> Result.mapError buildError
            let! bytes = Scimg.write image |> Result.mapError buildError
            let! inspection = Scimg.inspect bytes |> Result.mapError buildError
            let! hash = PoolAbi.compute inputs.Pool |> Result.mapError buildError

            let! requirements =
                RuntimeRequirements.derive inputs.Pool linked image bytes
                |> Result.mapError buildError

            return
                { Pool = inputs.Pool
                  Linked = linked
                  Image = image
                  ImageBytes = bytes
                  InspectJson = inspection
                  PoolAbiHash = hash
                  Requirements = requirements }
        }

    let private limitCases (limits: RuntimeLimits) (required: RuntimeRequirements) =
        [ ImageBytes, required.ImageBytes, limits.MaxImageBytes
          RuntimeStateBytes, required.RuntimeStateBytes, limits.MaxRuntimeStateBytes
          RuntimeScratchBytes, required.RuntimeScratchBytes, limits.MaxRuntimeScratchBytes
          RxMessages, required.RxMessages, limits.MaxRxMessages
          RxPrograms, required.RxPrograms, limits.MaxRxPrograms
          TxMessages, required.TxMessages, limits.MaxTxMessages
          TxPrograms, required.TxPrograms, limits.MaxTxPrograms
          PoolSlots, required.PoolSlots, limits.MaxPoolSlots
          Conversions, required.Conversions, limits.MaxConversions
          NestedMuxRecords, required.NestedMuxRecords, limits.MaxNestedMuxRecords
          MuxDepth, required.MuxDepth, limits.MaxMuxDepth
          QualityEntries, required.QualityEntries, limits.MaxQualityEntries
          ProtectionPlans, required.ProtectionPlans, limits.MaxProtectionPlans
          TxCounters, required.TxCounters, limits.MaxTxCounters
          RxCounters, required.RxCounters, limits.MaxRxCounters
          CoverageSpans, required.CoverageSpans, limits.MaxCoverageSpans
          TxTemplateBytes, required.TxTemplateBytes, limits.MaxTxTemplateBytes
          PayloadBytes, required.PayloadBytes, limits.MaxPayloadBytes ]

    let validateTarget (target: RuntimeCapabilities) (compiled: CompiledRuntime) =
        let required = compiled.Requirements
        let errors = ResizeArray<CapabilityMismatch>()

        if
            target.RuntimeImageMajor <> required.RuntimeImageMajor
            || target.RuntimeImageMinor < required.RuntimeImageMinor
        then
            errors.Add(
                RuntimeVersionMismatch(
                    required.RuntimeImageMajor,
                    required.RuntimeImageMinor,
                    target.RuntimeImageMajor,
                    target.RuntimeImageMinor
                )
            )

        for feature, _ in RuntimeCapabilities.featurePairs do
            if required.Features.Contains feature && not (target.Features.Contains feature) then
                errors.Add(MissingRuntimeFeature feature)

        for resource, needed, supported in limitCases target.Limits required do
            if needed > supported then
                errors.Add(RuntimeLimitExceeded(resource, needed, supported))

        match target.PoolAbiHash with
        | Some supported when supported <> compiled.PoolAbiHash ->
            errors.Add(PoolAbiMismatch(PoolAbi.format compiled.PoolAbiHash, PoolAbi.format supported))
        | _ -> ()

        if errors.Count = 0 then
            Ok compiled
        else
            Error(List.ofSeq errors)

    let loadAndCompile (project: ResolvedProject) =
        let read source path =
            try
                Ok(File.ReadAllText(path))
            with ex ->
                Error[InputIo(source, ex.Message)]

        result {
            let poolPath =
                match project.Pool with
                | ResolvedDefinition p
                | ResolvedManifest p -> p

            let! poolText = read "pool" poolPath

            let! pool =
                Pool.parsePoolDefinition poolText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let mutable wires = []

            for source in project.WireSources do
                let! ir =
                    Dbc.parseDbcFile source.Path
                    |> Result.mapError (fun error -> [ InputValidation(sprintf "%A" error) ])

                let! wire =
                    Wire.toWireModel ir
                    |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

                wires <- wires @ [ source.Name, wire ]

            let! bindingText = read "binding" project.Binding

            let! bindings =
                Binding.parseBindingSet bindingText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let! targetText = read "target" project.Target

            let! target =
                RuntimeCapabilities.parse targetText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let! compiled =
                compile
                    { Pool = pool
                      Wires = wires
                      Bindings = bindings }
                |> Result.mapError (List.map RuntimeFailure)

            let! valid = validateTarget target compiled |> Result.mapError (List.map TargetMismatch)
            return valid, target
        }
