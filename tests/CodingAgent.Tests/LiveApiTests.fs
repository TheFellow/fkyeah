module CodingAgent.LiveApiTests

open System
open System.IO
open Xunit
open UnifiedLlm
open CodingAgent

// ============================================================
// Infrastructure: LiveApiFact attribute + helpers
// ============================================================

/// Custom Fact attribute that skips when the required API key env var is missing.
type LiveApiFactAttribute(provider: string) =
    inherit FactAttribute()

    let envVar =
        match provider with
        | "anthropic" -> "ANTHROPIC_API_KEY"
        | "openai" -> "OPENAI_API_KEY"
        | "gemini" -> "GEMINI_API_KEY"
        | _ -> failwith (sprintf "Unknown provider: %s" provider)

    do
        match Environment.GetEnvironmentVariable(envVar) with
        | null
        | "" -> base.Skip <- sprintf "Requires %s environment variable" envVar
        | _ -> ()

    member _.Provider = provider

module LiveApiHelpers =

    /// Map provider name to the required environment variable.
    let envVarFor (provider: string) =
        match provider with
        | "anthropic" -> "ANTHROPIC_API_KEY"
        | "openai" -> "OPENAI_API_KEY"
        | "gemini" -> "GEMINI_API_KEY"
        | _ -> failwith (sprintf "Unknown provider: %s" provider)

    /// Create a Client with a real HTTP adapter for the given provider.
    let createClient (provider: string) =
        let apiKey = Environment.GetEnvironmentVariable(envVarFor provider)
        let client = Client()

        match provider with
        | "anthropic" -> client.RegisterAdapter(AnthropicAdapter(apiKey))
        | "openai" -> client.RegisterAdapter(OpenAIAdapter(apiKey))
        | "gemini" -> client.RegisterAdapter(GeminiAdapter(apiKey))
        | _ -> failwith (sprintf "Unknown provider: %s" provider)

        client

    /// Default model for each provider.
    let modelFor (provider: string) =
        match provider with
        | "anthropic" -> "claude-sonnet-4-5"
        | "openai" -> "gpt-4o-mini"
        | "gemini" -> "gemini-2.0-flash"
        | _ -> failwith (sprintf "Unknown provider: %s" provider)

    /// Build a real IProviderProfile for the given provider.
    let profileFor (provider: string) =
        let model = modelFor provider

        match provider with
        | "anthropic" -> AnthropicProfile(model) :> IProviderProfile
        | "openai" -> OpenAIProfile(model) :> IProviderProfile
        | "gemini" -> GeminiProfile(model) :> IProviderProfile
        | _ -> failwith (sprintf "Unknown provider: %s" provider)

/// Create a temp directory for live API tests.
let createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "live-api-test-" + Guid.NewGuid().ToString("N").[..7])

    Directory.CreateDirectory(dir) |> ignore
    dir

/// Clean up temp directory.
let cleanupDir (dir: string) =
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

// ============================================================
// 1. Simple file creation
// ============================================================

module SimpleFileCreation =

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Anthropic - creates a file via tool use`` () =
        let dir = createTempDir ()

        try
            let provider = "anthropic"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Create a file called hello.txt containing exactly 'Hello World'")
            Assert.Equal(Idle, session.State)
            Assert.True(File.Exists(Path.Combine(dir, "hello.txt")), "hello.txt should exist")
            let content = File.ReadAllText(Path.Combine(dir, "hello.txt"))
            Assert.Contains("Hello", content)
        finally
            cleanupDir dir

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``OpenAI - creates a file via tool use`` () =
        let dir = createTempDir ()

        try
            let provider = "openai"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Create a file called hello.txt containing exactly 'Hello World'")
            Assert.Equal(Idle, session.State)
            Assert.True(File.Exists(Path.Combine(dir, "hello.txt")), "hello.txt should exist")
            let content = File.ReadAllText(Path.Combine(dir, "hello.txt"))
            Assert.Contains("Hello", content)
        finally
            cleanupDir dir

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Gemini - creates a file via tool use`` () =
        let dir = createTempDir ()

        try
            let provider = "gemini"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Create a file called hello.txt containing exactly 'Hello World'")
            Assert.Equal(Idle, session.State)
            Assert.True(File.Exists(Path.Combine(dir, "hello.txt")), "hello.txt should exist")
            let content = File.ReadAllText(Path.Combine(dir, "hello.txt"))
            Assert.Contains("Hello", content)
        finally
            cleanupDir dir

// ============================================================
// 2. Read file then edit it
// ============================================================

module ReadAndEditFile =

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Anthropic - reads and edits an existing file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "original content")
            let provider = "anthropic"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Read the file test.txt and change its content to 'updated content'")
            Assert.Equal(Idle, session.State)
            let content = File.ReadAllText(filePath)
            Assert.NotEqual<string>("original content", content)
        finally
            cleanupDir dir

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``OpenAI - reads and edits an existing file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "original content")
            let provider = "openai"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Read the file test.txt and change its content to 'updated content'")
            Assert.Equal(Idle, session.State)
            let content = File.ReadAllText(filePath)
            Assert.NotEqual<string>("original content", content)
        finally
            cleanupDir dir

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Gemini - reads and edits an existing file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "original content")
            let provider = "gemini"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Read the file test.txt and change its content to 'updated content'")
            Assert.Equal(Idle, session.State)
            let content = File.ReadAllText(filePath)
            Assert.NotEqual<string>("original content", content)
        finally
            cleanupDir dir

