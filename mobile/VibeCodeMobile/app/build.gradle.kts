import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

// Release signing. keystore.properties is gitignored; without it the release build still runs and simply comes
// out unsigned, which is better than a build that fails on a machine that has never seen the key.
val buildTemplate = providers.gradleProperty("vibecodeTemplate").orNull == "true"
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (!buildTemplate && file.exists()) file.inputStream().use { load(it) }
}

android {
    namespace = "com.vibecode.mobile"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.vibecode.mobile"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
    }

    signingConfigs {
        if (keystoreProperties.containsKey("storeFile")) {
            create("release") {
                storeFile = rootProject.file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")
                // v1 is dead weight above API 24 and its per-entry signatures are the weakest link in an APK
                // that has any. v3 adds the rotation proof that lets this key be replaced later without
                // orphaning every installed copy.
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        release {
            // R8 in full mode: shrinks, optimises and renames. Beyond the size win it strips the app of the
            // readable class/method names that make a decompiled APK trivial to navigate, and prunes unreachable
            // code so there is less of it to inspect at all.
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (keystoreProperties.containsKey("storeFile")) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
        debug {
            // Keeps the debug build installable next to a release one without either overwriting the other's
            // pairing - two copies of this app pointing at the same PC would otherwise share nothing but confusion.
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        // Not on by default in AGP 9. Needed for BuildConfig.DEBUG, which decides whether FLAG_SECURE is applied.
        buildConfig = true
    }

    packaging {
        resources { excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    }

    androidResources {
        // The enrolment blob must land in the APK STORED, at a fixed size, so the desktop can personalise a built
        // APK by overwriting those bytes in place instead of rebuilding the zip. A compressed entry would change
        // length the moment its contents changed, which would move every following entry.
        noCompress += "vcenroll"
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)
    implementation(libs.androidx.material.icons.extended)
    implementation(libs.androidx.biometric)
}
