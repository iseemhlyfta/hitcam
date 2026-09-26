plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// Release signing comes from the environment (CI secrets); without it the release APK is signed with the debug key.
val releaseKeystore: String? = System.getenv("HITCAM_KEYSTORE")?.takeIf { it.isNotBlank() }

android {
    namespace = "io.github.hitnes.hitcam"
    compileSdk = 37

    defaultConfig {
        applicationId = "io.github.hitnes.hitcam"
        minSdk = 26
        targetSdk = 36
        versionCode = 300
        versionName = "0.3.0"
    }

    signingConfigs {
        if (releaseKeystore != null) {
            create("release") {
                storeFile = file(releaseKeystore)
                storePassword = System.getenv("HITCAM_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("HITCAM_KEY_ALIAS")
                keyPassword = System.getenv("HITCAM_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    jvmToolchain(21)
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.zxing.embedded)
    testImplementation(libs.junit)
}
