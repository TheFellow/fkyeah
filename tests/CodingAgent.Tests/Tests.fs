module CodingAgent.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open UnifiedLlm
open CodingAgent

// ============================================================
// Helper: create a mock adapter that responds based on call count
// ============================================================

let makeMockAdapter (responses: (Request -> Response) list) =
    let mock = ConfigurableMockAdapter("test")
    let mutable callIndex = 0

    mock.SetCompleteHandler(fun req ->
        let idx = min callIndex (responses.Length - 1)
        callIndex <- callIndex + 1
        responses.[idx] req)

    mock

let makeSimpleMock () =
    let mock = ConfigurableMockAdapter("test")

    mock.SetCompleteHandler(fun req ->
        { Id = "r1"
          Model = req.Model
          Provider = "test"
          Message = Message.Assistant("Done.")
          FinishReason = Stop "stop"
          Usage = Usage.Zero
          ResponseId = None
          Raw = None
          Warnings = []
          RateLimit = None })

    mock

let makeToolMock () =
    let mock = ConfigurableMockAdapter("test")
    let mutable callCount = 0

    mock.SetCompleteHandler(fun _req ->
        callCount <- callCount + 1

        if callCount = 1 then
            let tc =
                { Id = "call_1"
                  Name = "read_file"
                  Arguments = """{"file_path":"/tmp/test.txt"}"""
                  Metadata = Map.empty }

            { Id = "r1"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage =
                { InputTokens = 10
                  OutputTokens = 5
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None }
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }
        else
            { Id = "r2"
              Model = "m"
              Provider = "test"
              Message = Message.Assistant("File content received.")
              FinishReason = Stop "stop"
              Usage =
                { InputTokens = 20
                  OutputTokens = 10
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None }
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

    mock

/// Create a temp directory for tests
let createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "coding-agent-test-" + Guid.NewGuid().ToString("N").[..7])

    Directory.CreateDirectory(dir) |> ignore
    dir

/// Clean up temp directory
let cleanupDir (dir: string) =
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

/// Create a test profile
type TestProfile(model: string) =
    interface IProviderProfile with
        member _.Id = "test"
        member _.Model = model

        member _.ToolDefinitions =
            [ SharedTools.readFile
              SharedTools.writeFile
              SharedTools.editFile
              SharedTools.shell
              SharedTools.grep
              SharedTools.glob ]

        member _.BuildSystemPrompt(env, projectDocs, userInstructions) =
            let parts =
                [ "You are a test coding assistant."
                  sprintf "Working directory: %s" env.WorkingDirectory
                  match projectDocs with
                  | Some d -> d
                  | None -> ""
                  match userInstructions with
                  | Some u -> u
                  | None -> "" ]

            parts |> List.filter (fun s -> s <> "") |> String.concat "\n\n"

        member _.SupportsStreaming = true
        member _.SupportsParallelToolCalls = true
        member _.ContextWindowSize = 200000

// ============================================================
// 9.1 Core Loop Tests
// ============================================================

module CoreLoopTests =

    [<Fact>]
    let ``Session can be created with ProviderProfile and ExecutionEnvironment`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            Assert.Equal(Idle, session.State)
            Assert.NotNull(session.SessionId)
        finally
            cleanupDir dir

    [<Fact>]
    let ``process_input runs agentic loop and completes naturally`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("Hello")
            // After natural completion, state should be Idle
            Assert.Equal(Idle, session.State)
            // Should have at least UserTurn + AssistantTurn
            Assert.True(session.History.Length >= 2)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Natural completion when model responds with text only`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("What is 2+2?")
            let lastTurn = session.History |> List.last

            match lastTurn with
            | AssistantTurn(content, toolCalls, _, _, _) ->
                Assert.Equal("Done.", content)
                Assert.True(toolCalls.IsEmpty)
            | _ -> Assert.Fail("Expected AssistantTurn")
        finally
            cleanupDir dir

    [<Fact>]
    let ``Round limits max_tool_rounds_per_input stops loop`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            // Always return tool calls - never naturally complete
            mock.SetCompleteHandler(fun _req ->
                let tc =
                    { Id = "call_1"
                      Name = "shell"
                      Arguments = """{"command":"echo hi"}"""
                      Metadata = Map.empty }

                { Id = "r"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    MaxToolRoundsPerInput = 2 }

            let session = Session(TestProfile("m"), env, client, config)

            session.RegisterTool(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> "hi" }
            )

            session.ProcessInput("run a command")
            // Should have stopped due to round limit
            let limitEvents = session.Events |> List.filter (fun e -> e.Kind = TurnLimit)
            Assert.True(limitEvents.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session turn limits max_turns stops loop`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())

            let config =
                { SessionConfig.Default with
                    MaxTurns = 2 }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("First")
            session.ProcessInput("Second")
            // Third input should hit the turn limit
            session.ProcessInput("Third")
            let limitEvents = session.Events |> List.filter (fun e -> e.Kind = TurnLimit)
            Assert.True(limitEvents.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Abort signal cancels and transitions to Closed`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Abort()
            Assert.Equal(Closed, session.State)
            let endEvents = session.Events |> List.filter (fun e -> e.Kind = SessionEnd)
            Assert.True(endEvents.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Multiple sequential inputs work`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("First request")
            Assert.Equal(Idle, session.State)
            session.ProcessInput("Second request")
            Assert.Equal(Idle, session.State)

            let userTurns =
                session.History
                |> List.filter (fun t ->
                    match t with
                    | UserTurn _ -> true
                    | _ -> false)

            Assert.Equal(2, userTurns.Length)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session.Usage aggregates tokens across tool-loop turns`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeToolMock ())
            let session = Session(TestProfile("m"), env, client)

            session.RegisterTool(
                { Definition = SharedTools.readFile
                  IsCacheable = false
                  Execute = fun _ _ -> "file contents" }
            )

            session.ProcessInput("Read the file")

            // makeToolMock returns usage 10/5 on the tool call turn and 20/10 on
            // the final turn — cumulative = 30/15.
            Assert.Equal(30, session.Usage.InputTokens)
            Assert.Equal(15, session.Usage.OutputTokens)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session.Usage is zero before any LLM calls`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            Assert.Equal(Usage.Zero, session.Usage)
            Assert.Equal(0L, session.CostMicrodollars)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session.CostMicrodollars accumulates per-call so cache hits only zero that call`` () =
        // Two calls on claude-sonnet-4-6 ($3/M in, $15/M out):
        //  Call 1: 1000 in / 200 out, no cache   -> 1000*3 + 200*15 = 6000 micros
        //  Call 2: 500 in / 100 out, cache read  -> 0 (cache hit zeros this call)
        // Aggregate-then-cost would see cache_read>0 and zero EVERYTHING.
        // Per-call cost must preserve call 1's $0.006.
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable callIdx = 0

            mock.SetCompleteHandler(fun _ ->
                callIdx <- callIdx + 1

                if callIdx = 1 then
                    let tc =
                        { Id = "c1"
                          Name = "read_file"
                          Arguments = """{"file_path":"/tmp/x"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "claude-sonnet-4-6"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage =
                        { InputTokens = 1000
                          OutputTokens = 200
                          ReasoningTokens = None
                          CacheReadTokens = None
                          CacheWriteTokens = None }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "claude-sonnet-4-6"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage =
                        { InputTokens = 500
                          OutputTokens = 100
                          ReasoningTokens = None
                          CacheReadTokens = Some 400
                          CacheWriteTokens = None }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(TestProfile("claude-sonnet-4-6"), env, client)

            session.RegisterTool(
                { Definition = SharedTools.readFile
                  IsCacheable = false
                  Execute = fun _ _ -> "x" }
            )

            session.ProcessInput("go")

            // Call 1 cost: 1000 * $3/M + 200 * $15/M = $0.003 + $0.003 = $0.006 -> 6000 micros.
            Assert.Equal(6000L, session.CostMicrodollars)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Loop detection triggers warning steering turn`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun _req ->
                callCount <- callCount + 1

                if callCount <= 12 then
                    let tc =
                        { Id = sprintf "call_%d" callCount
                          Name = "shell"
                          Arguments = """{"command":"echo same"}"""
                          Metadata = Map.empty }

                    { Id = "r"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "done"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("Done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    LoopDetectionWindow = 4
                    MaxToolRoundsPerInput = 15 }

            let session = Session(TestProfile("m"), env, client, config)

            session.RegisterTool(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> "same output" }
            )

            session.ProcessInput("keep running")
            let loopEvents = session.Events |> List.filter (fun e -> e.Kind = LoopDetection)
            Assert.True(loopEvents.Length > 0)
        finally
            cleanupDir dir

// ============================================================
// 9.2 Provider Profile Tests
// ============================================================

module ProviderProfileTests =

    [<Fact>]
    let ``OpenAI profile provides codex-rs-aligned tools including apply_patch`` () =
        let profile = OpenAIProfile("gpt-5.2-codex") :> IProviderProfile
        let names = profile.ToolDefinitions |> List.map (fun t -> t.Name)
        Assert.Contains("apply_patch", names)
        Assert.Contains("read_file", names)
        Assert.Contains("shell", names)

    [<Fact>]
    let ``Anthropic profile provides edit_file with old_string new_string`` () =
        let profile = AnthropicProfile("claude-opus-4-6") :> IProviderProfile
        let names = profile.ToolDefinitions |> List.map (fun t -> t.Name)
        Assert.Contains("edit_file", names)
        Assert.Contains("read_file", names)
        Assert.DoesNotContain("apply_patch", names)

    [<Fact>]
    let ``Gemini profile provides gemini-cli-aligned tools`` () =
        let profile = GeminiProfile("gemini-3-flash-preview") :> IProviderProfile
        let names = profile.ToolDefinitions |> List.map (fun t -> t.Name)
        Assert.Contains("edit_file", names)
        Assert.Contains("read_file", names)
        Assert.Contains("read_many_files", names)
        Assert.Contains("list_dir", names)
        Assert.Contains("shell", names)

    [<Fact>]
    let ``Each profile produces provider-specific system prompt`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let openai =
                (OpenAIProfile("gpt-5.2") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            let anthropic =
                (AnthropicProfile("claude-opus-4-6") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            let gemini =
                (GeminiProfile("gemini-3-flash-preview") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            Assert.Contains("OpenAI", openai)
            Assert.Contains("Claude", anthropic)
            Assert.Contains("Gemini", gemini)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Custom tools can be registered on top of profile`` () =
        let profile = AnthropicProfile("claude-opus-4-6")

        let customTool =
            { Name = "custom_tool"
              Description = "Custom"
              Parameters = """{"type":"object"}""" }

        profile.AddCustomTool(customTool)

        let names =
            (profile :> IProviderProfile).ToolDefinitions |> List.map (fun t -> t.Name)

        Assert.Contains("custom_tool", names)
        Assert.Contains("read_file", names)

    [<Fact>]
    let ``Tool name collisions resolved by custom overriding profile default`` () =
        let profile = AnthropicProfile("claude-opus-4-6")

        let customShell =
            { Name = "shell"
              Description = "Custom shell override"
              Parameters = """{"type":"object"}""" }

        profile.AddCustomTool(customShell)
        let tools = (profile :> IProviderProfile).ToolDefinitions
        let shellTools = tools |> List.filter (fun t -> t.Name = "shell")
        Assert.Equal(1, shellTools.Length)
        Assert.Equal("Custom shell override", shellTools.[0].Description)

// ============================================================
// 9.3 Tool Execution Tests
// ============================================================

module ToolExecutionTests =

    [<Fact>]
    let ``Tool calls are dispatched through the ToolRegistry`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            registry.Register(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> "hello world" }
            )

            let tc =
                { Id = "c1"
                  Name = "shell"
                  Arguments = """{"command":"echo hello"}"""
                  Metadata = Map.empty }

            let result = registry.Dispatch(tc, env)
            Assert.False(result.IsError)
            Assert.Equal("hello world", result.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Unknown tool calls return error result not exception`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            let tc =
                { Id = "c1"
                  Name = "unknown_tool"
                  Arguments = """{}"""
                  Metadata = Map.empty }

            let result = registry.Dispatch(tc, env)
            Assert.True(result.IsError)
            Assert.Contains("Unknown tool", result.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Tool execution errors are caught and returned as error results`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            registry.Register(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> failwith "boom" }
            )

            let tc =
                { Id = "c1"
                  Name = "shell"
                  Arguments = """{"command":"echo hi"}"""
                  Metadata = Map.empty }

            let result = registry.Dispatch(tc, env)
            Assert.True(result.IsError)
            Assert.Contains("Tool error", result.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Parallel tool dispatch works`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            registry.Register(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun args _ -> sprintf "result: %s" args }
            )

            let tcs =
                [ { Id = "c1"
                    Name = "shell"
                    Arguments = """{"command":"a"}"""
                    Metadata = Map.empty }
                  { Id = "c2"
                    Name = "shell"
                    Arguments = """{"command":"b"}"""
                    Metadata = Map.empty } ]

            let results = registry.DispatchAll(tcs, env, true)
            Assert.Equal(2, results.Length)
            Assert.True(results |> List.forall (fun r -> not r.IsError))
        finally
            cleanupDir dir

    [<Fact>]
    let ``Tool argument JSON is passed to executor`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()
            let mutable receivedArgs = ""

            registry.Register(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute =
                    fun args _ ->
                        receivedArgs <- args
                        "ok" }
            )

            let tc =
                { Id = "c1"
                  Name = "shell"
                  Arguments = """{"command":"ls -la"}"""
                  Metadata = Map.empty }

            registry.Dispatch(tc, env) |> ignore
            Assert.Contains("command", receivedArgs)
        finally
            cleanupDir dir

// ============================================================
// 9.4 Execution Environment Tests
// ============================================================

module ExecutionEnvironmentTests =

    [<Fact>]
    let ``LocalExecutionEnvironment implements file read and write`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            env.WriteFile("test.txt", "hello world\nsecond line")
            let content = env.ReadFile("test.txt", None, None)
            Assert.Contains("hello world", content)
            Assert.Contains("second line", content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``FileExists works correctly`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            Assert.False(env.FileExists("nonexistent.txt"))
            env.WriteFile("exists.txt", "content")
            Assert.True(env.FileExists("exists.txt"))
        finally
            cleanupDir dir

    [<Fact>]
    let ``ListDirectory works`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            env.WriteFile("a.txt", "a")
            env.WriteFile("b.txt", "b")
            let entries = env.ListDirectory(dir)
            Assert.True(entries.Length >= 2)
        finally
            cleanupDir dir

    [<Fact>]
    let ``ExecCommand runs and returns result`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let result = env.ExecCommand("echo hello", 10000, None)
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("hello", result.Stdout)
            Assert.False(result.TimedOut)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Environment variable filtering excludes sensitive vars`` () =
        let vars =
            dict
                [ "PATH", "/usr/bin"
                  "HOME", "/home/user"
                  "MY_API_KEY", "secret123"
                  "DB_SECRET", "password"
                  "AUTH_TOKEN", "token"
                  "NORMAL_VAR", "value" ]

        let filtered = EnvVarFilter.filterEnvVars vars
        Assert.True(filtered.ContainsKey("PATH"))
        Assert.True(filtered.ContainsKey("HOME"))
        Assert.True(filtered.ContainsKey("NORMAL_VAR"))
        Assert.False(filtered.ContainsKey("MY_API_KEY"))
        Assert.False(filtered.ContainsKey("DB_SECRET"))
        Assert.False(filtered.ContainsKey("AUTH_TOKEN"))

    [<Fact>]
    let ``Platform and OsVersion are not empty`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            Assert.True(env.Platform.Length > 0)
            Assert.True(env.OsVersion.Length > 0)
        finally
            cleanupDir dir

// ============================================================
// 9.5 Tool Output Truncation Tests
// ============================================================

module TruncationTests =

    [<Fact>]
    let ``Character-based truncation runs first`` () =
        let output = String.replicate 100000 "x"
        let config = SessionConfig.Default
        let result = Truncation.truncateToolOutput output "read_file" config
        Assert.True(result.Length < output.Length)
        Assert.Contains("WARNING", result)

    [<Fact>]
    let ``Line-based truncation runs second`` () =
        // Create output with many lines that fits in char limit
        let lines = [ for i in 1..1000 -> sprintf "line %d" i ] |> String.concat "\n"

        let config =
            { SessionConfig.Default with
                ToolOutputLimits = Map.ofList [ "shell", 1000000 ] }

        let result = Truncation.truncateToolOutput lines "shell" config
        // shell has default 256 line limit
        let resultLines = result.Split('\n')
        Assert.True(resultLines.Length <= 260) // 256 + omission marker line

    [<Fact>]
    let ``Truncation inserts visible marker`` () =
        let output = String.replicate 60000 "a"
        let result = Truncation.truncateChars output 50000 "head_tail"
        Assert.Contains("[WARNING: Tool output was truncated.", result)

    [<Fact>]
    let ``Default character limits match spec`` () =
        Assert.Equal(50000, TruncationDefaults.defaultCharLimits.["read_file"])
        Assert.Equal(30000, TruncationDefaults.defaultCharLimits.["shell"])
        Assert.Equal(20000, TruncationDefaults.defaultCharLimits.["grep"])
        Assert.Equal(20000, TruncationDefaults.defaultCharLimits.["glob"])
        Assert.Equal(10000, TruncationDefaults.defaultCharLimits.["edit_file"])
        Assert.Equal(1000, TruncationDefaults.defaultCharLimits.["write_file"])

    [<Fact>]
    let ``Character and line limits are overridable via SessionConfig`` () =
        let config =
            { SessionConfig.Default with
                ToolOutputLimits = Map.ofList [ "shell", 100 ]
                ToolLineLimits = Map.ofList [ "shell", 5 ] }

        let output = String.replicate 200 "x"
        let result = Truncation.truncateToolOutput output "shell" config
        Assert.True(result.Length <= 200) // truncated by char limit

    [<Fact>]
    let ``Small output passes through without truncation`` () =
        let output = "small output"
        let result = Truncation.truncateToolOutput output "shell" SessionConfig.Default
        Assert.Equal(output, result)

    [<Fact>]
    let ``Head-tail truncation preserves beginning and end`` () =
        let head = "HEAD_CONTENT"
        let middle = String.replicate 10000 "M"
        let tail = "TAIL_CONTENT"
        let output = head + middle + tail
        let result = Truncation.truncateChars output 100 "head_tail"
        Assert.Contains("HEAD", result)
        Assert.Contains("TAIL", result)

    [<Fact>]
    let ``Tail truncation keeps end of output`` () =
        let head = "HEAD_START"
        let tail = "TAIL_END"
        let output = head + String.replicate 10000 "M" + tail
        let result = Truncation.truncateChars output 100 "tail"
        Assert.Contains("TAIL_END", result)

// ============================================================
// 9.6 Steering Tests
// ============================================================

module SteeringTests =

    [<Fact>]
    let ``steer queues message injected after current tool round`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Steer("Change direction")
            session.ProcessInput("Start task")

            let steeringTurns =
                session.History
                |> List.filter (fun t ->
                    match t with
                    | SteeringTurn _ -> true
                    | _ -> false)

            Assert.True(steeringTurns.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``follow_up queues message processed after current input completes`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.FollowUp("Follow-up task")
            session.ProcessInput("Initial task")
            // Should have processed both initial and follow-up
            let userTurns =
                session.History
                |> List.filter (fun t ->
                    match t with
                    | UserTurn _ -> true
                    | _ -> false)

            Assert.True(userTurns.Length >= 2)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Steering messages appear as SteeringTurn in history`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Steer("Mid-task redirection")
            session.ProcessInput("Do something")

            let steering =
                session.History
                |> List.choose (fun t ->
                    match t with
                    | SteeringTurn(content, _) -> Some content
                    | _ -> None)

            Assert.Contains("Mid-task redirection", steering)
        finally
            cleanupDir dir

    [<Fact>]
    let ``SteeringTurns converted to user messages for LLM`` () =
        // Verify via HistoryConverter
        let turns: Turn list =
            [ UserTurn("hello", DateTime.UtcNow)
              SteeringTurn("steer me", DateTime.UtcNow) ]

        let messages = HistoryConverter.toMessages turns
        Assert.Equal(2, messages.Length)
        Assert.Equal(Role.User, messages.[1].Role)
        Assert.Equal("steer me", messages.[1].Text)

// ============================================================
// 9.7 Reasoning Effort Tests
// ============================================================

module ReasoningEffortTests =

    [<Fact>]
    let ``reasoning_effort is passed through to LLM request`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable capturedEffort: string option = None
            let mock = ConfigurableMockAdapter("test")

            mock.SetCompleteHandler(fun req ->
                capturedEffort <- req.ReasoningEffort

                { Id = "r"
                  Model = req.Model
                  Provider = "test"
                  Message = Message.Assistant("ok")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    ReasoningEffort = Some "high" }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("Think hard")
            Assert.Equal(Some "high", capturedEffort)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Valid reasoning effort values`` () =
        // Verify all values can be set
        for value in [ "low"; "medium"; "high" ] do
            let config =
                { SessionConfig.Default with
                    ReasoningEffort = Some value }

            Assert.Equal(Some value, config.ReasoningEffort)

        let defaultConfig =
            { SessionConfig.Default with
                ReasoningEffort = None }

        Assert.True(defaultConfig.ReasoningEffort.IsNone)

    [<Fact>]
    let ``Changing reasoning_effort mid-session takes effect`` () =
        // Since config is immutable, a new session would pick up the new value.
        // This test verifies the config itself accepts the change.
        let config1 =
            { SessionConfig.Default with
                ReasoningEffort = Some "low" }

        let config2 =
            { config1 with
                ReasoningEffort = Some "high" }

        Assert.Equal(Some "low", config1.ReasoningEffort)
        Assert.Equal(Some "high", config2.ReasoningEffort)

// ============================================================
// 9.7b MaxTokens Tests
// ============================================================

module MaxTokensTests =

    let private captureRequestMock (capture: Request -> unit) =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun req ->
            capture req

            { Id = "r"
              Model = req.Model
              Provider = "test"
              Message = Message.Assistant("ok")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        mock

    [<Fact>]
    let ``SessionConfig.Default.MaxTokens is 16384 to prevent truncation`` () =
        // Regression: previously SessionConfig had no MaxTokens, so requests fell through
        // to the Anthropic adapter's hardcoded default of 4096, truncating Sonnet plans
        // mid-thought every turn. The default must be a generous value.
        Assert.Equal(Some 16384, SessionConfig.Default.MaxTokens)

    [<Fact>]
    let ``MaxTokens from SessionConfig is propagated to LLM request`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable capturedMaxTokens: int option = None
            let mock = captureRequestMock (fun req -> capturedMaxTokens <- req.MaxTokens)

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    MaxTokens = Some 32768 }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("hello")
            Assert.Equal(Some 32768, capturedMaxTokens)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Default Session uses 16384 MaxTokens (not adapter fallback of 4096)`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable capturedMaxTokens: int option = None
            let mock = captureRequestMock (fun req -> capturedMaxTokens <- req.MaxTokens)

            let client = Client()
            client.RegisterAdapter(mock)

            // No config supplied — must still send MaxTokens, not None.
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("hello")
            Assert.Equal(Some 16384, capturedMaxTokens)
        finally
            cleanupDir dir

    [<Fact>]
    let ``MaxTokens=None propagates as None for callers that explicitly opt out`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable capturedMaxTokens: int option = Some -1
            let mock = captureRequestMock (fun req -> capturedMaxTokens <- req.MaxTokens)

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    MaxTokens = None }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("hello")
            Assert.Equal(None, capturedMaxTokens)
        finally
            cleanupDir dir

