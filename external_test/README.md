external_test policy

This folder is for local testing with public DBCs only. By default, everything in this folder is ignored by Git except this README and .gitkeep.

Rules
- Do not commit proprietary or confidential DBCs.
- Prefer permissively licensed public DBCs (e.g., OpenDBC under MIT).
- If you must reference a third-party file in docs or scripts, include provenance and license.

Tips
- Keep large DBCs out of Git to avoid bloat and license risks.
- Use scripts/bulk_stress.ps1 to run stress tests across your local corpus.

See also
- ../THIRD_PARTY_NOTICES.md# external_test DBC corpus

This folder contains external, publicly available DBC files used for broader validation.

- rivian_park_assist_can.dbc
  - Source: https://github.com/commaai/opendbc (MIT License)
  - Path in upstream repo: opendbc/dbc/rivian_park_assist_can.dbc
  - Purpose: small, representative DBC to exercise parsing/codegen and the stress suite

Notes
- Only include files with permissive licenses suitable for redistribution (e.g., MIT).
- Do not commit proprietary or confidential DBC files.
