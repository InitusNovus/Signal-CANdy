namespace Generator

module Result =
    let (|Success|Failure|) (r: Result<_,_>) =
        match r with
        | Ok v -> Success v
        | Error e -> Failure e
