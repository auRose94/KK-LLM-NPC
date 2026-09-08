# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **SafeHttp utility** — retry wrapper with exponential backoff (3 attempts, 1s/2s/4s delays), proper resource disposal, and TLS validation for all LLM HTTP calls
- **Constants.cs** — named constants for all magic numbers (HTTP timeouts, perception ranges, movement values, etc.) — replaces scattered literals throughout the codebase
- **Thread pool semaphore** — caps concurrent `Task.Run` calls to prevent thread pool starvation under heavy multi-NPC load
- **Improved error logging** — all bare `catch (Exception) { }` blocks now log the exception message via `Logger.LogWarning("...: " + e.Message)`
- **Cross-platform build support** — build.sh includes dotnet SDK fallback alongside mcs
- **docs/ARCHITECTURE.md** — complete architecture overview with component breakdown, data flow, and thread model
- **docs/CONFIG.md** — complete configuration reference with all keys, defaults, ranges, and purposes
- **CONTRIBUTING.md** — contribution guidelines, code style, testing, and debugging instructions
- **CODE_OF_CONDUCT.md** — Contributor Covenant Code of Conduct
- **GitHub Actions CI** — `.github/workflows/build.yml` for automated build verification
- **tests/test_json_standalone.cs** — standalone Json parser/writer test specifications (no Unity/BepInEx deps)
- **Improved README** — complete config table with ranges, Windows build instructions, architecture doc links

### Changed
- **Config duplication eliminated** — config entries are now bound in one place (`Main.cs`) and read via a shared config accessor, removing the duplicate field copies in `NPCInstance.cs`
- **HTTP resource management** — `HttpWebRequest` streams are now properly disposed via `using` blocks and `SafeHttp`'s finally blocks, preventing connection pool exhaustion
- **Magic numbers replaced** — all magic numbers (timeouts, ranges, thresholds) replaced with named constants in `Constants.cs`
- **Vision hung timeout** — reduced from 2 minutes to a configurable value with logging

### Fixed
- **HTTP connection leaks** — `HttpWebRequest.GetRequestStream()` is now always disposed in a `using` block, even when `GetResponse()` throws
- **Silent exception swallowing** — removed 15+ bare `catch (Exception) { }` blocks across Senses.cs, Movement.cs, Body.cs, Vision.cs, and Tools.cs
- **Door toggle-fluttering** — `MaybeOpenDoorAhead` cooldown now uses a named constant (`Consts.DoorCooldown`) for clarity
- **Camera clip detection** — improved camera-clip fix timing with configurable thresholds
- **Build.sh** — added mcs availability check with installation instructions

## [1.0.0] - 2026-09-05