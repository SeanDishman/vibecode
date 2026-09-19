# R8 rules for VibeCode Mobile.
#
# The app has no reflection, no serialisation library, and no JNI: every wire payload is parsed by hand through
# org.json into plain data classes. That means almost nothing needs to be kept, and the defaults from
# proguard-android-optimize.txt plus the AAPT-generated rules already cover Compose and AndroidX.
#
# Deliberately NOT kept: the data classes in com.vibecode.mobile.data. Their field names never appear on the wire
# (the JSON keys are string literals in the from() factories), so R8 is free to rename them.

# Strip the log calls that a release build should never be making. Nothing in this app logs a token, but the
# transcript it holds is someone's source code, so the safe default is that none of it can reach logcat at all.
-assumenosideeffects class android.util.Log {
    public static int v(...);
    public static int d(...);
    public static int i(...);
    public static int w(...);
    public static int e(...);
    public static int wtf(...);
}

# Keep the line numbers that make a real crash report readable, but throw away the source file name so the
# mapping is only useful with the retrace file that stays on the build machine.
-keepattributes LineNumberTable,SourceFile
-renamesourcefileattribute SourceFile

# androidx.biometric reaches for framework classes that do not exist on older API levels; R8 should not treat
# those as missing-class errors.
-dontwarn android.hardware.biometrics.**
-dontwarn android.security.identity.**