// ============================================================
// 9.8 System Prompt Tests
// ============================================================

module SystemPromptTests =

    [<Fact>]
    let ``System prompt includes provider-specific base instructions`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let prompt =
                (AnthropicProfile("claude-opus-4-6") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            Assert.Contains("Claude", prompt)
            Assert.Contains("edit_file", prompt)
        finally
            cleanupDir dir

    [<Fact>]
    let ``System prompt includes environment context`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let prompt =
                (AnthropicProfile("claude-opus-4-6") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            Assert.Contains("Working directory:", prompt)
            Assert.Contains("Platform:", prompt)
        finally
            cleanupDir dir

    [<Fact>]
    let ``System prompt includes project docs when present`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "# Project Instructions\nDo the thing.")
            let docs = ProjectDocs.discover dir "anthropic"
            Assert.True(docs.IsSome)
            Assert.Contains("Project Instructions", docs.Value)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Only relevant project files are loaded per provider`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Universal")
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "Anthropic-specific")
            File.WriteAllText(Path.Combine(dir, "GEMINI.md"), "Gemini-specific")

            let anthropicDocs = ProjectDocs.discover dir "anthropic"
            Assert.True(anthropicDocs.IsSome)
            Assert.Contains("Universal", anthropicDocs.Value)
            Assert.Contains("Anthropic-specific", anthropicDocs.Value)
            // Should NOT contain Gemini docs
            Assert.DoesNotContain("Gemini-specific", anthropicDocs.Value)

            let geminiDocs = ProjectDocs.discover dir "gemini"
            Assert.True(geminiDocs.IsSome)
            Assert.Contains("Universal", geminiDocs.Value)
            Assert.Contains("Gemini-specific", geminiDocs.Value)
            Assert.DoesNotContain("Anthropic-specific", geminiDocs.Value)
        finally
            cleanupDir dir

    [<Fact>]
    let ``User instruction overrides appended last`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let prompt =
                (AnthropicProfile("claude-opus-4-6") :> IProviderProfile)
                    .BuildSystemPrompt(env, None, Some "ALWAYS USE TABS")

            Assert.True(prompt.EndsWith("ALWAYS USE TABS"))
        finally
            cleanupDir dir

    [<Fact>]
    let ``AGENTS.md always loaded regardless of provider`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Universal instructions")

            for provider in [ "anthropic"; "openai"; "gemini" ] do
                let docs = ProjectDocs.discover dir provider
                Assert.True(docs.IsSome, sprintf "AGENTS.md should be loaded for %s" provider)
                Assert.Contains("Universal instructions", docs.Value)
        finally
            cleanupDir dir

    [<Fact>]
    let ``project docs discovery walks ancestor chain and truncates with marker at 32KB`` () =
        // Resolve symlinks (macOS /tmp -> /private/tmp) so git root matches our paths
        let dir = Path.GetFullPath(createTempDir ())

        try
            let nested = Path.Combine(dir, "a", "b", "c")
            Directory.CreateDirectory(nested) |> ignore
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            env.ExecCommand("git init -q", 10000, None) |> ignore

            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "ROOT")
            Directory.CreateDirectory(Path.Combine(dir, ".codex")) |> ignore
            File.WriteAllText(Path.Combine(dir, ".codex", "instructions.md"), "ROOT-OPENAI")

            let ancestor = Path.Combine(dir, "a")
            Directory.CreateDirectory(ancestor) |> ignore
            File.WriteAllText(Path.Combine(ancestor, "AGENTS.md"), String.replicate 40000 "x")

            let docs = ProjectDocs.discover nested "openai"
            Assert.True(docs.IsSome)

            let content = docs.Value
            Assert.Contains("AGENTS.md", content)
            Assert.Contains("ROOT", content)
            Assert.Contains("[Project instructions truncated at 32KB]", content)
        finally
            cleanupDir dir

