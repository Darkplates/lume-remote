#ifndef LUME_BRIDGE_H
#define LUME_BRIDGE_H
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct LumeFrameInfo { uint32_t width, height; int32_t epoch, sequence; } LumeFrameInfo;
size_t lume_error(uint8_t *output, size_t capacity);
uint64_t lume_open(const uint8_t *invitation, size_t length, const uint8_t *library, size_t library_length);
bool lume_cancel(uint64_t handle);
/* Call close on a worker thread. It cancels and joins all network work. */
bool lume_close(uint64_t handle);
/* Buffer functions return required bytes; no partial copy occurs. JSON is UTF-8, without NUL. */
size_t lume_state(uint64_t handle, uint8_t *output, size_t capacity);
/* Display actions: {"type":"list_monitors"}, {"type":"select_monitor","id":"..."}.
   State includes monitors, monitor_pending/status and input_ready. Input must use
   the epoch of the image actually presented, never a newer state snapshot epoch. */
size_t lume_media(uint64_t handle, uint8_t *output, size_t capacity);
/* Private credential: store using platform protection; never log or display. */
size_t lume_pairing(uint64_t handle, uint8_t *output, size_t capacity);
size_t lume_frame(uint64_t handle, int32_t last_sequence, LumeFrameInfo *info, uint8_t *rgba, size_t capacity);
bool lume_action(uint64_t handle, const uint8_t *json, size_t length);
/* Non-blocking PCM16 stereo, 48000 Hz. Output must hold 19200 bytes. 0 = no block.
   kind is 20 (system audio) or 21 (voice). Always check current media state before
   playing; release platform devices when disabled, disconnected or backgrounded. */
size_t lume_audio(uint64_t handle, uint8_t kind, int32_t *generation, uint8_t *pcm, size_t capacity);
/* Accepted only after remote consent, for the current voice generation. */
bool lume_microphone(uint64_t handle, int32_t generation, const uint8_t *pcm, size_t length);
#ifdef __cplusplus
}
#endif
#endif
