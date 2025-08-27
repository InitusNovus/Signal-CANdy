namespace Signal.CANdy.Core

module Config =
    type Config = {
        PhysType: string
        PhysMode: string
        RangeCheck: bool
        Dispatch: string
        CrcCounterCheck: bool
        MotorolaStartBit: string
        FilePrefix: string
    }
