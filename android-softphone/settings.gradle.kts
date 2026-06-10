pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        maven {
            name = "linphone.org maven repository"
            url = uri("https://download.linphone.org/maven_repository")
            content {
                includeGroup("org.linphone")
                includeGroupByRegex("org\\.linphone\\..*")
            }
        }
    }
}

rootProject.name = "CallspireAndroid"
include(":app")
