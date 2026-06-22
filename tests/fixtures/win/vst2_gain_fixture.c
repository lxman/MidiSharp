/* Clean-room VST2 gain effect with a Win32 (HWND) editor child. No Steinberg SDK headers — the AEffect
 * ABI is transcribed from MidiSharp's host-side Vst2Abi.cs. Name "MidiSharp VST2 Gain", uniqueID 'MsG2',
 * one "Gain" param (0..1 normalized -> x0..x2), 300x200 editor. Built by build-fixtures.ps1 (clang). */
#include <windows.h>
#include <string.h>
#include <stdint.h>

typedef struct AEffect AEffect;
typedef intptr_t (*DispatcherFn)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
typedef void (*SetParamFn)(AEffect*, int32_t, float);
typedef float (*GetParamFn)(AEffect*, int32_t);
typedef void (*ProcessReplacingFn)(AEffect*, float**, float**, int32_t);
typedef intptr_t (*AudioMasterFn)(AEffect*, int32_t, int32_t, intptr_t, void*, float);

struct AEffect {
    int32_t magic;
    DispatcherFn dispatcher;
    void* process;
    SetParamFn setParameter;
    GetParamFn getParameter;
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t resvd1, resvd2;
    int32_t initialDelay, realQualities, offQualities;
    float ioRatio;
    void* object; void* user;
    int32_t uniqueID, version;
    ProcessReplacingFn processReplacing;
    void* processDoubleReplacing;
    char future[56];
};

#define EFFECT_MAGIC 0x56737450 /* 'VstP' */
#define effClose 1
#define effGetParamName 8
#define effEditGetRect 13
#define effEditOpen 14
#define effEditClose 15
#define effEditIdle 19
#define effGetEffectName 45
#define effGetVendorString 47
#define effGetProductString 48
#define effGetVstVersion 58
#define effFlagsHasEditor 1
#define effFlagsCanReplacing 16

typedef struct { int16_t top, left, bottom, right; } ERect;

static float g_gain = 0.5f;                 /* normalized; 0.5 -> x1 (range x0..x2) */
static ERect g_rect = { 0, 0, 200, 300 };   /* 300 wide x 200 tall */
static HWND g_child = NULL;
static AEffect g_effect;

static intptr_t dispatcher(AEffect* e, int32_t op, int32_t idx, intptr_t value, void* ptr, float opt) {
    (void)e; (void)idx; (void)value; (void)opt;
    switch (op) {
        case effGetEffectName:    strcpy((char*)ptr, "MidiSharp VST2 Gain"); return 1;
        case effGetVendorString:  strcpy((char*)ptr, "MidiSharp"); return 1;
        case effGetProductString: strcpy((char*)ptr, "MidiSharp VST2 Gain"); return 1;
        case effGetParamName:     strcpy((char*)ptr, "Gain"); return 1;
        case effGetVstVersion:    return 2400;
        case effEditGetRect:      *(ERect**)ptr = &g_rect; return 1;
        case effEditOpen: {
            g_child = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                0, 0, g_rect.right - g_rect.left, g_rect.bottom - g_rect.top,
                (HWND)ptr, NULL, NULL, NULL);
            return g_child ? 1 : 0;
        }
        case effEditClose: if (g_child) { DestroyWindow(g_child); g_child = NULL; } return 1;
        case effEditIdle:  return 1;
        case effClose:     return 1;
        default:           return 0;
    }
}
static void setParameter(AEffect* e, int32_t idx, float val) { (void)e; if (idx == 0) g_gain = val; }
static float getParameter(AEffect* e, int32_t idx) { (void)e; return idx == 0 ? g_gain : 0.0f; }
static void processReplacing(AEffect* e, float** in, float** out, int32_t frames) {
    (void)e;
    float g = g_gain * 2.0f;
    for (int c = 0; c < 2; c++)
        for (int32_t i = 0; i < frames; i++)
            out[c][i] = in[c][i] * g;
}

__declspec(dllexport) AEffect* VSTPluginMain(AudioMasterFn host) {
    (void)host;
    memset(&g_effect, 0, sizeof(g_effect));
    g_effect.magic = EFFECT_MAGIC;
    g_effect.dispatcher = dispatcher;
    g_effect.setParameter = setParameter;
    g_effect.getParameter = getParameter;
    g_effect.numParams = 1;
    g_effect.numInputs = 2;
    g_effect.numOutputs = 2;
    g_effect.flags = effFlagsHasEditor | effFlagsCanReplacing;
    g_effect.uniqueID = 0x4D734732; /* 'MsG2' */
    g_effect.version = 1;
    g_effect.processReplacing = processReplacing;
    return &g_effect;
}
