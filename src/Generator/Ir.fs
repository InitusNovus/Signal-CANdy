namespace Generator

module Ir =

    type ByteOrder =
        | Little
        | Big

    type SignalType =
        | Signed
        | Unsigned
        | Float

    type Signal = {
        Name: string
        StartBit: uint16
        Length: uint16
        Factor: float
        Offset: float
        Minimum: float option
        Maximum: float option
        Unit: string
        IsSigned: bool
        IsCrc: bool
        IsCounter: bool
        ByteOrder: ByteOrder
        MultiplexerIndicator: string option // e.g., "M" for switch, "mX" for value
        MultiplexerSwitchValue: int option
        ValueTable: (int * string) list option
        Receivers: string list
    }

    type Message = {
        Name: string
        Id: uint32
        IsExtended: bool
        Length: uint16
        Signals: Signal list
        Sender: string
        Receivers: string list
    }

    type Ir = {
        Messages: Message list
    }
