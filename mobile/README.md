# Android companion

The companion connects to a desktop through a paired, authenticated TLS connection. Each installation stores its own credentials and locks access with the device's supported authentication controls.

## Build a template for the desktop

Install a compatible JDK and Android SDK platform 36. Select the JDK using `JAVA_HOME` and the SDK using `ANDROID_HOME` or an untracked local `local.properties` file.

From the repository root:

```powershell
powershell -NoProfile -File scripts/Build-Mobile.ps1
```

This builds an unsigned release APK and copies it to the desktop's ignored build assets. Template builds ignore local signing properties. The desktop signs personalized copies with an identity generated for that desktop. The source enrollment asset must remain exactly 2,048 bytes, space-padded, with the single boolean `configured` set to `false`. The publish script validates the template before embedding it.

## Sign an installable generic app

Create a signing key and an untracked `VibeCodeMobile/keystore.properties` file with your own values:

```properties
storeFile=/path/to/your/release-key.jks
storePassword=your-local-password
keyAlias=your-key-alias
keyPassword=your-local-password
```

Then run `gradlew.bat :app:assembleRelease` in `VibeCodeMobile`. Without that local signing configuration, the release APK is unsigned and serves as a template for the desktop packager. Never commit signing keys or generated enrollment data.
