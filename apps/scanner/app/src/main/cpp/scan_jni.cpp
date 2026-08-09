// Android JNI wrapper around the platform-neutral AirFerry ZXing-C++ core.

#include <jni.h>
#include <android/log.h>

#include <cstdint>
#include <exception>
#include <limits>
#include <optional>
#include <vector>

#include "airferry_zxing_core.h"

#define LOG_TAG "airferry-zxing"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace {

static_assert(sizeof(jint) == sizeof(int32_t));

jbyte* PinLuminance(
    JNIEnv* env,
    jbyteArray pixels,
    jint width,
    jint height,
    jint row_stride,
    jsize* length)
{
    if (pixels == nullptr) {
        return nullptr;
    }
    const jsize len = env->GetArrayLength(pixels);
    if (length != nullptr) {
        *length = len;
    }
    const int64_t required = width > 0 && height > 0 && row_stride >= width
        ? static_cast<int64_t>(height - 1) * row_stride + width
        : -1;
    if (required < 0 || required > len) {
        return nullptr;
    }
    return env->GetByteArrayElements(pixels, nullptr);
}

jbyteArray ToJavaBytes(JNIEnv* env, const std::vector<uint8_t>& bytes)
{
    if (bytes.empty() || bytes.size() > static_cast<size_t>(std::numeric_limits<jsize>::max())) {
        return nullptr;
    }
    jbyteArray output = env->NewByteArray(static_cast<jsize>(bytes.size()));
    if (output == nullptr) {
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
        }
        return nullptr;
    }
    env->SetByteArrayRegion(
        output,
        0,
        static_cast<jsize>(bytes.size()),
        reinterpret_cast<const jbyte*>(bytes.data()));
    if (env->ExceptionCheck()) {
        env->ExceptionClear();
        return nullptr;
    }
    return output;
}

void WriteBbox(JNIEnv* env, jintArray output, const AirFerryZxing::Bbox& bbox)
{
    if (output == nullptr || env->GetArrayLength(output) < 4) {
        return;
    }
    const jint values[4] = {bbox[0], bbox[1], bbox[2], bbox[3]};
    env->SetIntArrayRegion(output, 0, 4, values);
    if (env->ExceptionCheck()) {
        env->ExceptionClear();
    }
}

}  // namespace

extern "C" {

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeY(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride)
{
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jbyteArray output = nullptr;
    try {
        auto result = AirFerryZxing::DecodeOneFull(
            reinterpret_cast<const uint8_t*>(pixels), length, width, height, row_stride);
        if (result) {
            output = ToJavaBytes(env, result->payload);
        }
    } catch (const std::exception& error) {
        LOGE("full decode error: %s", error.what());
    }
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeYTracked(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride,
    jintArray out_bbox)
{
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jbyteArray output = nullptr;
    try {
        auto result = AirFerryZxing::DecodeOneFull(
            reinterpret_cast<const uint8_t*>(pixels), length, width, height, row_stride);
        if (result) {
            output = ToJavaBytes(env, result->payload);
            if (output != nullptr) {
                WriteBbox(env, out_bbox, result->bbox);
            }
        }
    } catch (const std::exception& error) {
        LOGE("tracked full decode error: %s", error.what());
    }
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeYRegion(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride,
    jint x,
    jint y,
    jint side)
{
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jbyteArray output = nullptr;
    try {
        auto result = AirFerryZxing::DecodeOneRegion(
            reinterpret_cast<const uint8_t*>(pixels), length,
            width, height, row_stride, x, y, side);
        if (result) {
            output = ToJavaBytes(env, result->payload);
        }
    } catch (const std::exception& error) {
        LOGE("region decode error: %s", error.what());
    }
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeYRegionTracked(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride,
    jint x,
    jint y,
    jint side,
    jintArray out_bbox)
{
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jbyteArray output = nullptr;
    try {
        auto result = AirFerryZxing::DecodeOneRegion(
            reinterpret_cast<const uint8_t*>(pixels), length,
            width, height, row_stride, x, y, side);
        if (result) {
            output = ToJavaBytes(env, result->payload);
            if (output != nullptr) {
                WriteBbox(env, out_bbox, result->bbox);
            }
        }
    } catch (const std::exception& error) {
        LOGE("tracked region decode error: %s", error.what());
    }
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeMultiY(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride)
{
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jbyteArray output = nullptr;
    try {
        const auto decoded = AirFerryZxing::DecodeMultiFull(
            reinterpret_cast<const uint8_t*>(pixels), length, width, height, row_stride);
        output = ToJavaBytes(env, AirFerryZxing::PackMultiResults(decoded));
    } catch (const std::exception& error) {
        LOGE("multi decode error: %s", error.what());
    }
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

JNIEXPORT jbyteArray JNICALL
Java_com_airferry_app_scan_ZxingDecoder_decodeMultiYTracked(
    JNIEnv* env,
    jobject,
    jbyteArray y_plane,
    jint width,
    jint height,
    jint row_stride,
    jintArray hints,
    jint hint_count,
    jfloat margin_fraction)
{
    if (hints == nullptr || hint_count <= 0 || hint_count > 64 ||
        env->GetArrayLength(hints) < hint_count * 4) {
        return nullptr;
    }
    jsize length = 0;
    jbyte* pixels = PinLuminance(env, y_plane, width, height, row_stride, &length);
    if (pixels == nullptr) {
        return nullptr;
    }
    jint* hint_data = env->GetIntArrayElements(hints, nullptr);
    if (hint_data == nullptr) {
        env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
        return nullptr;
    }

    jbyteArray output = nullptr;
    try {
        const auto decoded = AirFerryZxing::DecodeMultiRegions(
            reinterpret_cast<const uint8_t*>(pixels), length, width, height, row_stride,
            reinterpret_cast<const int32_t*>(hint_data),
            static_cast<size_t>(hint_count), margin_fraction);
        output = ToJavaBytes(env, AirFerryZxing::PackMultiResults(decoded));
    } catch (const std::exception& error) {
        LOGE("tracked multi decode error: %s", error.what());
    }
    env->ReleaseIntArrayElements(hints, hint_data, JNI_ABORT);
    env->ReleaseByteArrayElements(y_plane, pixels, JNI_ABORT);
    return output;
}

}  // extern "C"
