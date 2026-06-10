plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.callspire.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.callspire.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables {
            useSupportLibrary = true
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }
}

dependencies {
    val bom = platform("androidx.compose:compose-bom:2025.02.00")

    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.0")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.0")
    implementation("androidx.navigation:navigation-compose:2.8.9")

    implementation(bom)
    androidTestImplementation(bom)

    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.compose.material3:material3:1.3.2")
    implementation("androidx.compose.material:material-icons-extended")

    implementation("androidx.datastore:datastore-preferences:1.1.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.1")

    // Linphone SDK (VoIP / SIP)
    implementation("org.linphone.no-video:linphone-sdk-android:5.4.+")

    // Linphone audio focus helper (recommended by Linphone SDK docs)
    implementation("androidx.media:media:1.2.0")
}
