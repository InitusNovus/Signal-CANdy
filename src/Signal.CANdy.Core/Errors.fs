namespace Signal.CANdy.Core

module Errors =
    type ParseError =
        | InvalidDbc of string
        | IoError of string
        | Unknown of string

    type CodeGenError =
        | TemplateError of string
        | IoError of string
        | Unknown of string
        | UnsupportedFeature of string

    type ValidationError =
        | InvalidValue of string
        | MissingField of string
        | IoError of string
        | Unknown of string

    /// Unified error type for the full generate-from-paths pipeline.
    /// Preserves the original error category so consumers can distinguish
    /// parse failures from validation failures from codegen failures.
    type GenerateError =
        | Parse of ParseError
        | Validation of ValidationError
        | CodeGen of CodeGenError

    type GeneratedFiles =
        { Sources: string list
          Headers: string list
          Others: string list }
