# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

## [0.1.0] - 2025-08-25

### Added
- Initial public release of Signal CANdy: DBC → C99 code generator (F#).
- Prefix system for generated symbols to avoid collisions (`--prefix`).
- CLI flags: `--prefix`, `--emit-main`, `--config` for generator behavior control.
- GitHub Actions CI: build, test, codegen sanity, and C build validation with Make.
- Tag-triggered Release workflow to auto-create GitHub Releases on `v*` tags.
- Comprehensive README (EN/KR) with quick start, behavior notes, performance methodology, and Motorola MSB diagram.

### Changed
- Repository rebrand and documentation alignment to "Signal CANdy".
- Templates banner metadata and consistent file headers in generated C.

### Fixed
- Makefile/linking reliability on CI (ensure `-lm`).
- Test isolation with fallback Makefile generation in `gen`.

[0.1.0]: https://github.com/InitusNovus/Signal-CANdy/releases/tag/v0.1.0
