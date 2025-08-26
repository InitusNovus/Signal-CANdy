# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

## [Unreleased]

## [0.2.0] - TBD

### Added
- **C++ compatibility**: All generated headers now include `extern "C"` guards for seamless C++ integration.
- **Enhanced CLI**: Improved help messages with examples and proper exit codes.
- **Config validation**: Added validation for YAML config values with warnings for invalid options.
- **Compatibility shims**: Auto-generated `utils.h` and `registry.h` headers for backward compatibility.

### Changed
- **Prefix system**: Stabilized file prefix handling to prevent symbol conflicts and linker errors.
- **CLI help**: Enhanced `--help` output with version info, examples, and clearer option descriptions.

### Fixed
- **Header naming**: Resolved include path mismatches between prefixed and non-prefixed utilities.
- **Duplicate symbols**: Generator now cleans conflicting prefix variants to prevent linker errors.
- **Build stability**: Improved C build reliability with proper symbol management.

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
