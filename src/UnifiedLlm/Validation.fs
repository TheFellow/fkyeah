namespace UnifiedLlm

open System

[<RequireQualifiedAccess>]
type ValidationIssue =
    | UnknownModel of modelId: string
    | UnsupportedCapability of modelId: string * capability: string
    | InvalidTemperature of modelId: string * value: float
    | InvalidToolChoice of string
    | EmptyMessages
    | PromptAndMessagesBothPresent
    | PreviousResponseMismatch of string
    | ThinkingBudgetExceedsMaxTokens of modelId: string * maxTokens: int * budgetTokens: int

type ValidationResult = Result<Request, ValidationIssue list>

type RequestValidator =
    { Validate: Request -> ValidationResult }

/// Reasoning-effort thinking budgets and helpers for sizing max_tokens
/// above the budget. Single source of truth shared by validation and
/// any caller that builds requests with reasoning enabled.
module Reasoning =

    /// Headroom kept above the thinking budget when auto-sizing max_tokens.
    /// Matches the Anthropic adapter's auto-bump (HttpAdapters.fs).
    [<Literal>]
    let HeadroomTokens = 4096

    /// Thinking budget tokens for a reasoning-effort string. Returns 0
    /// for unknown values (callers can treat that as "no budget rule").
    let thinkingBudgetTokens (effort: string) =
        match effort with
        | "low" -> 2048
        | "medium" -> 8192
        | "high" -> 32768
        | "xhigh" -> 65536
        | _ -> 0

    /// Recommend a max_tokens value that satisfies the validator's
    /// "max_tokens > thinking budget" rule. Returns the requested value
    /// untouched when no effort is set or it already exceeds the budget.
    let recommendMaxTokens (effort: string option) (requested: int) =
        match effort with
        | Some e ->
            let budget = thinkingBudgetTokens e

            if budget > 0 && requested <= budget then
                budget + HeadroomTokens
            else
                requested
        | None -> requested

module ValidationIssue =

    let describe issue =
        match issue with
        | ValidationIssue.UnknownModel modelId -> $"Unknown model '{modelId}'"
        | ValidationIssue.UnsupportedCapability(modelId, capability) ->
            $"Model '{modelId}' does not support {capability}"
        | ValidationIssue.InvalidTemperature(modelId, value) ->
            $"Model '{modelId}' received invalid temperature {value}"
        | ValidationIssue.InvalidToolChoice message -> $"Invalid tool choice: {message}"
        | ValidationIssue.EmptyMessages -> "Request must include prompt or messages"
        | ValidationIssue.PromptAndMessagesBothPresent -> "Prompt and messages cannot both be present"
        | ValidationIssue.PreviousResponseMismatch message -> $"Previous response mismatch: {message}"
        | ValidationIssue.ThinkingBudgetExceedsMaxTokens(modelId, maxTokens, budgetTokens) ->
            $"Model '{modelId}' has max_tokens={maxTokens} but thinking budget requires {budgetTokens}; increase max_tokens above the thinking budget"

module RequestValidator =

    let private hasImageContent (message: Message) =
        message.Content
        |> List.exists (function
            | Image _ -> true
            | _ -> false)

    let private toolChoiceIssues (request: Request) =
        match request.ToolChoice, request.Tools with
        | Some(ToolChoice.Named name), Some tools when tools |> List.exists (fun tool -> tool.Name = name) |> not ->
            [ ValidationIssue.InvalidToolChoice($"Named tool '{name}' is not present in request.Tools") ]
        | Some(ToolChoice.Named name), Option.None ->
            [ ValidationIssue.InvalidToolChoice($"Named tool '{name}' requires request.Tools") ]
        | Some ToolChoice.Required, Some tools when List.isEmpty tools ->
            [ ValidationIssue.InvalidToolChoice("ToolChoice.Required requires at least one tool") ]
        | Some ToolChoice.Required, Option.None ->
            [ ValidationIssue.InvalidToolChoice("ToolChoice.Required requires request.Tools") ]
        | _ -> []

    let fromCatalog () : RequestValidator =
        { Validate =
            fun request ->
                let issues = ResizeArray<ValidationIssue>()

                match request.Prompt, request.Messages with
                | Some _, _ :: _ -> issues.Add ValidationIssue.PromptAndMessagesBothPresent
                | None, [] -> issues.Add ValidationIssue.EmptyMessages
                | _ -> ()

                for issue in toolChoiceIssues request do
                    issues.Add issue

                match ModelCatalog.resolveModel request.Model with
                | None -> issues.Add(ValidationIssue.UnknownModel request.Model)
                | Some model ->
                    match request.Tools with
                    | Some tools when not (List.isEmpty tools) && not model.SupportsTools ->
                        issues.Add(ValidationIssue.UnsupportedCapability(model.Id, "tools"))
                    | _ -> ()

                    let hasVision = request.Messages |> List.exists hasImageContent

                    if hasVision && not model.SupportsVision then
                        issues.Add(ValidationIssue.UnsupportedCapability(model.Id, "vision"))

                    if request.ReasoningEffort.IsSome && not model.SupportsReasoning then
                        issues.Add(ValidationIssue.UnsupportedCapability(model.Id, "reasoning"))

                    match request.ReasoningEffort, request.MaxTokens with
                    | Some effort, Some maxTokens ->
                        let budgetTokens = Reasoning.thinkingBudgetTokens effort

                        if budgetTokens > 0 && maxTokens <= budgetTokens then
                            issues.Add(
                                ValidationIssue.ThinkingBudgetExceedsMaxTokens(model.Id, maxTokens, budgetTokens)
                            )
                    | _ -> ()

                    match request.Temperature with
                    | Some value when value < 0.0 || value > 2.0 ->
                        issues.Add(ValidationIssue.InvalidTemperature(model.Id, value))
                    | _ -> ()

                    match request.PreviousResponseId with
                    | Some _ ->
                        match request.Provider with
                        | Some provider when
                            not (String.Equals(provider, model.Provider, StringComparison.OrdinalIgnoreCase))
                            ->
                            issues.Add(
                                ValidationIssue.PreviousResponseMismatch(
                                    $"provider '{provider}' does not match model provider '{model.Provider}'"
                                )
                            )
                        | _ -> ()
                    | None -> ()

                if issues.Count = 0 then
                    Result.Ok request
                else
                    Result.Error(issues |> Seq.toList) }
