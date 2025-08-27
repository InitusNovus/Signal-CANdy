namespace Signal.CANdy

open System
open System.Threading.Tasks

/// Exceptions for C#-friendly facade API
type SignalCandyException(message: string) =
    inherit Exception(message)

type SignalCandyValidationException(message: string) =
    inherit SignalCandyException(message)

type SignalCandyParseException(message: string) =
    inherit SignalCandyException(message)

type SignalCandyCodeGenException(message: string) =
    inherit SignalCandyException(message)

/// C#-friendly Facade wrapping Signal.CANdy.Core
type GeneratorFacade() =
    member _.Version : string = Signal.CANdy.Core.Api.version ()

    member _.ValidateConfig(cfg: Signal.CANdy.Core.Config.Config) : unit =
        match Signal.CANdy.Core.Config.validate cfg with
        | Ok _ -> ()
        | Error e ->
            let msg = match e with
                      | Signal.CANdy.Core.Errors.ValidationError.InvalidValue s -> s
                      | Signal.CANdy.Core.Errors.ValidationError.MissingField s -> s
                      | Signal.CANdy.Core.Errors.ValidationError.IoError s -> s
                      | Signal.CANdy.Core.Errors.ValidationError.Unknown s -> s
            raise (SignalCandyValidationException(msg))

    member _.ParseDbc(path: string) : Signal.CANdy.Core.Ir.Ir =
        match Signal.CANdy.Core.Api.parseDbc path with
        | Ok ir -> ir
        | Error e ->
            let msg = match e with
                      | Signal.CANdy.Core.Errors.ParseError.InvalidDbc s -> s
                      | Signal.CANdy.Core.Errors.ParseError.IoError s -> s
                      | Signal.CANdy.Core.Errors.ParseError.Unknown s -> s
            raise (SignalCandyParseException(msg))

    member _.GenerateCode(ir: Signal.CANdy.Core.Ir.Ir, outputPath: string, cfg: Signal.CANdy.Core.Config.Config) : Signal.CANdy.Core.Errors.GeneratedFiles =
        match Signal.CANdy.Core.Api.generateCode ir outputPath cfg with
        | Ok files -> files
        | Error e ->
            let msg = match e with
                      | Signal.CANdy.Core.Errors.CodeGenError.TemplateError s -> s
                      | Signal.CANdy.Core.Errors.CodeGenError.IoError s -> s
                      | Signal.CANdy.Core.Errors.CodeGenError.Unknown s -> s
            raise (SignalCandyCodeGenException(msg))

    member _.GenerateFromPathsAsync(dbcPath: string, outputPath: string, configPath: string) : Task<Signal.CANdy.Core.Errors.GeneratedFiles> = task {
        let! res = Signal.CANdy.Core.Api.generateFromPaths dbcPath outputPath (if String.IsNullOrWhiteSpace configPath then None else Some configPath)
        match res with
        | Ok files -> return files
        | Error e ->
            let msg = match e with
                      | Signal.CANdy.Core.Errors.CodeGenError.TemplateError s -> s
                      | Signal.CANdy.Core.Errors.CodeGenError.IoError s -> s
                      | Signal.CANdy.Core.Errors.CodeGenError.Unknown s -> s
            return raise (SignalCandyCodeGenException(msg))
    }
