# Building PoorMansFeed

How to build and test PoorMansFeed from its source, and how the release files are made. This file goes out with each release's source, so the copy in a release's tag describes how that release is built.

## What you need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) in the 10.0.4xx band: 10.0.400 or a later patch. `global.json` asks for 10.0.400 and uses the newest 10.0.4xx patch you have installed; an SDK from another band, .NET 11 included, isn't used.
- A Mac, to build the macOS app: the `.app` bundle is put together with tools only macOS has.
- PowerShell 7, to run the publish script on Windows.

The packages come from NuGet the first time you build.

## Getting a release's source

Each release's source is the commit its tag points to, and `main` holds the latest release. Releases tagged before this file was added have no copy of it, and build the same way:

```bash
git clone https://github.com/asfritecx/PoorMansFeed.git
cd PoorMansFeed
git checkout v1.2.1      # or any release's tag
```

## Run, build and test

```bash
dotnet run --project src/PoorMansFeed      # start the app
dotnet build                               # build the solution, PoorMansFeed.slnx
dotnet test                                # run the tests
```

- `dotnet build --warnaserror` fails on any warning, nullable warnings included.
- A Release build fails on a package with a known vulnerability (NuGet's audit warnings NU1901 to NU1904 are errors).
- Package versions are locked in each project's `packages.lock.json`. After changing a package, run `dotnet restore` and check the lock file's changes.
- Some tests are skipped where they don't apply, so a run shows skips on every platform: the DPAPI tests run only on Windows, the file permission tests only on macOS and Linux (a few, which use `chflags` or `chmod +a`, only on macOS), and the symbolic link tests only where the account can make links. The macOS keychain tests run only when `POORMANSFEED_KEYCHAIN_TESTS=1` is set, and they use keychain items of their own.

## Release files

```bash
./build/publish.sh                  # win-x64 and osx-arm64
./build/publish.sh osx-arm64        # one runtime
pwsh -NoProfile -File build/publish.ps1 -Runtime win-x64      # the same on Windows
```

The scripts restore once in locked mode, so a lock file that is out of date fails here. They write to `artifacts/` (git-ignored):

- `artifacts/win-x64/PoorMansFeed.exe`: a self-contained single-file app.
- `artifacts/osx-arm64/PoorMansFeed.app`: the self-contained single-file app in a macOS bundle with its icon, ad-hoc signed. It is built on a Mac; on any other computer the script leaves the bare executable in `artifacts/osx-arm64/`.

Each build carries the .NET runtime of the SDK that made it, so .NET doesn't need to be installed where it runs, and a newer SDK patch brings a newer runtime. The version number comes from `<Version>` in `Directory.Build.props`.

A release's downloads are these two builds, the exe as `PoorMansFeed-{version}-win-x64.exe` and the app zipped as `PoorMansFeed-{version}-osx-arm64.zip`, with the SHA-256 of each in `SHA256SUMS.txt`, and the licence and notices as `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`.

A build you make yourself wasn't downloaded, so Windows and macOS open it without the first-launch warnings the release files get.

## Project structure

```
PoorMansFeed/
├── PoorMansFeed.slnx
├── Directory.Build.props        # shared settings and the version number
├── global.json                  # SDK version
├── nuget.config                 # package source
├── build/                       # publish.sh and publish.ps1
├── src/
│   ├── PoorMansFeed.Core/       # dataset, database, search, export, exceptions, the cloud range tools and AbuseIPDB (no UI)
│   └── PoorMansFeed/            # Avalonia desktop app
├── tests/
│   └── PoorMansFeed.Tests/      # xUnit tests, with headless window tests
├── README.md
├── BUILDING.md                  # this file
├── LICENSE                      # MIT licence
└── THIRD-PARTY-NOTICES.txt      # licences of the components in the release builds
```
