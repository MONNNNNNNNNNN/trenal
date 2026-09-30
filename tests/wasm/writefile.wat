;; Creates wasm-out.txt in the pre-opened current directory (fd 3) and writes a line.
(module
  (import "wasi_snapshot_preview1" "path_open"
    (func $path_open (param i32 i32 i32 i32 i32 i64 i64 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1)
  (data (i32.const 100) "wasm-out.txt")
  (data (i32.const 200) "written by wasm\n")
  (func (export "_start") (local $err i32)
    ;; path_open(dirfd=3, lookupflags=0, path, len=12, oflags=CREAT|TRUNC, rights=FD_WRITE, inherit=0, fdflags=0, &fd@300)
    (local.set $err (call $path_open (i32.const 3) (i32.const 0) (i32.const 100) (i32.const 12)
      (i32.const 9) (i64.const 64) (i64.const 0) (i32.const 0) (i32.const 300)))
    (if (local.get $err) (then (call $proc_exit (local.get $err))))
    (i32.store (i32.const 0) (i32.const 200))
    (i32.store (i32.const 4) (i32.const 16))
    (drop (call $fd_write (i32.load (i32.const 300)) (i32.const 0) (i32.const 1) (i32.const 8)))))