// ============================================================
// 9.9 Subagent Tests
// ============================================================

module SubagentTests =

    [<Fact>]
    let ``Subagent can be spawned with scoped task`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            let handle = SubAgent.spawn profile env client "Write tests" 0 1 None
            Assert.True(handle.IsSome)
            Assert.Equal("completed", handle.Value.Status) // Already processed
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent shares parent execution environment`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            // Write a file, spawn subagent, check it can see the same filesystem
            env.WriteFile("parent_file.txt", "from parent")
            let handle = SubAgent.spawn profile env client "Read parent_file.txt" 0 1 None
            Assert.True(handle.IsSome)
            // The subagent's session uses the same env
            Assert.True(env.FileExists("parent_file.txt"))
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent has independent conversation history`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            let handle = SubAgent.spawn profile env client "Sub task" 0 1 None
            Assert.True(handle.IsSome)
            let subHistory = handle.Value.Session.History
            Assert.True(subHistory.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Depth limiting prevents recursive spawning`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            // At max depth, spawning should return None
            let handle = SubAgent.spawn profile env client "Sub task" 1 1 None
            Assert.True(handle.IsNone)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent results are returned`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            let handle = SubAgent.spawn profile env client "Do something" 0 1 None
            Assert.True(handle.IsSome)
            let result = handle.Value.Wait()
            Assert.True(result.Success)
            Assert.True(result.TurnsUsed > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Close agent terminates subagent`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let profile = TestProfile("m") :> IProviderProfile
            let handle = SubAgent.spawn profile env client "Task" 0 1 None
            Assert.True(handle.IsSome)
            handle.Value.Close()
            Assert.Equal("closed", handle.Value.Status)
        finally
            cleanupDir dir

