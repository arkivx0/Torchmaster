import java.util.Properties
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}
// Signing material lives OUTSIDE the source tree:
//   LANTERNLY_KEYSTORE_PROPS=/path/to/keystore.properties  (storeFile relative to it, storePassword, keyAlias, keyPassword)
val ksFile: File? = (System.getenv("LANTERNLY_KEYSTORE_PROPS") ?: findProperty("lanternly.keystoreProps") as String?)?.let { File(it) }?.takeIf { it.exists() }
val ksProps = Properties().apply { ksFile?.inputStream()?.use { load(it) } }
android {
    namespace = "io.github.arkivx0.lanternly"
    compileSdk = 35
    buildToolsVersion = "35.0.0"
    defaultConfig {
        applicationId = "io.github.arkivx0.lanternly"
        minSdk = 29
        targetSdk = 35
        versionCode = 9
        versionName = "0.1.7"
    }
    signingConfigs {
        if (ksFile != null) create("release") {
            storeFile = File(ksFile.parentFile, ksProps.getProperty("storeFile"))
            storePassword = ksProps.getProperty("storePassword")
            keyAlias = ksProps.getProperty("keyAlias")
            keyPassword = ksProps.getProperty("keyPassword")
        }
    }
    buildTypes { release { isMinifyEnabled = true; proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro"); signingConfig = signingConfigs.findByName("release") } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    lint { abortOnError = true; checkReleaseBuilds = true; warningsAsErrors = false }
    sourceSets["test"].resources.srcDir("../../spec")
}
dependencies {
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
