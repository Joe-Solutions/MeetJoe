# MeetJoe.Android (M0)

Thin Avalonia host for Google Play. Shared UI: `MeetJoe.App`.

## Prerequisites

```bash
dotnet workload install android
# Android SDK via Android Studio (API 24+)
```

## Run (emulator / device)

```bash
dotnet build src/MeetJoe.Android/MeetJoe.Android.csproj -t:Run -f net10.0-android
```

Set `MEETJOE_API_URL` to your API (default production or local `http://10.0.2.2:5080` for emulator → host).

## Store

Publish `.aab` later (M3): `dotnet publish -c Release -f net10.0-android -p:AndroidPackageFormat=aab`
