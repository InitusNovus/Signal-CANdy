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

    type ValidationError =
        | InvalidValue of string
        | MissingField of string
        | IoError of string
        | Unknown of string

    type GeneratedFiles = {
        Sources: string list
        Headers: string list
        Others: string list
    }
