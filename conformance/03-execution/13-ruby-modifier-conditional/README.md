# 13-ruby-modifier-conditional

Regression test for Ruby modifier conditionals (trailing `if`/`unless`/`while`).

A naive block-depth parser might count the `if` in `return false if condition` as a block starter expecting a matching `end`. Ruby allows `if`/`unless`/`while`/`until` as trailing modifiers that do NOT require `end`. This test plants a Ruby program with multiple modifier conditionals alongside real block-form keywords and verifies the code extracts and executes correctly.

The test Ruby code has:
- `module`/`class`/`def` blocks with matching `end` (real blocks)
- `if`/`unless` as trailing modifiers on `return` statements (NOT blocks)
- A block-form `if` with matching `end` (real block)
- A block-form `while` with matching `end` (real block)

If a parser incorrectly counts modifier keywords as block starters, it will see 15 starters vs 7 `end` keywords and fail to find the module boundary.
