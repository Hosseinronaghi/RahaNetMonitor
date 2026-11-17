# Raha Net Monitor

Windows and Android network usage monitoring project.

**Current status: v0.6.0 RC1 (source preview, not a stable release).** Compilation of Windows EXE and Android APK and device-level validation have not been confirmed.

The existing v0.6.0-rc1 source package includes a .NET 8 WinForms Windows app and a Kotlin/Jetpack Compose Android app, plus GitHub Actions configuration, tests and Persian documentation. The full source package has not yet been committed to this repository.

### Known limitations

- Windows currently measures per-network-interface traffic and lists TCP/IPv4 connections by process; it does **not** measure bytes per process.
- Android uses OS-reported, cumulative UID network statistics; per-destination monitoring is not yet implemented.
- Background scheduling via WorkManager is best-effort and may be delayed.
- The repository must not be described as production-ready until builds and physical-device tests pass.

### Planned repository layout

`windows/` — .NET application  
`android/` — Android application  
`tests/` — source checks  
`docs/` — release status, roadmap and test checklist  
`.github/workflows/` — automated builds