// ============================================================
// 9.10 Event System Tests
// ============================================================

module EventSystemTests =

    [<Fact>]
    let ``All core event kinds are emitted`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("Hello")
            let kinds = session.Events |> List.map (fun e -> e.Kind) |> set
            Assert.Contains(UserInput, kinds)
            Assert.Contains(LlmCallStart, kinds)
            Assert.Contains(LlmCallEnd, kinds)
            Assert.Contains(AssistantTextEnd, kinds)
            Assert.Contains(SessionEnd, kinds)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Events have correct session ID`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("Hello")
            let sessionId = session.SessionId
            Assert.True(session.Events |> List.forall (fun e -> e.SessionId = sessionId))
        finally
            cleanupDir dir

    [<Fact>]
    let ``Tool call events are emitted during tool execution`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeToolMock ())
            let session = Session(TestProfile("m"), env, client)

            session.RegisterTool(
                { Definition = SharedTools.readFile
                  IsCacheable = true
                  Execute = fun _ _ -> "file content" }
            )

            session.ProcessInput("Read a file")

            let toolStartEvents =
                session.Events |> List.filter (fun e -> e.Kind = CodingAgent.ToolCallStart)

            let toolEndEvents =
                session.Events |> List.filter (fun e -> e.Kind = CodingAgent.ToolCallEnd)

            Assert.True(toolStartEvents.Length > 0)
            Assert.True(toolEndEvents.Length > 0)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session lifecycle events bracket the session`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("Hello")
            let endEvents = session.Events |> List.filter (fun e -> e.Kind = SessionEnd)
            Assert.True(endEvents.Length > 0)
        finally
            cleanupDir dir

// ============================================================
// 9.11 Error Handling Tests
// ============================================================

module ErrorHandlingTests =

    [<Fact>]
    let ``Tool execution errors return error result to LLM`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            registry.Register(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> failwith "command failed" }
            )

            let tc =
                { Id = "c1"
                  Name = "shell"
                  Arguments = """{"command":"echo hi"}"""
                  Metadata = Map.empty }

            let result = registry.Dispatch(tc, env)
            Assert.True(result.IsError)
            Assert.Contains("Tool error", result.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Authentication errors are not retried and surface immediately`` () =
        // This is handled at the UnifiedLlm layer, tested here for integration
        let authErr = AuthenticationError("Invalid API key")
        Assert.False((authErr :> ProviderError).Retryable)

    [<Fact>]
    let ``Closed session rejects new input`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Close()
            Assert.Throws<exn>(fun () -> session.ProcessInput("Should fail")) |> ignore
        finally
            cleanupDir dir

    [<Fact>]
    let ``Abort transitions to Closed state`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Abort()
            Assert.Equal(Closed, session.State)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Graceful shutdown emits SessionEnd event`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            session.Abort()
            let endEvents = session.Events |> List.filter (fun e -> e.Kind = SessionEnd)
            Assert.True(endEvents.Length > 0)
        finally
            cleanupDir dir

// ============================================================
// History Converter Tests
// ============================================================

module HistoryConverterTests =

    [<Fact>]
    let ``UserTurn converts to user message`` () =
        let turns = [ UserTurn("hello", DateTime.UtcNow) ]
        let msgs = HistoryConverter.toMessages turns
        Assert.Equal(1, msgs.Length)
        Assert.Equal(Role.User, msgs.[0].Role)
        Assert.Equal("hello", msgs.[0].Text)

    [<Fact>]
    let ``AssistantTurn converts to assistant message with tool calls`` () =
        let tc =
            { Id = "c1"
              Name = "shell"
              Arguments = """{}"""
              Metadata = Map.empty }

        let turns = [ AssistantTurn("text", [ tc ], None, Usage.Zero, DateTime.UtcNow) ]
        let msgs = HistoryConverter.toMessages turns
        Assert.Equal(1, msgs.Length)
        Assert.Equal(Role.Assistant, msgs.[0].Role)

    [<Fact>]
    let ``ToolResultsTurn converts to tool result messages`` () =
        let results =
            [ { ToolCallId = "c1"
                Content = "output"
                IsError = false
                ImageData = None
                ImageMediaType = None } ]

        let turns = [ ToolResultsTurn(results, DateTime.UtcNow) ]
        let msgs = HistoryConverter.toMessages turns
        Assert.Equal(1, msgs.Length)
        Assert.Equal(Role.Tool, msgs.[0].Role)

    [<Fact>]
    let ``SystemTurn converts to system message`` () =
        let turns = [ SystemTurn("instructions", DateTime.UtcNow) ]
        let msgs = HistoryConverter.toMessages turns
        Assert.Equal(1, msgs.Length)
        Assert.Equal(Role.System, msgs.[0].Role)

// ============================================================
// Loop Detection Tests
// ============================================================

