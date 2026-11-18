// Gradle 工程设置
//
// 布局：单模块工程，模块目录就是本目录（src/ 直接在根下）。
// 命令行构建：
//   gradle assembleDebug
//   gradle installDebug
//
// ★ 镜像配置（实测本机网络）：
//   services.gradle.org 不可达 → Gradle 发行包从腾讯云镜像下载
//   google() 走 dl.google.com/dl/android/maven2（可达）
//   jcenter() 已停止服务 → 换成 mavenCentral()

pluginManagement {
    repositories {
        maven { url = uri("https://dl.google.com/dl/android/maven2") }
        maven { url = uri("https://mirrors.cloud.tencent.com/nexus/repository/maven-public/") }
        gradlePluginPortal()
        mavenCentral()
    }
    // ★ 插件版本在这里声明，build.gradle.kts 里用 plugins { id(...) } 直接引用
    plugins {
        id("com.android.application") version "8.1.4"
        id("org.jetbrains.kotlin.android") version "1.9.24"
    }
}

dependencyResolutionManagement {
    repositories {
        maven { url = uri("https://dl.google.com/dl/android/maven2") }
        maven { url = uri("https://mirrors.cloud.tencent.com/nexus/repository/maven-public/") }
        mavenCentral()
    }
}

rootProject.name = "DouZaiMa"
