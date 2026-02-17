# NuGet Publish Checklist (HRpc)

## 1. Prepare

- [ ] Version bumped in `/Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj`
- [ ] `/Users/hamed.afzali/Desktop/Repos/HRpc/README.md` matches current API
- [ ] `/Users/hamed.afzali/Desktop/Repos/HRpc/project.md` updated if process changed
- [ ] Release notes drafted from `/Users/hamed.afzali/Desktop/Repos/HRpc/RELEASE_NOTES_TEMPLATE.md`

## 2. Validate

- [ ] `dotnet restore /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln`
- [ ] `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- [ ] `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`
- [ ] `ls -la /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## 3. Publish

- [ ] `dotnet nuget push "/Users/hamed.afzali/Desktop/Repos/HRpc/artifacts/HRpc.<VERSION>.nupkg" --api-key "<NUGET_API_KEY>" --source "https://api.nuget.org/v3/index.json" --skip-duplicate`

## 4. Post-publish

- [ ] Package page is live on NuGet
- [ ] README renders correctly on NuGet package page
- [ ] Smoke install works in clean sample: `dotnet add package HRpc --version <VERSION>`
- [ ] Tag/release notes published in repository

## 5. If something is wrong

- [ ] Deprecate the bad version in NuGet
- [ ] Publish patched version (`+1` patch)
- [ ] Update release notes with migration guidance
