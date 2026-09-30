;; Never returns: Ctrl+C must stop it.
(module
  (memory (export "memory") 1)
  (func (export "_start") (loop $l (br $l))))
