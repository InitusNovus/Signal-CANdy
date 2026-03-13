namespace Signal.CANdy.Core

module Ir =

    type ByteOrder =
        | Little
        | Big

    type CrcAlgorithmId =
        | CRC8_SAE_J1850
        | CRC8_8H2F
        | CRC16_CCITT
        | CRC32P4
        | Custom of string

    type CrcAlgorithmParams =
        { Width: int
          Poly: uint64
          Init: uint64
          XorOut: uint64
          ReflectIn: bool
          ReflectOut: bool }

    type CrcSignalMeta =
        { Algorithm: CrcAlgorithmId
          Params: CrcAlgorithmParams
          ByteRange: {| Start: int; End: int |}
          DataId: uint16 option }

    type CounterSignalMeta = { Modulus: int; Increment: int }

    type CrcCounterMode =
        | Validate
        | Passthrough
        | FailFast

    type Signal =
        { Name: string
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
          MultiplexerIndicator: string option
          MultiplexerSwitchValue: int option
          ValueTable: (int * string) list option
          Receivers: string list
          CrcMeta: CrcSignalMeta option
          CounterMeta: CounterSignalMeta option }

    type Message =
        { Name: string
          Id: uint32
          IsExtended: bool
          Length: uint16
          Signals: Signal list
          Sender: string
          Receivers: string list
          CrcCounterMode: CrcCounterMode option }

    type Ir = { Messages: Message list }
