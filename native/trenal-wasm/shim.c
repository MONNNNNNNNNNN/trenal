// trenal-wasm: a tiny C API over WAMR for running WASI command-line programs in-process.
// iOS apps can't exec binaries or JIT, so .wasm tools run on WAMR's fast interpreter with
// stdio wired to pipes the host owns (the terminal) and host directories pre-opened.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include "wasm_export.h"

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

typedef struct trenal_wasm {
    uint8_t *bytes;
    uint32_t size;
    wasm_module_t module;
    wasm_module_inst_t _Atomic inst;
} trenal_wasm;

static pthread_once_t init_once = PTHREAD_ONCE_INIT;
static int init_ok = 0;

static void do_init(void)
{
    RuntimeInitArgs args;
    memset(&args, 0, sizeof(args));
    args.mem_alloc_type = Alloc_With_System_Allocator;
    init_ok = wasm_runtime_full_init(&args) ? 1 : 0;
}

EXPORT int trenal_wasm_init(void)
{
    pthread_once(&init_once, do_init);
    return init_ok;
}

static void set_err(char *err, int errlen, const char *msg)
{
    if (err && errlen > 0) snprintf(err, (size_t)errlen, "%s", msg ? msg : "unknown error");
}

EXPORT trenal_wasm *trenal_wasm_load(const char *path, char *err, int errlen)
{
    if (!trenal_wasm_init()) { set_err(err, errlen, "WAMR runtime init failed"); return NULL; }

    FILE *f = fopen(path, "rb");
    if (!f) { set_err(err, errlen, "cannot open .wasm file"); return NULL; }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size <= 0 || size > 0x7fffffff) { fclose(f); set_err(err, errlen, "bad .wasm size"); return NULL; }

    trenal_wasm *w = calloc(1, sizeof(*w));
    w->bytes = malloc((size_t)size);
    w->size = (uint32_t)size;
    if (fread(w->bytes, 1, (size_t)size, f) != (size_t)size) {
        fclose(f); free(w->bytes); free(w);
        set_err(err, errlen, "short read");
        return NULL;
    }
    fclose(f);

    char ebuf[256] = {0};
    // WAMR may keep pointers into the buffer, so it stays alive until trenal_wasm_free.
    w->module = wasm_runtime_load(w->bytes, w->size, ebuf, sizeof(ebuf));
    if (!w->module) {
        free(w->bytes); free(w);
        set_err(err, errlen, ebuf);
        return NULL;
    }
    return w;
}

// Runs _start. Blocks until the program exits. Returns its exit code, or -1 with err set.
EXPORT int trenal_wasm_run(trenal_wasm *w,
                           const char **argv, int argc,
                           const char **env, int envc,
                           const char **map_dirs, int nmaps,
                           int in_fd, int out_fd, int err_fd,
                           unsigned int stack_size,
                           char *err, int errlen)
{
    wasm_runtime_init_thread_env();
    int code = -1;
    char ebuf[256] = {0};

    wasm_runtime_set_wasi_args_ex(w->module, NULL, 0, map_dirs, (uint32_t)nmaps,
                                  env, (uint32_t)envc, (char **)argv, argc,
                                  in_fd, out_fd, err_fd);

    wasm_module_inst_t inst = wasm_runtime_instantiate(w->module, stack_size, 0, ebuf, sizeof(ebuf));
    if (!inst) { set_err(err, errlen, ebuf); goto done; }
    w->inst = inst;

    wasm_function_inst_t start = wasm_runtime_lookup_wasi_start_function(inst);
    if (!start) { set_err(err, errlen, "not a WASI command module (no _start)"); goto deinst; }

    wasm_exec_env_t exec_env = wasm_runtime_create_exec_env(inst, stack_size);
    if (!exec_env) { set_err(err, errlen, "cannot create exec env"); goto deinst; }

    if (wasm_runtime_call_wasm(exec_env, start, 0, NULL)) {
        code = (int)wasm_runtime_get_wasi_exit_code(inst);
    } else {
        const char *ex = wasm_runtime_get_exception(inst);
        if (ex && strstr(ex, "wasi proc exit")) code = (int)wasm_runtime_get_wasi_exit_code(inst);
        else if (ex && strstr(ex, "terminated by user")) code = 130;
        else set_err(err, errlen, ex);
    }
    wasm_runtime_destroy_exec_env(exec_env);

deinst:
    w->inst = NULL;
    wasm_runtime_deinstantiate(inst);
done:
    wasm_runtime_destroy_thread_env();
    return code;
}

// Ctrl+C: stop a running program from another thread.
EXPORT void trenal_wasm_terminate(trenal_wasm *w)
{
    wasm_module_inst_t inst = w->inst;
    if (inst) wasm_runtime_terminate(inst);
}

EXPORT void trenal_wasm_free(trenal_wasm *w)
{
    if (!w) return;
    if (w->module) wasm_runtime_unload(w->module);
    free(w->bytes);
    free(w);
}
