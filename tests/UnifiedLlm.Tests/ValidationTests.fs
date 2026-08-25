module UnifiedLlm.ValidationTests

open Xunit
open UnifiedLlm

let private validator = RequestValidator.fromCatalog ()

[<Fact>]
let ``validator rejects unknown model and invalid temperature`` () =
    let request =
        { Request.Create("unknown-model", [ Message.User("hello") ]) with
            Temperature = Some 9.0 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues -> Assert.Contains(ValidationIssue.UnknownModel "unknown-model", issues)

[<Fact>]
let ``validator rejects prompt and messages together`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Prompt = Some "duplicate" }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues -> Assert.Contains(ValidationIssue.PromptAndMessagesBothPresent, issues)

[<Fact>]
let ``validator rejects named tool choice when tool missing`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ToolChoice = Some(ToolChoice.Named "missing_tool") }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.Contains(ValidationIssue.InvalidToolChoice("Named tool 'missing_tool' requires request.Tools"), issues)

// ── New Sprint-010 tests ──

[<Fact>]
let ``validator accepts valid request with known model`` () =
    let request = Request.Create("gpt-5.4", [ Message.User("hello") ])

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

[<Fact>]
let ``validator rejects empty messages with no prompt`` () =
    let request = Request.Create("gpt-5.4", [])

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues -> Assert.Contains(ValidationIssue.EmptyMessages, issues)

[<Fact>]
let ``validator accepts tools request with model that supports tools`` () =
    let tool =
        { Name = "my_tool"
          Description = "does stuff"
          Parameters = "{}" }

    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("use tools") ]) with
            Tools = Some [ tool ] }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

[<Fact>]
let ``validator rejects negative temperature`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("hi") ]) with
            Temperature = Some -1.0 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.InvalidTemperature _ -> true
                | _ -> false),
            "expected InvalidTemperature issue"
        )

[<Fact>]
let ``validator rejects temperature above 2`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("hi") ]) with
            Temperature = Some 3.5 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.InvalidTemperature _ -> true
                | _ -> false),
            "expected InvalidTemperature issue"
        )

[<Fact>]
let ``validator reports multiple simultaneous issues`` () =
    let request =
        { Request.Create("unknown-model-xyz", []) with
            Temperature = Some 5.0 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.UnknownModel _ -> true
                | _ -> false),
            "expected UnknownModel issue"
        )

        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.EmptyMessages -> true
                | _ -> false),
            "expected EmptyMessages issue"
        )

[<Fact>]
let ``validator accepts reasoning request against supported model`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("think hard") ]) with
            ReasoningEffort = Some "high" }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

[<Fact>]
let ``validator rejects ToolChoice Required with no tools`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ToolChoice = Some ToolChoice.Required }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.InvalidToolChoice _ -> true
                | _ -> false),
            "expected InvalidToolChoice issue"
        )

[<Fact>]
let ``validator rejects previous_response_id when explicit provider does not match model provider`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("hello") ]) with
            PreviousResponseId = Some "resp_123"
            Provider = Some "openai" }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.Contains(
            ValidationIssue.PreviousResponseMismatch("provider 'openai' does not match model provider 'anthropic'"),
            issues
        )

[<Fact>]
let ``validator rejects previous_response_id for a provider without continuation support`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("hello") ]) with
            PreviousResponseId = Some "resp_123" }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.Contains(
            ValidationIssue.PreviousResponseMismatch("provider 'anthropic' does not support previous_response_id"),
            issues
        )

[<Fact>]
let ``validator rejects previous_response_id for uncatalogued OpenRouter model`` () =
    let request =
        { Request.Create("vendor/model", [ Message.User("hello") ]) with
            Provider = Some "openrouter"
            PreviousResponseId = Some "resp_123" }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.Contains(
            ValidationIssue.PreviousResponseMismatch("provider 'openrouter' does not support previous_response_id"),
            issues
        )

