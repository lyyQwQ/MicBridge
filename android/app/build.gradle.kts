
plugins {
    id("com.android.application")
    id("dev.flutter.flutter-gradle-plugin")
}
val releaseKeystore = System.getenv("MICBRIDGE_RELEASE_KEYSTORE")
val releaseStorePassword = System.getenv("MICBRIDGE_RELEASE_STORE_PASSWORD")
val releaseKeyAlias = System.getenv("MICBRIDGE_RELEASE_KEY_ALIAS")
val releaseKeyPassword = System.getenv("MICBRIDGE_RELEASE_KEY_PASSWORD")
val releaseSigningReady = listOf(releaseKeystore, releaseStorePassword, releaseKeyAlias, releaseKeyPassword).all { !it.isNullOrBlank() }
val releaseRequested = gradle.startParameter.taskNames.any { it.contains("release", ignoreCase = true) }

if (releaseRequested && !releaseSigningReady) {
    throw GradleException("Release builds require MICBRIDGE_RELEASE_KEYSTORE, MICBRIDGE_RELEASE_STORE_PASSWORD, MICBRIDGE_RELEASE_KEY_ALIAS, and MICBRIDGE_RELEASE_KEY_PASSWORD")
}

android {
    namespace = "io.github.lyyqwq.micbridge"
    compileSdk = flutter.compileSdkVersion

    signingConfigs {
        create("release") {
            if (releaseSigningReady) {
                storeFile = file(releaseKeystore!!)
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "io.github.lyyqwq.micbridge"
        minSdk = 26
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.getByName("release")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}

