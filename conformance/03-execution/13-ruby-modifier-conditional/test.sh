#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Regression test: Ruby modifier conditionals (trailing if/unless)
# Bug: a block-depth parser might count `if` in `return false if cond` as a
# block starter with no matching `end`, breaking extraction. Modifier keywords
# appearing mid-line must NOT be counted as block starters.

export EXTRACT_RUN="$(cd "$TEST_DIR/../../07-models" && pwd)/extract-run.sh"

# Plant a simulated LLM response with Ruby code using modifier conditionals.
# This code has 15 keywords that naive parsers would count as block starters
# (module, class, def×3, if×7, unless×2, while) but only 7 of those are
# actual blocks (module, class, def×3, if×1, while×1) with matching `end`.
mkdir -p "$LOGS_DIR/tool"
cat > "$LOGS_DIR/tool/response.md" <<'RUBY_RESPONSE'
Here is the Ruby code:

```ruby
module OrderValidator
  class Validator
    def validate(order_id, total, items)
      puts "ERROR: nil order" if order_id.nil?
      return if order_id.nil?
      puts "ERROR: no items" if items.nil?
      return if items.nil?
      puts "ERROR: empty" if items.empty?
      return if items.empty?
      puts "ERROR: negative total" unless total > 0
      return unless total > 0
      puts "ERROR: too many items" unless items.length <= 100
      return unless items.length <= 100

      status = "VALID"

      if total > 10000
        status = "VALID-HIGH-VALUE"
      end

      count = 0
      while count < items.length
        count += 1
      end

      puts "#{order_id}: #{status} (#{count} items, total=#{total})"
    end

    def self.run
      v = Validator.new
      v.validate("ORD-001", 150, ["a", "b", "c"])
      v.validate(nil, 0, [])
      v.validate("ORD-002", 25000, ["x"])
    end
  end
end

OrderValidator::Validator.run
```
RUBY_RESPONSE

# Set the env vars that extract-run.sh expects
export ATTRACTOR_LOGS_ROOT="$LOGS_DIR"
export ATTRACTOR_STAGE_DIR="$LOGS_DIR/tool"

# Run the extraction and execution directly
bash "$EXTRACT_RUN" ruby tool > "$LOGS_DIR/tool/tool_output.txt" 2>&1
exit_code=$?

assert_exit_code 0 "$exit_code" "Ruby with modifier conditionals should execute cleanly"
assert_file_exists "$LOGS_DIR/tool/tool_output.txt" "tool produced output"

output="$(cat "$LOGS_DIR/tool/tool_output.txt")"
assert_contains "$output" "ORD-001: VALID (3 items, total=150)" "first order validates"
assert_contains "$output" "ERROR: nil order" "nil order caught by modifier if"
assert_contains "$output" "ORD-002: VALID-HIGH-VALUE (1 items, total=25000)" "high-value order validates"

pass "Ruby modifier conditionals (trailing if/unless) extracted and executed correctly"
