// MidiSharp CLAP win32 GUI fixture — clean-room, well-behaved.
// Based on the official CLAP plugin template (free-audio/clap, MIT licence).
// Adds a clap.gui extension that creates a WS_CHILD STATIC window in set_parent() and returns immediately
// (no blocking, no message loop inside the plugin — the host pumps messages).

#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <assert.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <clap/clap.h>
#include <clap/ext/gui.h>

// ─── descriptor ──────────────────────────────────────────────────────────────

static const clap_plugin_descriptor_t s_desc = {
    .clap_version = CLAP_VERSION_INIT,
    .id           = "midisharp.test.gui",
    .name         = "MidiSharp Test GUI",
    .vendor       = "MidiSharp",
    .url          = "",
    .manual_url   = "",
    .support_url  = "",
    .version      = "1.0.0",
    .description  = "Clean-room CLAP win32 GUI fixture for MidiSharp hosting tests.",
    .features     = (const char *[]){
        CLAP_PLUGIN_FEATURE_AUDIO_EFFECT,
        CLAP_PLUGIN_FEATURE_STEREO,
        NULL
    },
};

// ─── plugin data ─────────────────────────────────────────────────────────────

typedef struct {
    clap_plugin_t  plugin;
    const clap_host_t *host;
    HWND child_hwnd;   // set by gui.set_parent, cleared by gui.destroy
} fixture_t;

// ─── audio-ports extension ───────────────────────────────────────────────────

static uint32_t ap_count(const clap_plugin_t *p, bool is_input) { return 1; } /* one main stereo port on each side */

static bool ap_get(const clap_plugin_t *p, uint32_t index, bool is_input, clap_audio_port_info_t *info) {
    if (index != 0) return false;
    info->id           = 0;
    info->channel_count = 2;
    info->flags        = CLAP_AUDIO_PORT_IS_MAIN;
    info->port_type    = CLAP_PORT_STEREO;
    info->in_place_pair = CLAP_INVALID_ID;
    snprintf(info->name, sizeof(info->name), "Main");
    return true;
}

static const clap_plugin_audio_ports_t s_audio_ports = {
    .count = ap_count,
    .get   = ap_get,
};

// ─── gui extension ────────────────────────────────────────────────────────────

static bool gui_is_api_supported(const clap_plugin_t *p, const char *api, bool is_floating) {
    return strcmp(api, CLAP_WINDOW_API_WIN32) == 0 && !is_floating;
}

static bool gui_get_preferred_api(const clap_plugin_t *p, const char **api, bool *is_floating) {
    *api        = CLAP_WINDOW_API_WIN32;
    *is_floating = false;
    return true;
}

static bool gui_create(const clap_plugin_t *p, const char *api, bool is_floating) {
    // Only win32 embedded is supported; no window resource allocated yet (that happens in set_parent).
    return strcmp(api, CLAP_WINDOW_API_WIN32) == 0 && !is_floating;
}

static void gui_destroy(const clap_plugin_t *p) {
    fixture_t *f = p->plugin_data;
    if (f->child_hwnd) {
        DestroyWindow(f->child_hwnd);
        f->child_hwnd = NULL;
    }
}

static bool gui_set_scale(const clap_plugin_t *p, double scale) { return true; }

static bool gui_get_size(const clap_plugin_t *p, uint32_t *w, uint32_t *h) {
    *w = 320;
    *h = 240;
    return true;
}

static bool gui_can_resize(const clap_plugin_t *p) { return false; }

static bool gui_get_resize_hints(const clap_plugin_t *p, clap_gui_resize_hints_t *hints) {
    return false;
}

static bool gui_adjust_size(const clap_plugin_t *p, uint32_t *w, uint32_t *h) { return true; }

static bool gui_set_size(const clap_plugin_t *p, uint32_t w, uint32_t h) { return false; }

static bool gui_set_parent(const clap_plugin_t *p, const clap_window_t *window) {
    fixture_t *f = p->plugin_data;
    // Destroy any existing child window before creating a new one.
    if (f->child_hwnd) {
        DestroyWindow(f->child_hwnd);
        f->child_hwnd = NULL;
    }
    HWND parent = (HWND)window->win32;
    // Create a simple STATIC child — immediately visible, returns at once (no blocking).
    HWND child = CreateWindowExW(
        0,
        L"STATIC",
        L"MidiSharp CLAP GUI Fixture",
        WS_CHILD | WS_VISIBLE,
        0, 0, 320, 240,
        parent,
        NULL,
        NULL,
        NULL
    );
    if (!child) return false;
    f->child_hwnd = child;
    return true;
}

static bool gui_set_transient(const clap_plugin_t *p, const clap_window_t *window) { return true; }
static void gui_suggest_title(const clap_plugin_t *p, const char *title) {}
static bool gui_show(const clap_plugin_t *p) { return true; }  // child is already WS_VISIBLE
static bool gui_hide(const clap_plugin_t *p) { return true; }

