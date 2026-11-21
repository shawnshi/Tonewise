/*
 * Copyright (c) 2025 Auxio Project
 * JStringRef.cpp is part of Auxio.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */
 
#include "JStringRef.h"
#include "util.h"
#include <vector>

JStringRef::JStringRef(JNIEnv *env, jstring jString) : env(env), string(jString) {
}

JStringRef::JStringRef(JNIEnv *env, const TagLib::String string) : env(env), string(nullptr) {
    // Convert via UTF-16LE so 4-byte supplementary UTF-8 characters never hit JNI NewStringUTF (Modified UTF-8).
    TagLib::ByteVector utf16 = string.data(TagLib::String::UTF16LE);
    jsize unitCount = static_cast<jsize>(utf16.size() / 2);
    if (unitCount <= 0) {
        static const jchar empty = 0;
        this->string = env->NewString(&empty, 0);
    } else {
        const auto *raw = reinterpret_cast<const unsigned char*>(utf16.data());
        std::vector<jchar> units(static_cast<size_t>(unitCount));
        for (jsize i = 0; i < unitCount; i++) {
            size_t off = static_cast<size_t>(i) * 2;
            units[static_cast<size_t>(i)] =
                    static_cast<jchar>(raw[off] | (static_cast<jchar>(raw[off + 1]) << 8));
        }
        this->string = env->NewString(units.data(), unitCount);
    }
    throwOnJniException(env, "JStringRef::NewString");
    if (this->string == nullptr) {
        throw std::runtime_error("NewString returned null");
    }
}

JStringRef::~JStringRef() {
    if (string != nullptr) {
        env->DeleteLocalRef(string);
    }
}

TagLib::String JStringRef::copy() {
    if (string == nullptr) {
        return {};
    }
    jsize unitCount = env->GetStringLength(string);
    throwOnJniException(env, "JStringRef::GetStringLength");
    if (unitCount <= 0) {
        return {};
    }
    const jchar *chars = env->GetStringChars(string, nullptr);
    throwOnJniException(env, "JStringRef::GetStringChars");
    if (chars == nullptr) {
        return {};
    }
    TagLib::ByteVector utf16(static_cast<unsigned int>(unitCount) * 2, 0);
    for (jsize i = 0; i < unitCount; i++) {
        uint16_t ch = static_cast<uint16_t>(chars[i]);
        unsigned int off = static_cast<unsigned int>(i) * 2;
        utf16[off] = static_cast<char>(ch & 0xFF);
        utf16[off + 1] = static_cast<char>((ch >> 8) & 0xFF);
    }
    env->ReleaseStringChars(string, chars);
    return TagLib::String(utf16, TagLib::String::UTF16LE);
}

jstring& JStringRef::operator*() {
    return string;
}