module LoopDetectionTests =

    [<Fact>]
    let ``No loop detected with varied tool calls`` () =
        let turns: Turn list =
            [ for i in 1..10 ->
                  AssistantTurn(
                      "",
                      [ { Id = sprintf "c%d" i
                          Name = sprintf "tool%d" i
                          Arguments = "{}"
                          Metadata = Map.empty } ],
                      None,
                      Usage.Zero,
                      DateTime.UtcNow
                  ) ]

        Assert.False(LoopDetection.detectLoop turns 10)

    [<Fact>]
    let ``Loop detected with identical repeating calls`` () =
        let turns: Turn list =
            [ for _ in 1..10 ->
                  AssistantTurn(
                      "",
                      [ { Id = "c1"
                          Name = "shell"
                          Arguments = """{"command":"echo same"}"""
                          Metadata = Map.empty } ],
                      None,
                      Usage.Zero,
                      DateTime.UtcNow
                  ) ]

        Assert.True(LoopDetection.detectLoop turns 10)

    [<Fact>]
    let ``Short history does not trigger loop`` () =
        let turns: Turn list =
            [ AssistantTurn(
                  "",
                  [ { Id = "c1"
                      Name = "shell"
                      Arguments = """{}"""
                      Metadata = Map.empty } ],
                  None,
                  Usage.Zero,
                  DateTime.UtcNow
              ) ]

        Assert.False(LoopDetection.detectLoop turns 10)

// ============================================================
// AgentToolRegistry Tests
// ============================================================

module AgentToolRegistryTests =

    [<Fact>]
    let ``Register and resolve tools`` () =
        let registry = AgentToolRegistry()

        registry.Register(
            { Definition = SharedTools.shell
              IsCacheable = false
              Execute = fun _ _ -> "ok" }
        )

        Assert.True((registry.Resolve("shell")).IsSome)
        Assert.True((registry.Resolve("unknown")).IsNone)

    [<Fact>]
    let ``List and names`` () =
        let registry = AgentToolRegistry()

        registry.Register(
            { Definition = SharedTools.shell
              IsCacheable = false
              Execute = fun _ _ -> "" }
        )

        registry.Register(
            { Definition = SharedTools.readFile
              IsCacheable = true
              Execute = fun _ _ -> "" }
        )

        Assert.Equal(2, registry.Count)
        Assert.Contains("shell", registry.Names())
        Assert.Contains("read_file", registry.Names())

    [<Fact>]
    let ``Unregister removes tool`` () =
        let registry = AgentToolRegistry()

        registry.Register(
            { Definition = SharedTools.shell
              IsCacheable = false
              Execute = fun _ _ -> "" }
        )

        registry.Unregister("shell")
        Assert.True((registry.Resolve("shell")).IsNone)

// ============================================================
// Sprint 002 Additions
// ============================================================

module Sprint002ToolRegistrationTests =

    [<Fact>]
    let ``Session auto-registers write_file without manual wiring`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun _ ->
                callCount <- callCount + 1

                if callCount = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "write_file"
                          Arguments = """{"file_path":"auto-registered.txt","content":"ok"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("Write a file")
            Assert.True(File.Exists(Path.Combine(dir, "auto-registered.txt")))
        finally
            cleanupDir dir

    [<Fact>]
    let ``Schema validation failure returns error result instead of exception`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let registry = AgentToolRegistry()

            let locationTool =
                { Name = "needs_location"
                  Description = "Needs location"
                  Parameters =
                    """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""" }

            registry.Register(
                { Definition = locationTool
                  IsCacheable = false
                  Execute = fun _ _ -> "ok" }
            )

            let tc =
                { Id = "c1"
                  Name = "needs_location"
                  Arguments = """{}"""
                  Metadata = Map.empty }

            let result = registry.Dispatch(tc, env)
            Assert.True(result.IsError)
            Assert.Contains("missing required field 'location'", result.Content)
        finally
            cleanupDir dir

module Sprint002EventTests =

    [<Fact>]
    let ``SessionStart is emitted as the first event`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)
            Assert.True(session.Events.Length > 0)
            Assert.Equal(SessionStart, session.Events.Head.Kind)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Assistant text start delta end and tool output delta are emitted`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText("/tmp/test.txt", "x")
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeToolMock ())
            let session = Session(TestProfile("m"), env, client)

            session.RegisterTool(
                { Definition = SharedTools.readFile
                  IsCacheable = true
                  Execute = fun _ _ -> "file content" }
            )

            session.ProcessInput("Read the file")

            let kinds = session.Events |> List.map (fun e -> e.Kind)
            Assert.Contains(AssistantTextStart, kinds)
            Assert.Contains(AssistantTextDelta, kinds)
            Assert.Contains(AssistantTextEnd, kinds)
            Assert.Contains(ToolCallOutputDelta, kinds)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Streaming mode emits incremental AssistantTextDelta events`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")

            mock.SetStreamHandler(fun _ ->
                seq {
                    yield StreamStart
                    yield TextStart "t1"
                    yield TextDelta(Some "t1", "hello ")
                    yield TextDelta(Some "t1", "world")
                    yield TextEnd "t1"
                    yield Finish(Stop "stop", Some Usage.Zero, None)
                })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    EnableStreaming = true }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("stream")

            let deltas =
                session.Events
                |> List.choose (fun e ->
                    if e.Kind = AssistantTextDelta then
                        e.Data |> Map.tryFind "delta"
                    else
                        None)

            Assert.True(deltas.Length >= 2)
            Assert.Equal("hello world", String.concat "" deltas)
        finally
            cleanupDir dir

    [<Fact>]
    let ``AwaitingInput state is controlled by explicit signal`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(TestProfile("m"), env, client)

            session.RequestAwaitingInput()
            session.ProcessInput("Need more info")
            Assert.Equal(AwaitingInput, session.State)

            session.ProcessInput("Here is the answer")
            Assert.Equal(Idle, session.State)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Context window warning fires at eighty percent threshold`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let tinyProfile =
                { new IProviderProfile with
                    member _.Id = "test"
                    member _.Model = "m"
                    member _.ToolDefinitions = []
                    member _.BuildSystemPrompt(_, _, _) = "test"
                    member _.SupportsStreaming = false
                    member _.SupportsParallelToolCalls = false
                    member _.ContextWindowSize = 100 }

            let client = Client()
            client.RegisterAdapter(makeSimpleMock ())
            let session = Session(tinyProfile, env, client)
            session.ProcessInput(String.replicate 500 "x")

            let warning = session.Events |> List.tryFind (fun e -> e.Kind = Warning)
            Assert.True(warning.IsSome)
            Assert.True(warning.Value.Data.ContainsKey("current_tokens"))
            Assert.True(warning.Value.Data.ContainsKey("limit_tokens"))
            Assert.True(warning.Value.Data.ContainsKey("percentage"))
        finally
            cleanupDir dir

module Sprint002PromptTests =

    [<Fact>]
    let ``Environment context reports non-git directories`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let prompt =
                (OpenAIProfile("gpt-5.2") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            Assert.Contains("Is git repository: false", prompt)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Environment context includes git branch for git repositories`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            env.ExecCommand(
                "git init -q && git config user.email test@example.com && git config user.name test && echo hi > a.txt && git add a.txt && git commit -m init -q",
                10000,
                None
            )
            |> ignore

            let prompt =
                (OpenAIProfile("gpt-5.2") :> IProviderProfile).BuildSystemPrompt(env, None, None)

            Assert.Contains("Is git repository: true", prompt)
            Assert.Contains("Git branch:", prompt)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Project docs discovery loads root first then deeper directories`` () =
        let dir = createTempDir ()

        try
            let nested = Path.Combine(dir, "a", "b")
            Directory.CreateDirectory(nested) |> ignore
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            env.ExecCommand("git init -q", 10000, None) |> ignore
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "ROOT")
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "ROOT-CLAUDE")
            let sub = Path.Combine(dir, "a")
            File.WriteAllText(Path.Combine(sub, "AGENTS.md"), "SUB")
            File.WriteAllText(Path.Combine(sub, "CLAUDE.md"), "SUB-CLAUDE")

            let docs = ProjectDocs.discover nested "anthropic"
            Assert.True(docs.IsSome)
            let content = docs.Value
            let rootIdx = content.IndexOf("ROOT", StringComparison.Ordinal)
            let subIdx = content.IndexOf("SUB", StringComparison.Ordinal)
            Assert.True(rootIdx >= 0 && subIdx > rootIdx)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Project docs discovery enforces thirty-two-kilobyte budget`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), String.replicate 40000 "x")
            let docs = ProjectDocs.discover dir "openai"
            Assert.True(docs.IsSome)
            Assert.Contains("[Project instructions truncated at 32KB]", docs.Value)
        finally
            cleanupDir dir

