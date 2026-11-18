// 斗在吗 · Gradle 构建配置（单模块工程）
//
// ★ 关键点：TFLite 用 AAR 形式引入，自带 jni/arm64-v8a/libtensorflowlite_jni.so，
//   所以【不需要 NDK、不需要任何 native 编译】。
//
// ★ minSdk 为什么定 24：
//   目标设备是 Android 10（API 29）及以上，定 24 兼容性最好。
//   TFLite 2.3.0 要求 minSdk 19+，满足。
//
// ★ 镜像配置（实测本机网络）：
//   services.gradle.org 不可达 → Gradle 发行包从腾讯云镜像下载
//   google() 走 dl.google.com/dl/android/maven2（可达）
//   jcenter() 已停止服务 → 换成 mavenCentral()

// ★ 单模块工程：插件必须直接应用。
//   插件版本在 settings.gradle.kts 的 pluginManagement 里声明。
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// ★ 仓库统一在 settings.gradle.kts 的 dependencyResolutionManagement 里声明，
//   这里不要重复加，否则会与 PREFER_SETTINGS 模式冲突。
//   （插件依赖走 pluginManagement，运行时依赖走 dependencyResolutionManagement）

tasks.named("clean") {
    delete(layout.buildDirectory)
}

android {
    namespace = "com.yq.sentinel"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.yq.sentinel"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        // ★ NDK：只为了编译绑核那一个 JNI（cpu_affinity.cpp）。
        //   推理本身不需要 NDK —— MediaPipe Tasks 是 AAR，自带 native 库。
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a")
        }
        externalNativeBuild {
            cmake {
                arguments += listOf("-DANDROID_STL=c++_shared")
                cppFlags += "-O2"
            }
        }
    }

    // ★ 绑核 JNI 的独立 so：
    //   单独一个库，即使 native 编译失败也不影响主逻辑降级运行。
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
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

    sourceSets {
        getByName("main") {
            jniLibs.srcDirs("src/main/libs")
            assets.srcDirs("src/main/assets")
        }
    }

    // 保留 native 符号，方便用 simpleperf 看 TFLite 内部热点
    packaging {
        jniLibs.useLegacyPackaging = true
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    implementation("com.google.android.material:material:1.11.0")

    // ★ MediaPipe Tasks Vision —— 自带 arm64-v8a native 库，无需 NDK
    //
    //   为什么用它而不是裸 TFLite：
    //   MediaPipe 的 ssd_mobilenet_v2 模型【故意不含 NMS 与 anchor 解码】
    //   （官方导出时 add_postprocessing_op=False），裸 TFLite 跑会报
    //   "shape [1,12276,4] 与 [1,10,4] 不匹配"。
    //   Tasks 库把 anchor 生成 + box 解码 + NMS 全包了，输出即最终结果。
    //
    //   注意：tasks-vision 在【Google Maven】上，不在 Maven Central。
    //   依赖已通过 settings.gradle.kts 的 dl.google.com 仓库配置好。
    //
    //   ★ 用 1.2.0 而不是 0.10.x：
    //     0.10.x 需要额外的 tasks-vision-image-generator，
    //     且它与 tasks-vision 有重复类（checkDebugDuplicateClasses 失败）；
    //     1.2.0 把 image-generator 合并进去了，依赖更干净。
    implementation("com.google.mediapipe:tasks-vision:1.2.0")
    implementation("com.google.mediapipe:tasks-core:1.2.0")

    testImplementation("junit:junit:4.13.2")
}
