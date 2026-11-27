plugins {
    id("com.android.application") version "9.4.1"
    id("com.google.devtools.ksp") version "2.3.6"
    id("org.jetbrains.kotlin.plugin.serialization") version "2.2.10"
    id("androidx.room") version "2.8.5"
}

android {
    namespace = "ru.bimhouse.crg.field"
    compileSdk = 36

    defaultConfig {
        applicationId = "ru.bimhouse.crg.field"
        minSdk = 26
        targetSdk = 36
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

room {
    schemaDirectory("$projectDir/schemas")
}

val submissionCases = rootProject.file("../server/BHS.CRG.Modules.Work/Submission/SubmissionCases.json")

androidComponents {
    onVariants { variant ->
        variant.hostTests.values.forEach { hostTest ->
            hostTest.configureTestTask { test ->
                test.inputs.file(submissionCases)
                test.systemProperty("submissionCasesFile", submissionCases.absolutePath)
            }
        }
    }
}

dependencies {
    implementation("androidx.room:room-runtime:2.8.5")
    ksp("androidx.room:room-compiler:2.8.5")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")

    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.room:room-testing:2.8.5")
}
