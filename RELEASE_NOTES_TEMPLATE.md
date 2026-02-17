# HRpc v1.0.2 Release Notes

Release date: 2026-02-17
NuGet: https://www.nuget.org/packages/HRpc/1.0.2

## Summary

- Documentation patch release to correct NuGet package README for end-user package consumption.

## Added

- Server-side quick start section in NuGet README.

## Changed

- README reorganized to package-consumer format.
- Installation instructions now lead with `dotnet add package HRpc`.
- Added protocol and error-handling notes relevant to library users.

## Fixed

- Removed repository maintenance/build content from package README.
- NuGet README now reflects real package usage instead of project contributor workflow.

## Breaking Changes

- None.

## Migration Notes

- No migration required.

## Verification

- Target frameworks validated: `net6.0`, `net7.0`, `net9.0`
- Test command: `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- Pack command: `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## Checksums / Artifacts

- `HRpc.1.0.2.nupkg`

## Contributors

- Hamed Afzali
