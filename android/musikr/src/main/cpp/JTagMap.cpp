/*
 * Copyright (c) 2024 Auxio Project
 * JTagMap.cpp is part of Auxio.
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
 
#include "JTagMap.h"

#include "JStringRef.h"
#include "util.h"

JTagMap::JTagMap(JNIEnv *env) : env(env) {
    auto jTagMapClass = std::make_unique < JClassRef
            > (env, "org/oxycblt/musikr/metadata/NativeTagMap");
    auto jTagMapInitMethod = jTagMapClass->method("<init>", "()V");
    jobject mapObj = env->NewObject(**jTagMapClass, jTagMapInitMethod);
    throwOnJniException(env, "JTagMap::NativeTagMap.<init>");
    if (mapObj == nullptr) {
        throw std::runtime_error("Failed to instantiate NativeTagMap");
    }
    jTagMap = std::make_unique<JObjectRef>(env, mapObj);
    jTagMapAddIdSingleMethod = jTagMapClass->method("addID",
            "(Ljava/lang/String;Ljava/lang/String;)V");
    jTagMapAddIdListMethod = jTagMapClass->method("addID",
            "(Ljava/lang/String;Ljava/util/List;)V");
    jTagMapAddCustomSingleMethod = jTagMapClass->method("addCustom",
            "(Ljava/lang/String;Ljava/lang/String;)V");
    jTagMapAddCustomListMethod = jTagMapClass->method("addCustom",
            "(Ljava/lang/String;Ljava/util/List;)V");
    jTagMapAddCombinedSingleMethod = jTagMapClass->method("addCombined",
            "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V");
    jTagMapAddCombinedListMethod = jTagMapClass->method("addCombined",
            "(Ljava/lang/String;Ljava/lang/String;Ljava/util/List;)V");
    jTagMapGetObjectMethod = jTagMapClass->method("getObject",
            "()Ljava/util/Map;");

    jArrayListClass = std::make_unique < JClassRef
            > (env, "java/util/ArrayList");
    jArrayListInitMethod = jArrayListClass->method("<init>", "()V");
    jArrayListAddMethod = jArrayListClass->method("add",
            "(Ljava/lang/Object;)Z");
}

void JTagMap::add_id(const TagLib::String id, const TagLib::String value) {
    JStringRef jId { env, id };
    JStringRef jValue { env, value };
    env->CallVoidMethod(**jTagMap, jTagMapAddIdSingleMethod, *jId, *jValue);
    throwOnJniException(env, "JTagMap::addID(single)");
}

void JTagMap::add_id(const TagLib::String id, const TagLib::StringList values) {
    JStringRef jId { env, id };
    jobject listObj = env->NewObject(**jArrayListClass, jArrayListInitMethod);
    throwOnJniException(env, "JTagMap::ArrayList.<init>");
    if (listObj == nullptr) {
        throw std::runtime_error("Failed to allocate ArrayList");
    }
    JObjectRef jValues { env, listObj };
    for (auto &value : values) {
        if (env->PushLocalFrame(16) != 0) {
            throwOnJniException(env, "JTagMap::PushLocalFrame");
            throw std::runtime_error("JNI local frame overflow");
        }
        try {
            JStringRef jValue { env, value };
            env->CallBooleanMethod(*jValues, jArrayListAddMethod, *jValue);
            throwOnJniException(env, "JTagMap::ArrayList.add");
        } catch (...) {
            env->PopLocalFrame(nullptr);
            throw;
        }
        env->PopLocalFrame(nullptr);
    }
    env->CallVoidMethod(**jTagMap, jTagMapAddIdListMethod, *jId, *jValues);
    throwOnJniException(env, "JTagMap::addID(list)");
}

void JTagMap::add_custom(const TagLib::String description,
        const TagLib::String value) {
    JStringRef jDescription { env, description };
    JStringRef jValue { env, value };
    env->CallVoidMethod(**jTagMap, jTagMapAddCustomSingleMethod, *jDescription,
            *jValue);
    throwOnJniException(env, "JTagMap::addCustom(single)");
}

void JTagMap::add_custom(const TagLib::String description,
        const TagLib::StringList values) {
    JStringRef jDescription { env, description };
    jobject listObj = env->NewObject(**jArrayListClass, jArrayListInitMethod);
    throwOnJniException(env, "JTagMap::ArrayList.<init>");
    if (listObj == nullptr) {
        throw std::runtime_error("Failed to allocate ArrayList");
    }
    JObjectRef jValues { env, listObj };
    for (auto &value : values) {
        if (env->PushLocalFrame(16) != 0) {
            throwOnJniException(env, "JTagMap::PushLocalFrame");
            throw std::runtime_error("JNI local frame overflow");
        }
        try {
            JStringRef jValue { env, value };
            env->CallBooleanMethod(*jValues, jArrayListAddMethod, *jValue);
            throwOnJniException(env, "JTagMap::ArrayList.add");
        } catch (...) {
            env->PopLocalFrame(nullptr);
            throw;
        }
        env->PopLocalFrame(nullptr);
    }
    env->CallVoidMethod(**jTagMap, jTagMapAddCustomListMethod, *jDescription,
            *jValues);
    throwOnJniException(env, "JTagMap::addCustom(list)");
}

void JTagMap::add_combined(const TagLib::String id,
        const TagLib::String description, const TagLib::String value) {
    JStringRef jId { env, id };
    JStringRef jDescription { env, description };
    JStringRef jValue { env, value };
    env->CallVoidMethod(**jTagMap, jTagMapAddCombinedSingleMethod, *jId,
            *jDescription, *jValue);
    throwOnJniException(env, "JTagMap::addCombined(single)");
}

void JTagMap::add_combined(const TagLib::String id,
        const TagLib::String description, const TagLib::StringList values) {
    JStringRef jId { env, id };
    JStringRef jDescription { env, description };
    jobject listObj = env->NewObject(**jArrayListClass, jArrayListInitMethod);
    throwOnJniException(env, "JTagMap::ArrayList.<init>");
    if (listObj == nullptr) {
        throw std::runtime_error("Failed to allocate ArrayList");
    }
    JObjectRef jValues { env, listObj };
    for (auto &value : values) {
        if (env->PushLocalFrame(16) != 0) {
            throwOnJniException(env, "JTagMap::PushLocalFrame");
            throw std::runtime_error("JNI local frame overflow");
        }
        try {
            JStringRef jValue { env, value };
            env->CallBooleanMethod(*jValues, jArrayListAddMethod, *jValue);
            throwOnJniException(env, "JTagMap::ArrayList.add");
        } catch (...) {
            env->PopLocalFrame(nullptr);
            throw;
        }
        env->PopLocalFrame(nullptr);
    }
    env->CallVoidMethod(**jTagMap, jTagMapAddCombinedListMethod, *jId,
            *jDescription, *jValues);
    throwOnJniException(env, "JTagMap::addCombined(list)");
}

std::unique_ptr<JObjectRef> JTagMap::getObject() {
    jobject mapObj = env->CallObjectMethod(**jTagMap, jTagMapGetObjectMethod);
    throwOnJniException(env, "JTagMap::getObject");
    return std::make_unique<JObjectRef>(env, mapObj);
}
