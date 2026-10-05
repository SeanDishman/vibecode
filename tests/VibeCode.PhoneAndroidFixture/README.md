# Android to .NET bridge fixture

This console host runs the production `PhoneBridgeService`, TLS certificate pinning, device-proof verifier,
WPF dispatcher, transcript mirror, and chat command dispatch. It uses a new `VIBECODE_DATA_DIR` below the
supplied output directory and one synthetic chat with an `ICodingSession` adapter that never starts a provider.
All listeners, including test controls, bind only to `127.0.0.1` (18766/18767). Do not expose these fixture ports.

Build the Android debug, instrumentation, and release APKs first. Copy the new release into
`SRC/VibeCode.Desktop/Assets/vibecode-mobile.apk` before compiling this host to test desktop APK generation.

```powershell
dotnet build SRC/tests/VibeCode.PhoneAndroidFixture --artifacts-path artifacts/final-phone-desktop-build -t:Rebuild
& artifacts/final-phone-desktop-build/bin/VibeCode.PhoneAndroidFixture/debug/VibeCode.PhoneAndroidFixture.exe artifacts/android-verification/dotnet-fixture
```

Use a disposable API 26+ Android emulator. Install the debug and androidTest APKs, then run:

```powershell
$pin = (Get-Content artifacts/android-verification/dotnet-fixture/fixture.json | ConvertFrom-Json).fingerprint
adb -s emulator-5580 shell am instrument -w -e dotnetPin $pin -e class com.vibecode.mobile.DotNetBridgeFlowTest com.vibecode.mobile.debug.test/androidx.test.runner.AndroidJUnitRunner
```

The emulator reaches Windows loopback through `10.0.2.2`. Start a fresh host for each test run: successful
pairing consumes the test code. Synthetic code `123456` is deliberately known and never touches live pairing.
The Android test covers actual signed reads, UTF-8 prompt dispatch into the fake adapter, model/effort/mode,
permission/question/plan responses, queue cancellation, stop, rename, forbidden routes, restart and revocation.
Valid new-chat creation, undo, provider execution, hardware-backed release enrollment, and physical biometric
authentication are not proven by this fixture.

Use a rebuild after copying the template: a copied APK may retain a timestamp older than an existing desktop
assembly, causing an incremental build to retain its previous embedded APK. Check `embedded-template-sha256.txt`
against the release/template SHA-256 after `/fixture/build-apk`.

After the Android test has unpaired, pinned HTTPS GET `/fixture/build-apk` on the control port calls the actual
desktop `PhoneApkBuilder.Build()` twice with disposable state. It produces `personalized-first.apk` and
`personalized-update.apk` for signature and same-signer update verification. These contain disposable test
enrollment and must not be distributed as user builds. `/fixture/state`, `/fixture/restart`, and `/fixture/stop`
are the other loopback controls. Stop the host after testing. Retained data contains only synthetic fixtures.
