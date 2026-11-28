# MeetJoe.iOS (M0)

Thin Avalonia host for the App Store. Shared UI: `MeetJoe.App`.

## Prerequisites

```bash
dotnet workload install ios
# Xcode (Mac required to build/run)
```

## Run (simulator)

```bash
dotnet build src/MeetJoe.iOS/MeetJoe.iOS.csproj -t:Run -f net10.0-ios
```

## Store

Publish `.ipa` later (M3) with distribution signing + Fastlane/TestFlight.