static const clap_plugin_gui_t s_gui = {
    .is_api_supported  = gui_is_api_supported,
    .get_preferred_api = gui_get_preferred_api,
    .create            = gui_create,
    .destroy           = gui_destroy,
    .set_scale         = gui_set_scale,
    .get_size          = gui_get_size,
    .can_resize        = gui_can_resize,
    .get_resize_hints  = gui_get_resize_hints,
    .adjust_size       = gui_adjust_size,
    .set_size          = gui_set_size,
    .set_parent        = gui_set_parent,
    .set_transient     = gui_set_transient,
    .suggest_title     = gui_suggest_title,
    .show              = gui_show,
    .hide              = gui_hide,
};

// ─── plugin vtable ────────────────────────────────────────────────────────────

static bool plug_init(const clap_plugin_t *p) { return true; }

static void plug_destroy(const clap_plugin_t *p) {
    fixture_t *f = p->plugin_data;
    free(f);
}

static bool plug_activate(const clap_plugin_t *p, double sr, uint32_t min_f, uint32_t max_f) { return true; }
static void plug_deactivate(const clap_plugin_t *p) {}
static bool plug_start_processing(const clap_plugin_t *p) { return true; }
static void plug_stop_processing(const clap_plugin_t *p) {}
static void plug_reset(const clap_plugin_t *p) {}

static clap_process_status plug_process(const clap_plugin_t *p, const clap_process_t *proc) {
    // Pass-through stereo (copy in → out).
    const uint32_t n = proc->frames_count;
    if (proc->audio_inputs_count >= 1 && proc->audio_outputs_count >= 1) {
        for (uint32_t c = 0; c < 2; c++) {
            const float *in  = proc->audio_inputs[0].data32[c];
            float       *out = proc->audio_outputs[0].data32[c];
            for (uint32_t i = 0; i < n; i++) out[i] = in[i];
        }
    }
    return CLAP_PROCESS_CONTINUE;
}

static const void *plug_get_extension(const clap_plugin_t *p, const char *id) {
    if (strcmp(id, CLAP_EXT_GUI)         == 0) return &s_gui;
    if (strcmp(id, CLAP_EXT_AUDIO_PORTS) == 0) return &s_audio_ports;
    return NULL;
}

static void plug_on_main_thread(const clap_plugin_t *p) {}

static clap_plugin_t *fixture_create(const clap_host_t *host) {
    fixture_t *f = calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->host = host;
    f->child_hwnd = NULL;
    f->plugin.desc         = &s_desc;
    f->plugin.plugin_data  = f;
    f->plugin.init         = plug_init;
    f->plugin.destroy      = plug_destroy;
    f->plugin.activate     = plug_activate;
    f->plugin.deactivate   = plug_deactivate;
    f->plugin.start_processing = plug_start_processing;
    f->plugin.stop_processing  = plug_stop_processing;
    f->plugin.reset        = plug_reset;
    f->plugin.process      = plug_process;
    f->plugin.get_extension = plug_get_extension;
    f->plugin.on_main_thread = plug_on_main_thread;
    return &f->plugin;
}

// ─── factory ─────────────────────────────────────────────────────────────────

static uint32_t factory_count(const clap_plugin_factory_t *f) { return 1; }

static const clap_plugin_descriptor_t *factory_descriptor(const clap_plugin_factory_t *f, uint32_t i) {
    return i == 0 ? &s_desc : NULL;
}

static const clap_plugin_t *factory_create(const clap_plugin_factory_t *factory,
                                            const clap_host_t *host,
                                            const char *id) {
    if (!clap_version_is_compatible(host->clap_version)) return NULL;
    if (strcmp(id, s_desc.id) == 0) return fixture_create(host);
    return NULL;
}

static const clap_plugin_factory_t s_factory = {
    .get_plugin_count      = factory_count,
    .get_plugin_descriptor = factory_descriptor,
    .create_plugin         = factory_create,
};

// ─── entry ────────────────────────────────────────────────────────────────────

static int g_init_count = 0;

static bool entry_init(const char *path) {
    if (++g_init_count == 1) { /* one-time init if needed */ }
    return true;
}

static void entry_deinit(void) {
    if (--g_init_count == 0) { /* one-time cleanup if needed */ }
}

static const void *entry_get_factory(const char *id) {
    if (g_init_count <= 0) return NULL;
    if (strcmp(id, CLAP_PLUGIN_FACTORY_ID) == 0) return &s_factory;
    return NULL;
}

CLAP_EXPORT const clap_plugin_entry_t clap_entry = {
    .clap_version = CLAP_VERSION_INIT,
    .init         = entry_init,
    .deinit       = entry_deinit,
    .get_factory  = entry_get_factory,
};