// ============================================================
// 3. Shell command execution
// ============================================================

module ShellCommandExecution =

    let private hasShellToolCall (session: Session) =
        session.History
        |> List.exists (fun t ->
            match t with
            | AssistantTurn(_, toolCalls, _, _, _) ->
                toolCalls |> List.exists (fun tc -> tc.Name = "shell" || tc.Name = "bash")
            | _ -> false)

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Anthropic - executes a shell command`` () =
        let dir = createTempDir ()

        try
            let provider = "anthropic"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Run the command: echo hello_from_shell")
            Assert.Equal(Idle, session.State)
            Assert.True(hasShellToolCall session, "Expected a shell tool call in history")
        finally
            cleanupDir dir

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``OpenAI - executes a shell command`` () =
        let dir = createTempDir ()

        try
            let provider = "openai"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Run the command: echo hello_from_shell")
            Assert.Equal(Idle, session.State)
            Assert.True(hasShellToolCall session, "Expected a shell tool call in history")
        finally
            cleanupDir dir

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Gemini - executes a shell command`` () =
        let dir = createTempDir ()

        try
            let provider = "gemini"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Run the command: echo hello_from_shell")
            Assert.Equal(Idle, session.State)
            Assert.True(hasShellToolCall session, "Expected a shell tool call in history")
        finally
            cleanupDir dir

// ============================================================
// 4. Reasoning effort change
// ============================================================

module ReasoningEffortChange =

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Anthropic - sessions with different reasoning effort both complete`` () =
        let dir = createTempDir ()

        try
            let provider = "anthropic"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let lowSession =
                Session(
                    profile,
                    env,
                    client,
                    config =
                        { SessionConfig.Default with
                            ReasoningEffort = Some "low" }
                )

            lowSession.ProcessInput("What is 2+2?")
            Assert.Equal(Idle, lowSession.State)

            let highSession =
                Session(
                    profile,
                    env,
                    client,
                    config =
                        { SessionConfig.Default with
                            ReasoningEffort = Some "high" }
                )

            highSession.ProcessInput("What is 2+2?")
            Assert.Equal(Idle, highSession.State)
        finally
            cleanupDir dir

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``OpenAI - session with default config completes`` () =
        let dir = createTempDir ()

        try
            let provider = "openai"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            // gpt-4o-mini doesn't support reasoning.effort — verify default config works
            let session = Session(profile, env, client)
            session.ProcessInput("What is 2+2?")
            Assert.Equal(Idle, session.State)
        finally
            cleanupDir dir

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Gemini - session with default config completes`` () =
        let dir = createTempDir ()

        try
            let provider = "gemini"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            // Gemini flash doesn't support reasoning.effort — verify default config works
            let session = Session(profile, env, client)
            session.ProcessInput("What is 2+2?")
            Assert.Equal(Idle, session.State)
        finally
            cleanupDir dir

// ============================================================
// 5. Provider-specific editing format
// ============================================================

module ProviderEditingFormat =

    let private hasAnyToolCall (session: Session) =
        session.History
        |> List.exists (fun t ->
            match t with
            | AssistantTurn(_, toolCalls, _, _, _) -> not toolCalls.IsEmpty
            | _ -> false)

    [<LiveApiFact("anthropic")>]
    [<Trait("Category", "LiveApi")>]
    let ``Anthropic - uses tool calls to edit a file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "line one\nline two\n")
            let provider = "anthropic"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Edit the file test.txt to add a new line at the end saying 'appended'")
            Assert.Equal(Idle, session.State)
            Assert.True(hasAnyToolCall session, "Expected at least one tool call in history")
        finally
            cleanupDir dir

    [<LiveApiFact("openai")>]
    [<Trait("Category", "LiveApi")>]
    let ``OpenAI - uses tool calls to edit a file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "line one\nline two\n")
            let provider = "openai"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Edit the file test.txt to add a new line at the end saying 'appended'")
            Assert.Equal(Idle, session.State)
            Assert.True(hasAnyToolCall session, "Expected at least one tool call in history")
        finally
            cleanupDir dir

    [<LiveApiFact("gemini")>]
    [<Trait("Category", "LiveApi")>]
    let ``Gemini - uses tool calls to edit a file`` () =
        let dir = createTempDir ()

        try
            let filePath = Path.Combine(dir, "test.txt")
            File.WriteAllText(filePath, "line one\nline two\n")
            let provider = "gemini"
            let profile = LiveApiHelpers.profileFor provider
            let client = LiveApiHelpers.createClient provider
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let session = Session(profile, env, client)
            session.ProcessInput("Edit the file test.txt to add a new line at the end saying 'appended'")
            Assert.Equal(Idle, session.State)
            Assert.True(hasAnyToolCall session, "Expected at least one tool call in history")
        finally
            cleanupDir dir
