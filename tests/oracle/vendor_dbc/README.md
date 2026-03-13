# Vendor DBC Subset for Oracle Testing

## Source

- **Repository**: https://github.com/commaai/opendbc
- **License**: MIT License
- **Commit**: `245cb1f2056071a7625e2ad7e4515f57784515bd`
- **Date**: February 13, 2026

## Selection Criteria

These 15 DBC files were curated from opendbc to provide diverse test coverage for Signal-CANdy:

- **Manufacturer diversity**: Toyota, Honda/Acura, Hyundai, Ford, GM, VW, BMW, Tesla, Chrysler, Mercedes, Volvo
- **Signal characteristics**: Mix of LE/BE byte orders, signed/unsigned signals, scaled signals (factor/offset)
- **Size range**: 4–331 messages per file (small to large)
- **Parsing success**: All files successfully parse and generate C code with Signal-CANdy v0.2.3+

## File Descriptions

### Toyota (3 files)
- `toyota_prius_2010_pt.dbc` — Toyota Prius 2010 powertrain, 26 messages
- `toyota_2017_ref_pt.dbc` — Toyota 2017 reference powertrain, 143 messages
- `toyota_adas.dbc` — Toyota ADAS system, 32 messages

### Hyundai/Kia (2 files)
- `hyundai_kia_generic.dbc` — Generic Hyundai/Kia platform, 146 messages
- `hyundai_2015_ccan.dbc` — Hyundai 2015 C-CAN network, 113 messages

### Ford (2 files)
- `ford_lincoln_base_pt.dbc` — Ford/Lincoln base powertrain, 331 messages (large file stress test)
- `ford_fusion_2018_pt.dbc` — Ford Fusion 2018 powertrain, 14 messages

### Volkswagen (1 file)
- `vw_meb.dbc` — VW Modular Electric Drive (MEB) platform, 124 messages

### BMW (1 file)
- `bmw_e9x_e8x.dbc` — BMW E9x/E8x series, 326 messages (large file, Motorola BE heavy)

### Honda/Acura (1 file)
- `acura_ilx_2016_nidec.dbc` — Acura ILX 2016 with Nidec ADAS, 36 messages

### GM (1 file)
- `gm_global_a_chassis.dbc` — GM Global A chassis network, 4 messages (minimal file)

### Tesla (1 file)
- `tesla_can.dbc` — Tesla CAN network, 44 messages

### Chrysler (1 file)
- `chrysler_pacifica_2017_hybrid_private_fusion.dbc` — Chrysler Pacifica 2017 Hybrid, 31 messages

### Mercedes-Benz (1 file)
- `mercedes_benz_e350_2010.dbc` — Mercedes-Benz E350 2010, 16 messages

### Volvo (1 file)
- `volvo_v60_2015_pt.dbc` — Volvo V60 2015 powertrain, 41 messages

## Usage

These files are used by `run_corpus.py` for batch validation:

```bash
python tests/oracle/run_corpus.py \
  --corpus-dir tests/oracle/vendor_dbc \
  --out-dir tmp/oracle_corpus \
  --report-only
```

## License Compliance

These files are redistributed under the MIT License per the opendbc project terms. The original files may be found at:
https://github.com/commaai/opendbc/tree/master/opendbc/dbc

MIT License text: https://github.com/commaai/opendbc/blob/master/LICENSE
