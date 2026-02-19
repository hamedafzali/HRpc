# HRpc v1.1.1 Release Notes

Release date: 2026-02-19
NuGet: https://www.nuget.org/packages/HRpc/1.1.1

## Summary

- Patch release adding InitialMessage property to PipeServer for initial message sending on connection.

## Added

- InitialMessage property to PipeServer for sending an initial message upon client connection.

## Changed

- None.

## Fixed

- None.

## Breaking Changes

- None.

## Migration Notes

- No migration required.

## Verification

- Target frameworks validated: `net48`, `net6.0`, `net7.0`, `net9.0`
- Test command: `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- Pack command: `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## Checksums / Artifacts

- `HRpc.1.1.1.nupkg`

## Contributors

- Hamed Afzali
