#include <jni.h>
#include <stdlib.h>
#include <string.h>
#include "lume.h"

JNIEXPORT jint JNICALL Java_com_lume_remote_Native_audio(JNIEnv *env,jclass type,jlong id,jint kind,jobject buffer,jintArray generation){
    (void)type;if(!buffer||!generation||(*env)->GetArrayLength(env,generation)!=1)return 0;
    void *pcm=(*env)->GetDirectBufferAddress(env,buffer);jlong capacity=(*env)->GetDirectBufferCapacity(env,buffer);
    if(!pcm||capacity<19200||capacity>1024*1024)return 0;int32_t gen=0;size_t n=lume_audio((uint64_t)id,(uint8_t)kind,&gen,pcm,(size_t)capacity);
    jint value=gen;(*env)->SetIntArrayRegion(env,generation,0,1,&value);return (jint)n;
}
JNIEXPORT jboolean JNICALL Java_com_lume_remote_Native_microphone(JNIEnv *env,jclass type,jlong id,jint generation,jbyteArray data,jint size){
    (void)type;if(!data||size<4||size>19200||size>(*env)->GetArrayLength(env,data))return JNI_FALSE;
    jbyte *pcm=(*env)->GetByteArrayElements(env,data,NULL);if(!pcm)return JNI_FALSE;
    bool ok=lume_microphone((uint64_t)id,generation,(const uint8_t*)pcm,(size_t)size);(*env)->ReleaseByteArrayElements(env,data,pcm,JNI_ABORT);return ok;
}

static jbyteArray snapshot(JNIEnv *env, uint64_t handle, int error) {
    const size_t limit = 2 * 1024 * 1024;
    size_t capacity = 4096;
    uint8_t *bytes = malloc(capacity);
    if (!bytes) return NULL;
    size_t length = error==2 ? lume_pairing(handle, bytes, capacity) : error ? lume_error(bytes, capacity) : lume_state(handle, bytes, capacity);
    if (length > capacity) {
        if (length > limit) { free(bytes); return NULL; }
        free(bytes); capacity = limit; bytes = malloc(capacity);
        if (!bytes) return NULL;
        length = error==2 ? lume_pairing(handle, bytes, capacity) : error ? lume_error(bytes, capacity) : lume_state(handle, bytes, capacity);
        if (length > capacity) { free(bytes); return NULL; }
    }
    jbyteArray result = (*env)->NewByteArray(env, (jsize)length);
    if (result && length) (*env)->SetByteArrayRegion(env, result, 0, (jsize)length, (const jbyte *)bytes);
    if(error==2){volatile uint8_t *p=bytes;for(size_t i=0;i<capacity;i++)p[i]=0;}
    free(bytes); return result;
}
JNIEXPORT jlong JNICALL Java_com_lume_remote_Native_open(JNIEnv *env, jclass type, jbyteArray invitation, jbyteArray library) {
    (void)type;
    if (!invitation || !library) return 0;
    jsize a=(*env)->GetArrayLength(env,invitation), b=(*env)->GetArrayLength(env,library);
    if (a<1 || a>65536 || b>4096) return 0;
    jbyte *text=(*env)->GetByteArrayElements(env,invitation,NULL);
    jbyte *path=(*env)->GetByteArrayElements(env,library,NULL);
    if (!text || !path) { if(text)(*env)->ReleaseByteArrayElements(env,invitation,text,JNI_ABORT); if(path)(*env)->ReleaseByteArrayElements(env,library,path,JNI_ABORT); return 0; }
    uint64_t id=lume_open((const uint8_t*)text,(size_t)a,(const uint8_t*)path,(size_t)b);
    (*env)->ReleaseByteArrayElements(env,invitation,text,JNI_ABORT);(*env)->ReleaseByteArrayElements(env,library,path,JNI_ABORT);
    return (jlong)id;
}
JNIEXPORT jboolean JNICALL Java_com_lume_remote_Native_cancel(JNIEnv *env,jclass type,jlong id) {(void)env;(void)type;return lume_cancel((uint64_t)id);}
JNIEXPORT jboolean JNICALL Java_com_lume_remote_Native_close(JNIEnv *env,jclass type,jlong id) {(void)env;(void)type;return lume_close((uint64_t)id);}
JNIEXPORT jbyteArray JNICALL Java_com_lume_remote_Native_state(JNIEnv *env,jclass type,jlong id) {(void)type;return snapshot(env,(uint64_t)id,0);}
JNIEXPORT jbyteArray JNICALL Java_com_lume_remote_Native_pairing(JNIEnv *env,jclass type,jlong id) {(void)type;return snapshot(env,(uint64_t)id,2);}
JNIEXPORT jbyteArray JNICALL Java_com_lume_remote_Native_error(JNIEnv *env,jclass type) {(void)type;return snapshot(env,0,1);}
JNIEXPORT jboolean JNICALL Java_com_lume_remote_Native_action(JNIEnv *env,jclass type,jlong id,jbyteArray json) {
    (void)type; if(!json)return JNI_FALSE;jsize length=(*env)->GetArrayLength(env,json);if(length<1 || length>300000)return JNI_FALSE;
    jbyte *data=(*env)->GetByteArrayElements(env,json,NULL);if(!data)return JNI_FALSE;
    bool ok=lume_action((uint64_t)id,(const uint8_t*)data,(size_t)length);
    (*env)->ReleaseByteArrayElements(env,json,data,JNI_ABORT);return ok;
}
JNIEXPORT jlong JNICALL Java_com_lume_remote_Native_frame(JNIEnv *env,jclass type,jlong id,jint previous,jobject buffer,jintArray metadata) {
    (void)type;if(!metadata || (*env)->GetArrayLength(env,metadata)!=4)return 0;
    void *data=buffer?(*env)->GetDirectBufferAddress(env,buffer):NULL;
    jlong capacity=buffer?(*env)->GetDirectBufferCapacity(env,buffer):0;
    if(capacity<0 || capacity>144*1024*1024 || (capacity && !data))return 0;
    LumeFrameInfo info={0};size_t required=lume_frame((uint64_t)id,previous,&info,(uint8_t*)data,(size_t)capacity);
    jint out[4]={(jint)info.width,(jint)info.height,(jint)info.epoch,(jint)info.sequence};
    (*env)->SetIntArrayRegion(env,metadata,0,4,out);return (jlong)required;
}
