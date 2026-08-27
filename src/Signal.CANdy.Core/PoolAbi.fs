namespace Signal.CANdy.Core

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography
open System.Text
open Signal.CANdy.Core.Pool

module PoolAbi =

    [<CustomEquality; NoComparison>]
    type PoolAbiHash =
        private
        | PoolAbiHash of byte array

        override this.Equals(other) =
            match other with
            | :? PoolAbiHash as value ->
                let (PoolAbiHash left) = this
                let (PoolAbiHash right) = value
                left.AsSpan().SequenceEqual(right)
            | _ -> false

        override this.GetHashCode() =
            let (PoolAbiHash bytes) = this
            HashCode.Combine(bytes.[0], bytes.[1], bytes.[2], bytes.[3])

    type PoolAbiError =
        | InvalidPool of string
        | InvalidHash of string

    let private storageCode =
        function
        | U8 -> 0uy
        | U16 -> 1uy
        | U32 -> 2uy
        | U64 -> 3uy
        | I8 -> 4uy
        | I16 -> 5uy
        | I32 -> 6uy
        | I64 -> 7uy
        | F32 -> 8uy
        | F64 -> 9uy

    let private directionCode =
        function
        | Rx -> 0uy
        | Tx -> 1uy

    let canonicalBytes contract =
        match Pool.validate contract with
        | Error errors -> Error(errors |> List.map (sprintf "%A" >> InvalidPool))
        | Ok valid ->
            try
                use stream = new MemoryStream()
                use writer = new BinaryWriter(stream, UTF8Encoding(false), true)
                writer.Write(Encoding.ASCII.GetBytes("SCPOOLABI\000"))
                writer.Write(1us)
                writer.Write(uint32 valid.Signals.Length)

                for signal in valid.Signals do
                    let unitBytes = UTF8Encoding(false, true).GetBytes(signal.Unit)

                    if unitBytes.Length > int UInt16.MaxValue then
                        raise (InvalidDataException("Pool unit UTF-8 length exceeds uint16."))

                    writer.Write(signal.SemanticId)
                    writer.Write(storageCode signal.Storage)
                    writer.Write(directionCode signal.Direction)
                    writer.Write(uint16 unitBytes.Length)
                    writer.Write(unitBytes)

                writer.Flush()
                Ok(stream.ToArray())
            with ex ->
                Error[InvalidPool ex.Message]

    let compute contract =
        canonicalBytes contract |> Result.map (SHA256.HashData >> PoolAbiHash)

    let format (PoolAbiHash bytes) =
        "sha256:" + (Convert.ToHexString(bytes).ToLowerInvariant())

    let parse (text: string) =
        if
            isNull text
            || not (Text.RegularExpressions.Regex.IsMatch(text, "^sha256:[0-9a-f]{64}$"))
        then
            Error(InvalidHash "Pool ABI hash must be sha256 followed by 64 lowercase hexadecimal digits.")
        else
            try
                Ok(PoolAbiHash(Convert.FromHexString(text.Substring(7))))
            with ex ->
                Error(InvalidHash ex.Message)
