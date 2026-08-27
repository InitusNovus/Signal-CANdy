# Nested mux quality runtime-image fixture

This issue #20 fixture compiles one standard 8-byte RX message, decimal ID 804 (`0x324`). `Outer` is the root selector. `Inner` is active under `Outer == 1`; `InnerA` and `InnerB` are active under `Inner == 1` and `Inner == 2`; `OuterB` is active directly under `Outer == 2`. The four `SG_MUL_VAL_` records use singleton `N-N` ranges and name each direct parent. `InnerA` alone has a 200 ms freshness threshold.

Pool ABI order is `Outer`, `Inner`, `InnerA`, `InnerB`, `OuterB`. All five signals are RX-bound with identity conversion and there are no TX messages.

Generate and inspect:

```sh
dotnet run --project src/Signal.CANdy.CLI -c Release -- scimg \
  -d examples/scimg_quality_demo/quality_demo.dbc \
  -p examples/scimg_quality_demo/pool.json \
  -b examples/scimg_quality_demo/binding.json \
  -o quality_demo.scimg --inspect quality_demo.inspect.json
```

At commit `60d04b2`, the deterministic image is 372 bytes with SHA-256 `1e5f2348ce5474a33a8eda4aa8a7a101a7bafe55450a219ec73a3b75f05f767f`, CRC32 `0x98DDACC6`, feature flags `0x0002` (`RXQ`), two nested records, five quality entries, and zero TX messages/programs/counters.
