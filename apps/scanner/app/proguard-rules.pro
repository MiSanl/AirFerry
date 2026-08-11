# =============================================================================
# R8 / ProGuard rules — AirFerry Android receiver
#
# Release builds run R8 (isMinifyEnabled + isShrinkResources). These rules keep
# the JNI boundary and reflection-sensitive classes intact. Most framework
# deps (Compose / CameraX / androidx) ship their own consumer rules, so only
# the app-specific reflection / native surfaces are kept here.
# =============================================================================

# --- JNI native method signatures -------------------------------------------
# R8 must NOT rename native methods, or the C/C++ side (libtransfer_engine.so,
# libairferry_zxing.so) cannot bind them. `-keepclasseswithmembernames` keeps
# the method *names* while still allowing the class itself to be renamed.
-keepclasseswithmembernames class * {
    native <methods>;
}

# --- App JNI bridge classes -------------------------------------------------
# Keep the whole bridge objects (Kotlin `object` singletons). R8 could prune
# these if it believes they are unreferenced; they are loaded via
# System.loadLibrary + external funs, so pin the class + method names.
-keep class com.airferry.app.nativelib.** { *; }
-keep class com.airferry.app.scan.** {
    *;
}

# --- org.json reflection ----------------------------------------------------
# org.json (org.json:json) relies on reflection / class-for-name in places.
-keep class org.json.** { *; }

# --- Remove debug logging from release (optional cleanliness) ---------------
-assumenosideeffects class android.util.Log {
    public static int d(...);
    public static int v(...);
}
