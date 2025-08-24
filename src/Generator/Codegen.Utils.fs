namespace Generator

open System.IO
open Generator.Ir
open Generator.Config

module Utils =

    let internal getCType (signal: Ir.Signal) (config: Config) =
        match config.PhysType with
        | "float" -> "float"
        | "fixed" ->
            let baseType =
                if (int signal.Length) <= 8 then "int8_t"
                elif (int signal.Length) <= 16 then "int16_t"
                elif (int signal.Length) <= 32 then "int32_t"
                else "int64_t" // Max 64 bits for raw value
            if signal.IsSigned then baseType else "u" + baseType
        | _ -> "float" // Default to float if unknown

    let internal getFixedPointScale (factor: float) (offset: float) =
        let factorDecimalPlaces =
            factor.ToString().Split('.')
            |> fun parts -> if Array.length parts > 1 then parts.[1].Length else 0
        let offsetDecimalPlaces =
            offset.ToString().Split('.')
            |> fun parts -> if Array.length parts > 1 then parts.[1].Length else 0
        let maxDecimalPlaces = max factorDecimalPlaces offsetDecimalPlaces
        pown 10.0 maxDecimalPlaces // 10^maxDecimalPlaces

    // Detect if factor equals 10^-n within tolerance and return integer scale (10^n)
    let internal tryPowerOfTenScale (factor: float) : int64 option =
        if factor <= 0.0 then None else
        let eps = 1e-12
        let rec loop n =
            if n > 9 then None // support up to 10^-9
            else
                let scaleF = pown 10.0 n
                if abs (factor - (1.0 / scaleF)) < eps then Some (int64 (pown 10 n)) else loop (n + 1)
        loop 0

    // Convert Motorola (BE) start bit from LSB-convention to MSB-convention using sawtooth numbering.
    // start: LSB-position index (0..63), length: number of bits
    let internal motorolaMsbFromLsb (start: int) (length: int) : int =
        let steps = max 0 (length - 1)
        let mutable byteIdx = start / 8
        let mutable bitIdx = start % 8 // 0..7, where 7 is MSB
        for _ in 1 .. steps do
            if bitIdx < 7 then
                bitIdx <- bitIdx + 1
            else
                // Move to next higher byte, at MSB position (sawtooth)
                byteIdx <- byteIdx + 1
                bitIdx <- 7
        byteIdx * 8 + bitIdx

    // Choose effective start bit depending on config for Motorola signals; LE stays unchanged.
    let internal chooseStartBit (signal: Ir.Signal) (config: Config) : int =
        let start = int signal.StartBit
        let len = int signal.Length
        match signal.ByteOrder with
        | ByteOrder.Big ->
            match (config.MotorolaStartBit |> (fun s -> s.ToLowerInvariant())) with
            | "lsb" -> motorolaMsbFromLsb start len
            | _ -> start // default "msb"
        | _ -> start

    let utilsHContent = 
        "#ifndef UTILS_H\n#define UTILS_H\n\n#include <stdint.h>\n#include <stdbool.h>\n\n// Little-endian bit extraction functions\nuint64_t get_bits_le(const uint8_t* data, uint16_t start_bit, uint16_t length);\n\n// Little-endian bit insertion functions\nvoid set_bits_le(uint8_t* data, uint16_t start_bit, uint16_t length, uint64_t value);\n\n// Big-endian (Motorola) bit extraction\nuint64_t get_bits_be(const uint8_t* data, uint16_t start_bit, uint16_t length);\n\n// Big-endian (Motorola) bit insertion\nvoid set_bits_be(uint8_t* data, uint16_t start_bit, uint16_t length, uint64_t value);\n\n#endif // UTILS_H"

    let utilsCContent = 
        "#include \"utils.h\"\n\n// Little-endian bit extraction\nuint64_t get_bits_le(const uint8_t* data, uint16_t start_bit, uint16_t length) {\n    uint64_t value = 0;\n    uint16_t byte_offset = start_bit / 8;\n    uint16_t bit_offset = start_bit % 8;\n    for (uint16_t i = 0; i < 8 && (byte_offset + i) < 8; ++i) {\n        value |= (uint64_t)data[byte_offset + i] << (i * 8);\n    }\n    value >>= bit_offset;\n    value &= (1ULL << length) - 1;\n    return value;\n}\n\n// Little-endian bit insertion\nvoid set_bits_le(uint8_t* data, uint16_t start_bit, uint16_t length, uint64_t value) {\n    uint16_t byte_offset = start_bit / 8;\n    uint16_t bit_offset = start_bit % 8;\n    uint64_t clear_mask = ((1ULL << length) - 1) << bit_offset;\n    for (uint16_t i = 0; i < 8 && (byte_offset + i) < 8; ++i) {\n        data[byte_offset + i] &= ~(uint8_t)(clear_mask >> (i * 8));\n    }\n    uint64_t insert_value = (value & ((1ULL << length) - 1)) << bit_offset;\n    for (uint16_t i = 0; i < 8 && (byte_offset + i) < 8; ++i) {\n        data[byte_offset + i] |= (uint8_t)(insert_value >> (i * 8));\n    }\n}\n\n// Big-endian (Motorola) bit extraction (DBC semantics, sawtooth)\nuint64_t get_bits_be(const uint8_t* data, uint16_t start_bit, uint16_t length) {\n    uint64_t value = 0;\n    int byte = start_bit / 8;\n    int bit = start_bit % 8; // 7..0 within byte, 7 is MSB\n    for (uint16_t i = 0; i < length; ++i) {\n        int curByte = byte;\n        int curBit = bit - (int)i;\n        while (curBit < 0) { curBit += 8; ++curByte; } // move to next higher byte\n        uint8_t b = data[curByte];\n        uint8_t bitval = (uint8_t)((b >> curBit) & 1u);\n        value = (value << 1) | bitval; // assemble MSB-first\n    }\n    return value;\n}\n\n// Big-endian (Motorola) bit insertion (DBC semantics, sawtooth)\nvoid set_bits_be(uint8_t* data, uint16_t start_bit, uint16_t length, uint64_t value) {\n    int byte = start_bit / 8;\n    int bit = start_bit % 8;\n    for (uint16_t i = 0; i < length; ++i) {\n        int curByte = byte;\n        int curBit = bit - (int)i;\n        while (curBit < 0) { curBit += 8; ++curByte; } // move to next higher byte\n        uint8_t bitval = (uint8_t)((value >> (length - 1 - i)) & 1u); // MSB-first\n        data[curByte] = (uint8_t)((data[curByte] & (uint8_t)~(1u << curBit)) | (uint8_t)(bitval << curBit));\n    }\n}"

    // Helper to choose C accessor based on byte order
    let accessorNames (byteOrder: ByteOrder) =
        match byteOrder with
        | ByteOrder.Little -> ("get_bits_le", "set_bits_le")
        | ByteOrder.Big -> ("get_bits_be", "set_bits_be")