[<Fact>]
let ``validator warns when thinking budget exceeds explicit max_tokens`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("think hard") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 4096 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected validation failure")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.ThinkingBudgetExceedsMaxTokens _ -> true
                | _ -> false),
            "expected ThinkingBudgetExceedsMaxTokens issue"
        )

[<Fact>]
let ``validator does not warn when max_tokens exceeds thinking budget`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("think hard") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 40000 }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

[<Fact>]
let ``validator does not warn about thinking budget when max_tokens is not set`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("think hard") ]) with
            ReasoningEffort = Some "high" }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

[<Fact>]
let ``validator does not warn about thinking budget when reasoning_effort is not set`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.User("hello") ]) with
            MaxTokens = Some 4096 }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok but got errors: {issues}")

// gpt-5.5 reasoning coverage. The catalog flags gpt-5.5 as
// SupportsReasoning=true; these tests pin the validator against each
// effort value paired with a Reasoning.recommendMaxTokens-sized budget,
// so future catalog changes that drop reasoning support fail loudly.

[<Theory>]
[<InlineData("low")>]
[<InlineData("medium")>]
[<InlineData("high")>]
[<InlineData("xhigh")>]
let ``validator accepts gpt-5.5 with reasoning_effort and recommended max_tokens`` (effort: string) =
    let maxTokens = Reasoning.recommendMaxTokens (Some effort) 16384

    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some effort
            MaxTokens = Some maxTokens }

    match validator.Validate request with
    | Result.Ok _ -> ()
    | Result.Error issues -> Assert.Fail($"expected Ok for effort={effort}, got: {issues}")

[<Fact>]
let ``validator rejects gpt-5.5 high reasoning paired with too-small max_tokens`` () =
    // Regression guard for the bug fixed in 0.15.2: the unbumped 16384
    // would slip past for "low"/"medium" but must trip for "high" (32768)
    // and "xhigh" (65536) on any model that supports reasoning.
    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 16384 }

    match validator.Validate request with
    | Result.Ok _ -> Assert.Fail("expected ThinkingBudgetExceedsMaxTokens for gpt-5.5 + high + 16384")
    | Result.Error issues ->
        Assert.True(
            issues
            |> List.exists (function
                | ValidationIssue.ThinkingBudgetExceedsMaxTokens(modelId, mt, budget) ->
                    modelId = "gpt-5.5" && mt = 16384 && budget = 32768
                | _ -> false),
            "expected ThinkingBudgetExceedsMaxTokens(gpt-5.5, 16384, 32768)"
        )

[<Fact>]
let ``Reasoning.thinkingBudgetTokens returns expected budgets`` () =
    Assert.Equal(2048, Reasoning.thinkingBudgetTokens "low")
    Assert.Equal(8192, Reasoning.thinkingBudgetTokens "medium")
    Assert.Equal(32768, Reasoning.thinkingBudgetTokens "high")
    Assert.Equal(65536, Reasoning.thinkingBudgetTokens "xhigh")
    Assert.Equal(0, Reasoning.thinkingBudgetTokens "bogus")

[<Fact>]
let ``Reasoning.recommendMaxTokens bumps when requested <= budget`` () =
    Assert.Equal(32768 + 4096, Reasoning.recommendMaxTokens (Some "high") 16384)
    Assert.Equal(65536 + 4096, Reasoning.recommendMaxTokens (Some "xhigh") 16384)
    Assert.Equal(8192 + 4096, Reasoning.recommendMaxTokens (Some "medium") 8192)

[<Fact>]
let ``Reasoning.recommendMaxTokens leaves requested unchanged when above budget`` () =
    Assert.Equal(100_000, Reasoning.recommendMaxTokens (Some "high") 100_000)
    Assert.Equal(16384, Reasoning.recommendMaxTokens (Some "low") 16384)
    Assert.Equal(16384, Reasoning.recommendMaxTokens None 16384)
