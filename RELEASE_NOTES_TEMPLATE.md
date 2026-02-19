# HRpc v1.1.0 Release Notes

Release date: 2026-02-19
NuGet: https://www.nuget.org/packages/HRpc/1.1.0

## Summary

- Feature release adding local Named Pipe transport and .NET Framework 4.8 support.

## Added

- Local Named Pipe client transport (`PipeConnection`, `PipeClientWrapper`).
- `net48` target framework support.

## Changed

- NuGet package metadata updated (description/tags) and README updated with Named Pipe example.
- Removed legacy `pipetemplate/` sources that depended on external packages.

## Fixed

- `TcpServer.StartAsync` made more robust against race during shutdown by using a captured cancellation token.

## Breaking Changes

- None.

## Migration Notes

- No migration required.

## Verification

- Target frameworks validated: `net48`, `net6.0`, `net7.0`, `net9.0`
- Test command: `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- Pack command: `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## Checksums / Artifacts

- `HRpc.1.1.0.nupkg`

## Contributors

- Hamed Afzali
