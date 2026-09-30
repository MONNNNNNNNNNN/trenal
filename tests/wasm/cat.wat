;; Copies stdin to stdout until EOF: exercises the stdin pipe and interactive input.
(module
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 1)
  (func (export "_start")
    (loop $again
      (i32.store (i32.const 0) (i32.const 1024))
      (i32.store (i32.const 4) (i32.const 4096))
      (drop (call $fd_read (i32.const 0) (i32.const 0) (i32.const 1) (i32.const 8)))
      (if (i32.eqz (i32.load (i32.const 8))) (then (return)))
      (i32.store (i32.const 16) (i32.const 1024))
      (i32.store (i32.const 20) (i32.load (i32.const 8)))
      (drop (call $fd_write (i32.const 1) (i32.const 16) (i32.const 1) (i32.const 24)))
      (br $again))))
