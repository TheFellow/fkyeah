module LiveApiTests

open System
open System.IO
open Xunit
open UnifiedLlm
open Attractor

// ============================================================================
// Live API Test Infrastructure
// ============================================================================

/// Custom Fact attribute that skips when the required API key env var is missing.
type LiveApiFactAttribute(provider: string) =
    inherit FactAttribute()

    let envVar =
        match provider with
        | "anthropic" -> "ANTHROPIC_API_KEY"
        | "openai" -> "OPENAI_API_KEY"
        | "gemini" -> "GEMINI_API_KEY"
        | _ -> failwithf "Unknown provider: %s" provider

    do
        let value = Environment.GetEnvironmentVariable(envVar)

        if String.IsNullOrEmpty(value) then
            base.Skip <- sprintf "Skipped: %s not set" envVar

module LiveApiHelpers =

    let envVarFor (provider: string) =
        match provider with
        | "anthropic" -> "ANTHROPIC_API_KEY"
        | "openai" -> "OPENAI_API_KEY"
        | "gemini" -> "GEMINI_API_KEY"
        | _ -> failwithf "Unknown provider: %s" provider

    let createClient (provider: string) =
        let key = Environment.GetEnvironmentVariable(envVarFor provider)
        let client = Client()

        match provider with
        | "anthropic" -> client.RegisterAdapter(AnthropicAdapter(key))
        | "openai" -> client.RegisterAdapter(OpenAIAdapter(key))
        | "gemini" -> client.RegisterAdapter(GeminiAdapter(key))
        | _ -> failwithf "Unknown provider: %s" provider

        client

    let modelFor (provider: string) =
        match provider with
        | "anthropic" -> "claude-sonnet-4-5"
        | "openai" -> "gpt-4o-mini"
        | "gemini" -> "gemini-2.5-flash"
        | _ -> failwithf "Unknown provider: %s" provider

// ============================================================================
// Smoke Test — Minimal DOT pipeline with a single task node
// ============================================================================

module SmokeTest =
    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider

        let dot =
            sprintf
                "digraph Smoke {\n    graph [goal=\"Smoke test\"]\n    start [shape=Mdiamond]\n    task  [shape=box, prompt=\"Reply with exactly: SMOKE_TEST_PASSED\", llm_model=\"%s\", llm_provider=\"%s\"]\n    exit  [shape=Msquare]\n    start -> task -> exit\n}"
                model
                provider

        let graph = DotParser.parseOrRaise dot

        let logsDir =
            Path.Combine(Path.GetTempPath(), "attractor-live-test-" + Guid.NewGuid().ToString("N").[..7])

        Directory.CreateDirectory(logsDir) |> ignore

        try
            let registry = HandlerRegistry.CreateDefault(llmClient = client)

            let config =
                { RunConfig.Default(logsDir) with
                    Registry = registry }

            let result = Engine.run graph config
            Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
            Assert.True(result.CompletedNodes.Length >= 2) // start + task (exit may or may not be counted)
        finally
            try
                Directory.Delete(logsDir, true)
            with _ ->
                ()

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Smoke test pipeline - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``Smoke test pipeline - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Smoke test pipeline - Gemini`` () = runTest "gemini"