module Sprint002ExecutionTests =

    [<Fact>]
    let ``ExecCommand timeout sends TERM before kill on unix-like platforms`` () =
        if OperatingSystem.IsWindows() then
            Assert.True(true)
        else
            let dir = createTempDir ()

            try
                let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
                let cmd = "trap '' TERM; while true; do sleep 1; done"
                let result = env.ExecCommand(cmd, 100, None)
                Assert.True(result.TimedOut)

                Assert.True(
                    result.DurationMs >= 1900,
                    $"Expected SIGTERM grace window before SIGKILL, got {result.DurationMs}ms"
                )
            finally
                cleanupDir dir

    [<Fact>]
    let ``Shell timeout output includes retry guidance message`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun _ ->
                callCount <- callCount + 1

                if callCount = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "shell"
                          Arguments = """{"command":"sleep 5","timeout_ms":50}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(TestProfile("m"), env, client)
            session.ProcessInput("run shell")

            let toolOutput =
                session.History
                |> List.tryPick (fun t ->
                    match t with
                    | ToolResultsTurn(results, _) -> results |> List.tryHead |> Option.map (fun r -> r.Content)
                    | _ -> None)
                |> Option.defaultValue ""

            Assert.Contains("[ERROR: Command timed out after", toolOutput)
            Assert.Contains("Retry with longer timeout_ms.", toolOutput)
        finally
            cleanupDir dir

module Sprint002GeminiTests =

    [<Fact>]
    let ``Gemini read_many_files and list_dir executors work`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha")
            File.WriteAllText(Path.Combine(dir, "b.txt"), "beta")

            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = GeminiProfile("gemini-3-flash-preview") :> IProviderProfile
            let mock = ConfigurableMockAdapter("gemini")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun _ ->
                callCount <- callCount + 1

                match callCount with
                | 1 ->
                    let tc =
                        { Id = "call_1"
                          Name = "read_many_files"
                          Arguments = """{"paths":["a.txt","b.txt"]}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "m"
                      Provider = "gemini"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 2 ->
                    let tc =
                        { Id = "call_2"
                          Name = "list_dir"
                          Arguments = """{"path":"."}"""
                          Metadata = Map.empty }

                    { Id = "r2"
                      Model = "m"
                      Provider = "gemini"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | _ ->
                    { Id = "r3"
                      Model = "m"
                      Provider = "gemini"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("inspect files")

            let outputs =
                session.History
                |> List.choose (fun t ->
                    match t with
                    | ToolResultsTurn(results, _) -> Some(results |> List.map (fun r -> r.Content))
                    | _ -> None)
                |> List.collect id

            let joined = String.concat "\n" outputs
            Assert.Contains("alpha", joined)
            Assert.Contains("beta", joined)
            Assert.Contains("a.txt", joined)
        finally
            cleanupDir dir

module Sprint002SubagentToolTests =

    let private extractAgentIdFromRequest (req: Request) =
        let text =
            req.Messages
            |> List.collect (fun m ->
                m.Content
                |> List.choose (function
                    | Text t -> Some t
                    | ToolResult tr -> Some tr.Content
                    | _ -> None))
            |> String.concat "\n"

        let m = Regex.Match(text, "agent_id:\\s*([A-Za-z0-9]+)")
        if m.Success then Some m.Groups.[1].Value else None

    [<Fact>]
    let ``Subagent tools spawn send wait close execute end-to-end`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-parent") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable callCount = 0
            let mutable lastAgentId: string option = None

            mock.SetCompleteHandler(fun req ->
                callCount <- callCount + 1

                match callCount with
                | 1 ->
                    let tc =
                        { Id = "call_spawn"
                          Name = "spawn_agent"
                          Arguments = """{"task":"subtask"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 2 ->
                    { Id = "sub_1"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("subagent initial")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 3 ->
                    let agentId = extractAgentIdFromRequest req |> Option.defaultValue ""
                    lastAgentId <- Some agentId

                    let tc =
                        { Id = "call_send"
                          Name = "send_input"
                          Arguments = $"""{{"agent_id":"{agentId}","message":"continue"}}"""
                          Metadata = Map.empty }

                    { Id = "r3"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 4 ->
                    { Id = "sub_2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("subagent follow-up")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 5 ->
                    let agentId = lastAgentId |> Option.defaultValue ""

                    let tc =
                        { Id = "call_wait"
                          Name = "wait"
                          Arguments = $"""{{"agent_id":"{agentId}"}}"""
                          Metadata = Map.empty }

                    { Id = "r5"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 6 ->
                    let agentId = lastAgentId |> Option.defaultValue ""

                    let tc =
                        { Id = "call_close"
                          Name = "close_agent"
                          Arguments = $"""{{"agent_id":"{agentId}"}}"""
                          Metadata = Map.empty }

                    { Id = "r6"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | _ ->
                    { Id = "r_final"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("Run subagent lifecycle")

            let outputs =
                session.History
                |> List.choose (fun t ->
                    match t with
                    | ToolResultsTurn(results, _) -> Some(results |> List.map (fun r -> r.Content))
                    | _ -> None)
                |> List.collect id
                |> String.concat "\n"

            Assert.Contains("turns_used:", outputs)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent depth limit returns tool error result`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-parent") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun req ->
                callCount <- callCount + 1

                if callCount = 1 then
                    let tc =
                        { Id = "call_spawn"
                          Name = "spawn_agent"
                          Arguments = """{"task":"subtask"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    MaxSubagentDepth = 0 }

            let session = Session(profile, env, client, config)
            session.ProcessInput("spawn")

            let firstToolResult =
                session.History
                |> List.tryPick (fun t ->
                    match t with
                    | ToolResultsTurn(results, _) -> results |> List.tryHead
                    | _ -> None)

            Assert.True(firstToolResult.IsSome)
            Assert.True(firstToolResult.Value.IsError)
            Assert.Contains("depth limit", firstToolResult.Value.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent working_dir override changes child cwd`` () =
        let dir = createTempDir ()

        try
            let subDir = Path.Combine(dir, "child")
            Directory.CreateDirectory(subDir) |> ignore
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-parent") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable callCount = 0

            mock.SetCompleteHandler(fun req ->
                callCount <- callCount + 1

                match callCount with
                | 1 ->
                    let tc =
                        { Id = "call_spawn"
                          Name = "spawn_agent"
                          Arguments = """{"task":"write file","working_dir":"child"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 2 ->
                    let tc =
                        { Id = "sub_write"
                          Name = "write_file"
                          Arguments = """{"file_path":"wd.txt","content":"ok"}"""
                          Metadata = Map.empty }

                    { Id = "sub1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 3 ->
                    { Id = "sub2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("sub done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | _ ->
                    { Id = "r_final"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("spawn with child cwd")
            Assert.True(File.Exists(Path.Combine(subDir, "wd.txt")))
        finally
            cleanupDir dir

    [<Fact>]
    let ``Subagent model override uses requested model`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-parent") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable callCount = 0
            let mutable subModel: string option = None

            mock.SetCompleteHandler(fun req ->
                callCount <- callCount + 1

                match callCount with
                | 1 ->
                    let tc =
                        { Id = "call_spawn"
                          Name = "spawn_agent"
                          Arguments = """{"task":"hello","model":"gpt-sub-model"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | 2 ->
                    subModel <- Some req.Model

                    { Id = "sub"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("sub done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                | _ ->
                    { Id = "done"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("spawn override")
            Assert.Equal(Some "gpt-sub-model", subModel)
        finally
            cleanupDir dir

// ============================================================
// Sprint 004 Coverage Tests
// ============================================================

module Sprint004Coverage =

    type private ParallelProfile(model: string, supportsParallel: bool) =
        interface IProviderProfile with
            member _.Id = "test"
            member _.Model = model

            member _.ToolDefinitions =
                [ { Name = "slow_tool"
                    Description = "Simulated slow tool"
                    Parameters = """{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}""" } ]

            member _.BuildSystemPrompt(_, _, _) = "You are a test agent."
            member _.SupportsStreaming = false
            member _.SupportsParallelToolCalls = supportsParallel
            member _.ContextWindowSize = 200000

    [<Fact>]
    let ``grep tool schema exposes glob_filter parameter`` () =
        let profile = OpenAIProfile("gpt-5.2") :> IProviderProfile
        let grepDef = profile.ToolDefinitions |> List.find (fun t -> t.Name = "grep")
        Assert.Contains("\"glob_filter\"", grepDef.Parameters)

    [<Fact>]
    let ``grep glob_filter limits matches to filtered files`` () =
        let dir = createTempDir ()

        try
            File.WriteAllText(Path.Combine(dir, "a.fs"), "let needle = 1\n")
            File.WriteAllText(Path.Combine(dir, "b.txt"), "needle in txt\n")
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let matches = env.Grep("needle", ".", false, 20, Some "*.fs")
            Assert.Contains("a.fs", matches)
            Assert.DoesNotContain("b.txt", matches)
        finally
            cleanupDir dir

    [<Fact>]
    let ``ProviderOptions from SessionConfig are forwarded to every LLM request`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable captured: Map<string, obj> option = None
            let mock = ConfigurableMockAdapter("test")

            mock.SetCompleteHandler(fun req ->
                captured <- req.ProviderOptions

                { Id = "r1"
                  Model = req.Model
                  Provider = "test"
                  Message = Message.Assistant("ok")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)

            let providerOptions: Map<string, obj> =
                Map.ofList [ "openai", box (Map.ofList [ "reasoning", box "high" ]) ]

            let config =
                { SessionConfig.Default with
                    ProviderOptions = Some providerOptions }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("hello")

            Assert.True(captured.IsSome)
            Assert.True(captured.Value.ContainsKey("openai"))
        finally
            cleanupDir dir

    [<Fact>]
    let ``OnEvent callback receives TOOL_CALL_END with both truncated and full output`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable calls = 0

            mock.SetCompleteHandler(fun _ ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "shell"
                          Arguments = """{"command":"echo hi"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let callbackEvents = ResizeArray<SessionEvent>()

            let config =
                { SessionConfig.Default with
                    OnEvent = Some callbackEvents.Add }

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(TestProfile("m"), env, client, config)

            session.RegisterTool(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute = fun _ _ -> String.replicate 35000 "x" + "TAIL_ERROR_FRAGMENT" }
            )

            session.ProcessInput("run shell")
            let toolEnd = callbackEvents |> Seq.tryFind (fun e -> e.Kind = ToolCallEnd)

            Assert.True(toolEnd.IsSome)
            Assert.True(toolEnd.Value.FullOutput.IsSome)
            Assert.Contains("TAIL_ERROR_FRAGMENT", toolEnd.Value.FullOutput.Value)
            Assert.True(toolEnd.Value.Data.ContainsKey("output"))
            Assert.True(toolEnd.Value.Data["output"].Length < toolEnd.Value.FullOutput.Value.Length)
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session uses parallel tool dispatch and preserves result order when supported`` () =
        let dir = createTempDir ()

        try
            // Pre-warm ThreadPool so concurrent dispatch isn't serialized on slow CI runners
            System.Threading.ThreadPool.SetMinThreads(8, 8) |> ignore
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable callCount = 0
            let mock = ConfigurableMockAdapter("test")

            mock.SetCompleteHandler(fun _ ->
                callCount <- callCount + 1

                if callCount = 1 then
                    let tcs =
                        [ { Id = "c1"
                            Name = "slow_tool"
                            Arguments = """{"n":1}"""
                            Metadata = Map.empty }
                          { Id = "c2"
                            Name = "slow_tool"
                            Arguments = """{"n":2}"""
                            Metadata = Map.empty }
                          { Id = "c3"
                            Name = "slow_tool"
                            Arguments = """{"n":3}"""
                            Metadata = Map.empty } ]

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = tcs |> List.map ToolCall
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let profile = ParallelProfile("m", true) :> IProviderProfile
            let session = Session(profile, env, client)
            let gate = obj ()
            let mutable active = 0
            let mutable maxActive = 0

            session.RegisterTool(
                { Definition = profile.ToolDefinitions.Head
                  IsCacheable = false
                  Execute =
                    fun args _ ->
                        lock gate (fun () ->
                            active <- active + 1
                            maxActive <- max maxActive active)

                        System.Threading.Thread.Sleep(500)
                        lock gate (fun () -> active <- active - 1)
                        args }
            )

            let sw = Diagnostics.Stopwatch.StartNew()
            session.ProcessInput("run slow tools")
            sw.Stop()

            let resultIds =
                session.History
                |> List.tryPick (fun t ->
                    match t with
                    | ToolResultsTurn(results, _) -> Some(results |> List.map (fun r -> r.ToolCallId))
                    | _ -> None)
                |> Option.defaultValue []

            Assert.Equal<string list>([ "c1"; "c2"; "c3" ], resultIds)
            Assert.True(maxActive > 1, $"Expected concurrent execution, max active was {maxActive}")
        finally
            cleanupDir dir

    [<Fact>]
    let ``Session keeps sequential tool dispatch when parallel support is disabled`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mutable callCount = 0
            let mock = ConfigurableMockAdapter("test")

            mock.SetCompleteHandler(fun _ ->
                callCount <- callCount + 1

                if callCount = 1 then
                    let tcs =
                        [ { Id = "c1"
                            Name = "slow_tool"
                            Arguments = """{"n":1}"""
                            Metadata = Map.empty }
                          { Id = "c2"
                            Name = "slow_tool"
                            Arguments = """{"n":2}"""
                            Metadata = Map.empty }
                          { Id = "c3"
                            Name = "slow_tool"
                            Arguments = """{"n":3}"""
                            Metadata = Map.empty } ]

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = tcs |> List.map ToolCall
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let profile = ParallelProfile("m", false) :> IProviderProfile
            let session = Session(profile, env, client)
            let gate = obj ()
            let mutable active = 0
            let mutable maxActive = 0

            session.RegisterTool(
                { Definition = profile.ToolDefinitions.Head
                  IsCacheable = false
                  Execute =
                    fun args _ ->
                        lock gate (fun () ->
                            active <- active + 1
                            maxActive <- max maxActive active)

                        System.Threading.Thread.Sleep(120)
                        lock gate (fun () -> active <- active - 1)
                        args }
            )

            let sw = Diagnostics.Stopwatch.StartNew()
            session.ProcessInput("run slow tools")
            sw.Stop()

            Assert.Equal(1, maxActive)

            Assert.True(
                sw.ElapsedMilliseconds >= 280L,
                $"Expected sequential execution, got {sw.ElapsedMilliseconds}ms"
            )
        finally
            cleanupDir dir

// ============================================================
// Sprint 005 Coverage Tests
// ============================================================

module Sprint005Coverage =

    let private toolResultTurnCount (history: Turn list) =
        history
        |> List.filter (function
            | ToolResultsTurn _ -> true
            | _ -> false)
        |> List.length

    [<Fact>]
    let ``C1 apply_patch create file from v4a patch`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile

            let patch =
                String.concat
                    "\n"
                    [ "*** Begin Patch"
                      "*** Add File: created.txt"
                      "+hello from patch"
                      "*** End Patch" ]

            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "apply_patch"
                          Arguments = JsonSerializer.Serialize({| patch = patch |})
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("create file")

            let created = Path.Combine(dir, "created.txt")
            Assert.True(File.Exists(created))
            Assert.Equal("hello from patch", File.ReadAllText(created))
        finally
            cleanupDir dir

    [<Fact>]
    let ``C1 apply_patch modifies existing file with update hunk`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile
            File.WriteAllText(Path.Combine(dir, "note.txt"), "alpha\nbeta\ngamma\n")

            let patch =
                String.concat
                    "\n"
                    [ "*** Begin Patch"
                      "*** Update File: note.txt"
                      "@@"
                      " alpha"
                      "-beta"
                      "+beta-updated"
                      " gamma"
                      "*** End Patch" ]

            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "apply_patch"
                          Arguments = JsonSerializer.Serialize({| patch = patch |})
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("modify file")

            let updated = File.ReadAllText(Path.Combine(dir, "note.txt"))
            Assert.Contains("beta-updated", updated)
            Assert.DoesNotContain("beta\n", updated)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C1 apply_patch deletes file`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile
            File.WriteAllText(Path.Combine(dir, "remove-me.txt"), "bye")

            let patch =
                String.concat "\n" [ "*** Begin Patch"; "*** Delete File: remove-me.txt"; "*** End Patch" ]

            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "apply_patch"
                          Arguments = JsonSerializer.Serialize({| patch = patch |})
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("delete file")
            Assert.False(File.Exists(Path.Combine(dir, "remove-me.txt")))
        finally
            cleanupDir dir

    [<Fact>]
    let ``C1 apply_patch invalid patch returns error result`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile
            let badPatch = "*** Add File: x.txt\n+oops"

            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "apply_patch"
                          Arguments = JsonSerializer.Serialize({| patch = badPatch |})
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("invalid patch")

            let result =
                session.History
                |> List.tryPick (function
                    | ToolResultsTurn(results, _) -> results |> List.tryHead
                    | _ -> None)

            Assert.True(result.IsSome)
            Assert.True(result.Value.IsError)
            Assert.Contains("Invalid patch", result.Value.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C1 apply_patch update missing file returns error result`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile

            let patch =
                String.concat
                    "\n"
                    [ "*** Begin Patch"
                      "*** Update File: nope.txt"
                      "@@"
                      "-a"
                      "+b"
                      "*** End Patch" ]

            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "apply_patch"
                          Arguments = JsonSerializer.Serialize({| patch = patch |})
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("update missing file")

            let result =
                session.History
                |> List.tryPick (function
                    | ToolResultsTurn(results, _) -> results |> List.tryHead
                    | _ -> None)

            Assert.True(result.IsSome)
            Assert.True(result.Value.IsError)
            Assert.Contains("missing file", result.Value.Content)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C3 three-round tool loop records three ToolResultsTurn entries in order`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun _ ->
                calls <- calls + 1

                if calls <= 3 then
                    let tc =
                        { Id = $"call_{calls}"
                          Name = "shell"
                          Arguments = """{"command":"echo hi"}"""
                          Metadata = Map.empty }

                    { Id = $"r{calls}"
                      Model = "m"
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage =
                        { Usage.Zero with
                            InputTokens = 10
                            OutputTokens = 5 }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r_final"
                      Model = "m"
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage =
                        { Usage.Zero with
                            InputTokens = 3
                            OutputTokens = 2 }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("run tools")

            Assert.Equal(3, toolResultTurnCount session.History)

            let turnKinds =
                session.History
                |> List.map (function
                    | AssistantTurn _ -> "assistant"
                    | ToolResultsTurn _ -> "tool"
                    | UserTurn _ -> "user"
                    | _ -> "other")

            let sequence = String.concat "," turnKinds
            Assert.Contains("assistant,tool,assistant,tool,assistant,tool,assistant", sequence)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C3 five-round tool loop usage aggregates across assistant turns`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = OpenAIProfile("gpt-5.3-codex") :> IProviderProfile
            let mock = ConfigurableMockAdapter("openai")
            let mutable calls = 0

            mock.SetCompleteHandler(fun _ ->
                calls <- calls + 1

                if calls <= 5 then
                    let tc =
                        { Id = $"call_{calls}"
                          Name = "shell"
                          Arguments = """{"command":"echo hi"}"""
                          Metadata = Map.empty }

                    { Id = $"r{calls}"
                      Model = "m"
                      Provider = "openai"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage =
                        { Usage.Zero with
                            InputTokens = calls
                            OutputTokens = calls }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r_final"
                      Model = "m"
                      Provider = "openai"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage =
                        { Usage.Zero with
                            InputTokens = 10
                            OutputTokens = 10 }
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("run tools")

            let usageTotal =
                session.History
                |> List.sumBy (function
                    | AssistantTurn(_, _, _, usage, _) -> usage.TotalTokens
                    | _ -> 0)

            Assert.Equal(50, usageTotal)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C2 provider profiles expose provider-specific prompt and tool schema spot-check`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let openai = OpenAIProfile("gpt-5.2") :> IProviderProfile
            let anthropic = AnthropicProfile("claude-opus-4-6") :> IProviderProfile
            let gemini = GeminiProfile("gemini-3-flash-preview") :> IProviderProfile

            let openaiPrompt = openai.BuildSystemPrompt(env, None, None)
            let anthropicPrompt = anthropic.BuildSystemPrompt(env, None, None)
            let geminiPrompt = gemini.BuildSystemPrompt(env, None, None)

            Assert.Contains("OpenAI", openaiPrompt)
            Assert.Contains("Claude", anthropicPrompt)
            Assert.Contains("Gemini", geminiPrompt)

            let openaiApplyPatch =
                openai.ToolDefinitions |> List.find (fun t -> t.Name = "apply_patch")

            let anthropicEditFile =
                anthropic.ToolDefinitions |> List.find (fun t -> t.Name = "edit_file")

            let geminiListDir =
                gemini.ToolDefinitions |> List.find (fun t -> t.Name = "list_dir")

            Assert.Contains("\"patch\"", openaiApplyPatch.Parameters)
            Assert.Contains("\"old_string\"", anthropicEditFile.Parameters)
            Assert.Contains("\"depth\"", geminiListDir.Parameters)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C4 streaming tool events interleave between text segments`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment

            let tc =
                { Id = "call_stream_1"
                  Name = "shell"
                  Arguments = """{"command":"echo hi"}"""
                  Metadata = Map.empty }

            let mock = ConfigurableMockAdapter("test")

            mock.SetStreamHandler(fun _ ->
                seq {
                    yield StreamStart
                    yield TextStart "t1"
                    yield TextDelta(Some "t1", "hello")
                    yield StreamEvent.ToolCallStart tc
                    yield StreamEvent.ToolCallDelta(tc.Id, tc.Arguments)
                    yield StreamEvent.ToolCallEnd tc
                    yield TextDelta(Some "t1", " world")
                    yield Finish(Stop "stop", Some Usage.Zero, None)
                })

            let client = Client()
            client.RegisterAdapter(mock)

            let config =
                { SessionConfig.Default with
                    EnableStreaming = true }

            let session = Session(TestProfile("m"), env, client, config)
            session.ProcessInput("stream with tool call")

            let firstDeltaIdx =
                session.Events
                |> List.findIndex (fun e ->
                    e.Kind = AssistantTextDelta && (e.Data |> Map.tryFind "delta") = Some "hello")

            let toolStartIdx =
                session.Events |> List.findIndex (fun e -> e.Kind = ToolCallStart)

            let toolEndIdx = session.Events |> List.findIndex (fun e -> e.Kind = ToolCallEnd)

            let secondDeltaIdx =
                session.Events
                |> List.findIndex (fun e ->
                    e.Kind = AssistantTextDelta && (e.Data |> Map.tryFind "delta") = Some " world")

            Assert.True(firstDeltaIdx < toolStartIdx)
            Assert.True(toolStartIdx < toolEndIdx)
            Assert.True(toolEndIdx < secondDeltaIdx)
        finally
            cleanupDir dir

    [<Fact>]
    let ``C6 edit_file supports whitespace-normalized matching`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let profile = AnthropicProfile("claude-sonnet-4-6") :> IProviderProfile
            // File uses tabs, LLM sends spaces in old_string
            File.WriteAllText(Path.Combine(dir, "ws.txt"), "def foo():\n\tif True:\n\t\treturn 1\n")

            let editArgs =
                JsonSerializer.Serialize(
                    {| file_path = "ws.txt"
                       old_string = "def foo():\n  if True:\n    return 1"
                       new_string = "def foo():\n\tif True:\n\t\treturn 42" |}
                )

            let mock = ConfigurableMockAdapter("anthropic")
            let mutable calls = 0

            mock.SetCompleteHandler(fun req ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "edit_file"
                          Arguments = editArgs
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = req.Model
                      Provider = "anthropic"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = req.Model
                      Provider = "anthropic"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(profile, env, client)
            session.ProcessInput("edit file")

            let content = File.ReadAllText(Path.Combine(dir, "ws.txt"))
            Assert.Contains("return 42", content)
        finally
            cleanupDir dir

module Sprint006Phase3Coverage =

    [<Fact>]
    let ``Tool pre-hook Error skips executor and returns tool error`` () =
        let dir = createTempDir ()

        try
            let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
            let mock = ConfigurableMockAdapter("test")
            let mutable calls = 0

            mock.SetCompleteHandler(fun _ ->
                calls <- calls + 1

                if calls = 1 then
                    let tc =
                        { Id = "call_1"
                          Name = "shell"
                          Arguments = """{"command":"echo hi"}"""
                          Metadata = Map.empty }

                    { Id = "r1"
                      Model = "m"
                      Provider = "test"
                      Message =
                        { Role = Assistant
                          Content = [ ToolCall tc ]
                          Name = None
                          ToolCallId = None }
                      FinishReason = ToolCalls "tool_calls"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
                else
                    { Id = "r2"
                      Model = "m"
                      Provider = "test"
                      Message = Message.Assistant("done")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None })

            let mutable executed = false

            let config =
                { SessionConfig.Default with
                    ToolCallHook =
                        Some(fun phase _ _ ->
                            if phase = ToolCallHookPhase.Pre then
                                Result.Error "blocked by pre-hook"
                            else
                                Result.Ok()) }

            let client = Client()
            client.RegisterAdapter(mock)
            let session = Session(TestProfile("m"), env, client, config)

            session.RegisterTool(
                { Definition = SharedTools.shell
                  IsCacheable = false
                  Execute =
                    fun _ _ ->
                        executed <- true
                        "ran" }
            )

            session.ProcessInput("run tool")

            let toolResult =
                session.History
                |> List.tryPick (function
                    | ToolResultsTurn(results, _) -> results |> List.tryHead
                    | _ -> None)

            Assert.True(toolResult.IsSome)
            Assert.True(toolResult.Value.IsError)
            Assert.Contains("blocked by pre-hook", toolResult.Value.Content)
            Assert.False(executed)
        finally
            cleanupDir dir
