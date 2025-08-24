# Fetching more DBC files for testing

This project does not include third-party DBC files due to licensing. Here are safe ways to expand your test corpus:

- Use your organization’s in-house DBCs (ensure permission and strip proprietary content before sharing).
- Public samples from tool vendors, academic datasets, or automotive forums that explicitly allow redistribution.
- Create synthetic DBCs using a generator (vary endianness, multiplexing, scaling, ranges).

## Suggested sources

- CAN bus tutorial sites often ship small example DBCs.
- Search terms: "sample dbc file", "dbc example multiplex", "vector dbc sample".

## Importing into this repo

- Place external files under `external_test/` (ignored in product builds).
- Do not commit files unless their license allows redistribution.

## Validating new DBCs

1. Generate code
   - dotnet run --project src/Generator -- --dbc external_test/<file>.dbc --out gen --config examples/config.yaml
2. Build generated C and run smoke tests
   - make -C gen build
   - ./gen/build/test_runner test_be_basic
   - ./gen/build/test_runner test_multiplex_roundtrip

3. Run stress suite
   - ./gen/build/test_runner test_stress_suite

If a DBC triggers parser/codegen issues, minimize and add a synthetic reproduction in `examples/`.
