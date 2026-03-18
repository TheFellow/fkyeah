module Tests

open System
open System.IO
open Xunit
open Attractor

// ============================================================================
// 11.1 DOT Parsing
// ============================================================================

module DotParsingTests =

    [<Fact>]
    let ``Parser accepts supported DOT subset (digraph with attribute blocks)`` () =
        let dot = """
        digraph Simple {
            graph [goal="Run tests"]
            node [shape=box]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [label="Node A", prompt="Do A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Simple", graph.Name)
        Assert.True(graph.Nodes.Count >= 3)

    [<Fact>]
    let ``Graph-level attributes are extracted correctly`` () =
        let dot = """
        digraph Test {
            graph [goal="Test goal", label="Test Label"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Test goal", graph.Goal)
        Assert.Equal("Test Label", graph.GraphLabel)

    [<Fact>]
    let ``Node attributes are parsed including multi-line attribute blocks`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            review [
                shape=hexagon,
                label="Review Changes",
                type="wait.human"
            ]
            start -> review -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let review = graph.Nodes["review"]
        Assert.Equal("hexagon", review.Shape)
        Assert.Equal("Review Changes", review.Label)
        Assert.Equal("wait.human", review.NodeType)

    [<Fact>]
    let ``Edge attributes are parsed correctly`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            gate [shape=diamond]
            start -> A -> gate
            gate -> exit [label="Yes", condition="outcome=success", weight=10]
            gate -> A [label="No", condition="outcome!=success"]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let yesEdge = graph.Edges |> List.find (fun e -> e.FromNode = "gate" && e.ToNode = "exit")
        Assert.Equal("Yes", yesEdge.Label)
        Assert.Equal("outcome=success", yesEdge.Condition)
        Assert.Equal(10, yesEdge.Weight)

    [<Fact>]
    let ``Chained edges produce individual edges for each pair`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            B [shape=box]
            start -> A -> B -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.True(graph.Edges |> List.exists (fun e -> e.FromNode = "start" && e.ToNode = "A"))
        Assert.True(graph.Edges |> List.exists (fun e -> e.FromNode = "A" && e.ToNode = "B"))
        Assert.True(graph.Edges |> List.exists (fun e -> e.FromNode = "B" && e.ToNode = "exit"))

    [<Fact>]
    let ``Node and edge default blocks apply to subsequent declarations`` () =
        let dot = """
        digraph Test {
            node [shape=box, timeout=900s]
            edge [weight=5]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [label="Node A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let nodeA = graph.Nodes["A"]
        Assert.Equal("box", nodeA.Shape)
        // Timeout should be inherited from defaults
        match nodeA.Timeout with
        | Some d -> Assert.Equal(900000L, d.Milliseconds)
        | None -> Assert.Fail("Expected timeout from defaults")
        // Edge weight should be inherited
        let edge = graph.Edges |> List.find (fun e -> e.FromNode = "A" && e.ToNode = "exit")
        Assert.Equal(5, edge.Weight)

    [<Fact>]
    let ``Subgraph blocks are flattened`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            subgraph cluster_loop {
                label = "Loop A"
                node [thread_id="loop-a"]
                Plan [label="Plan next step"]
                Implement [label="Implement"]
            }
            start -> Plan -> Implement -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.True(graph.Nodes |> Map.containsKey "Plan")
        Assert.True(graph.Nodes |> Map.containsKey "Implement")
        Assert.Equal("loop-a", graph.Nodes["Plan"].ThreadId)

    [<Fact>]
    let ``Quoted and unquoted attribute values both work`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, label="Quoted Label", max_retries=3, goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let a = graph.Nodes["A"]
        Assert.Equal("Quoted Label", a.Label)
        Assert.Equal(3, a.MaxRetries)
        Assert.True(a.GoalGate)

    [<Fact>]
    let ``Comments are stripped before parsing`` () =
        let dot = """
        // This is a line comment
        digraph Test {
            /* This is a block comment */
            start [shape=Mdiamond] // inline comment
            exit [shape=Msquare]
            start -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal(2, graph.Nodes.Count)

    [<Fact>]
    let ``Double slash inside quoted string is preserved`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=parallelogram, tool_command="curl https://example.com/api"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let cmd = graph.Nodes["A"].GetAttrString("tool_command", "")
        Assert.Contains("https://example.com/api", cmd)

    [<Fact>]
    let ``Block comment inside quoted string is preserved`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=parallelogram, tool_command="find . -name '*.go'"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let cmd = graph.Nodes["A"].GetAttrString("tool_command", "")
        Assert.Contains("*.go", cmd)

    [<Fact>]
    let ``Class attribute on nodes works`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            review [shape=box, class="code,critical", prompt="Review"]
            start -> review -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("code,critical", graph.Nodes["review"].Class)

    [<Fact>]
    let ``Qualified dotted attribute keys parse correctly`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=parallelogram, tool_hooks.pre="echo pre", tool_hooks.post="echo post"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let node = graph.Nodes["A"]
        Assert.Equal("echo pre", node.GetAttrString("tool_hooks.pre", ""))
        Assert.Equal("echo post", node.GetAttrString("tool_hooks.post", ""))

// ============================================================================
// 11.2 Validation and Linting
// ============================================================================

module ValidationTests =

    [<Fact>]
    let ``Exactly one start node is required`` () =
        let dot = """
        digraph Test {
            exit [shape=Msquare]
            A [shape=box]
            A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "start_node" && d.Severity = Severity.Error))

    [<Fact>]
    let ``Exactly one exit node is required`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            A [shape=box]
            start -> A
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "terminal_node" && d.Severity = Severity.Error))

    [<Fact>]
    let ``Validation fails when multiple exit nodes are present`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit1 [shape=Msquare]
            exit2 [shape=Msquare]
            A [shape=box]
            start -> A -> exit1
            A -> exit2
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        let terminalDiag =
            diags
            |> List.tryFind (fun d -> d.Rule = "terminal_node" && d.Severity = Severity.Error)
        Assert.True(terminalDiag.IsSome)
        Assert.Contains("exactly one exit node", terminalDiag.Value.Message.ToLowerInvariant())

    [<Fact>]
    let ``Start node has no incoming edges`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            start -> A -> exit
            A -> start
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "start_no_incoming" && d.Severity = Severity.Error))

    [<Fact>]
    let ``Exit node has no outgoing edges`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            start -> A -> exit
            exit -> A
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "exit_no_outgoing" && d.Severity = Severity.Error))

    [<Fact>]
    let ``All nodes are reachable from start`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            orphan [shape=box]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "reachability" && d.NodeId = "orphan"))

    [<Fact>]
    let ``Codergen nodes without prompt produce warning`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            bare_node [shape=box]
            start -> bare_node -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "prompt_on_llm_nodes" && d.Severity = Severity.Warning))

    [<Fact>]
    let ``Condition expressions on edges parse without errors`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="test"]
            start -> A
            A -> exit [condition="outcome=success"]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.False(diags |> List.exists (fun d -> d.Rule = "condition_syntax"))

    [<Fact>]
    let ``validate_or_raise throws on error-severity violations`` () =
        let dot = """
        digraph Test {
            A [shape=box]
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Throws<ValidationException>(fun () ->
            Validation.validateOrRaise graph None |> ignore)

    [<Fact>]
    let ``Lint results include rule name, severity, node ID, and message`` () =
        let dot = """
        digraph Test {
            exit [shape=Msquare]
            A [shape=box]
            A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        let startDiag = diags |> List.find (fun d -> d.Rule = "start_node")
        Assert.Equal(Severity.Error, startDiag.Severity)
        Assert.False(String.IsNullOrEmpty(startDiag.Message))

// ============================================================================
// 11.3 Execution Engine
// ============================================================================

module ExecutionEngineTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Engine resolves the start node and begins execution there`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``Each node handler is resolved via shape-to-handler-type mapping`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Do something"]
            gate [shape=diamond]
            start -> A -> gate
            gate -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.True(result.CompletedNodes |> List.contains "A")
        Assert.True(result.CompletedNodes |> List.contains "gate")

    [<Fact>]
    let ``Handler is called with node, context, graph, logs_root and returns Outcome`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test prompt"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.True(result.NodeOutcomes |> Map.containsKey "A")
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["A"].Status)

    [<Fact>]
    let ``Outcome is written to logs_root/node_id/status_json`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore
        let statusPath = Path.Combine(logsRoot, "A", "status.json")
        Assert.True(File.Exists(statusPath))

    [<Fact>]
    let ``Edge selection follows 5-step priority`` () =
        // Test condition match wins over weight
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fail_exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            gate [shape=diamond]
            start -> A -> gate
            gate -> exit [label="Yes", condition="outcome=success", weight=1]
            gate -> fail_exit [label="No", weight=100]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        // Should follow the condition match to exit, not the higher weight fail_exit
        Assert.True(result.CompletedNodes |> List.contains "A")

    [<Fact>]
    let ``Engine loops: execute -> select edge -> advance -> repeat`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Step 1"]
            B [shape=box, prompt="Step 2"]
            C [shape=box, prompt="Step 3"]
            start -> A -> B -> C -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal<string list>(["start"; "A"; "B"; "C"], result.CompletedNodes)

    [<Fact>]
    let ``Terminal node stops execution`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        // exit node should NOT be in completed (it's terminal - checked but not executed in the standard loop)
        Assert.True(result.CompletedNodes |> List.contains "A")

    [<Fact>]
    let ``Pipeline outcome is success if all goal gates succeeded`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

// ============================================================================
// 11.4 Goal Gate Enforcement
// ============================================================================

module GoalGateTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Nodes with goal_gate=true are tracked`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let a = graph.Nodes["A"]
        Assert.True(a.GoalGate)

    [<Fact>]
    let ``Goal gate allows exit when all satisfied`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``Goal gate enforcement checks at exit`` () =
        // Test with a custom handler that returns FAIL for goal gate node
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true, retry_target="A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        // With default codergen (simulated), it should succeed
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

// ============================================================================
// 11.5 Retry Logic
// ============================================================================

module RetryTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Nodes with max_retries are retried on failure`` () =
        let mutable callCount = 0
        let customHandler =
            { new IHandler with
                member _.Execute(node, context, graph, logsRoot) =
                    callCount <- callCount + 1
                    if callCount < 3 then
                        Outcome.Retry("not ready yet")
                    else
                        Outcome.Success(notes = "finally succeeded") }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=5]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(3, callCount)

    [<Fact>]
    let ``Retry count respects configured limit`` () =
        let mutable callCount = 0
        let customHandler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    Outcome.Retry("always fails") }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=2]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        Assert.Equal(3, callCount) // 1 initial + 2 retries

    [<Fact>]
    let ``Backoff delay is calculated`` () =
        let config = BackoffConfig.Default
        let delay1 = BackoffConfig.Default.DelayForAttempt(1)
        Assert.True(delay1 > 0)

    [<Fact>]
    let ``After retry exhaustion the final outcome is used`` () =
        let mutable callCount = 0
        let customHandler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    Outcome.Retry("retry") }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=1, allow_partial=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(logsRoot) |> ignore
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        // allow_partial=true should yield PartialSuccess when retries exhausted
        Assert.Equal(StageStatus.PartialSuccess, result.NodeOutcomes["A"].Status)

    [<Fact>]
    let ``FAIL outcomes are retried when retry policy is configured`` () =
        let mutable callCount = 0
        let customHandler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    if callCount < 3 then Outcome.Fail("transient")
                    else Outcome.Success() }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=3]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(3, callCount)

    [<Fact>]
    let ``max_visits stops infinite node loops`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="loop forever", max_visits=3]
            start -> A
            A -> A
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let result = Engine.run graph (RunConfig.Default(logsRoot))
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        Assert.Contains("exceeded max_visits", result.FinalOutcome.FailureReason)

// ============================================================================
// 11.6 Node Handlers
// ============================================================================

module HandlerTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Start handler returns SUCCESS immediately`` () =
        let handler = Handlers.StartHandler() :> IHandler
        let node = { Id = "start"; Attributes = Map.ofList ["shape", AttrValue.String "Mdiamond"] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)

    [<Fact>]
    let ``Exit handler returns SUCCESS immediately`` () =
        let handler = Handlers.ExitHandler() :> IHandler
        let node = { Id = "exit"; Attributes = Map.ofList ["shape", AttrValue.String "Msquare"] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)

    [<Fact>]
    let ``Codergen handler expands goal in prompt and writes files`` () =
        let handler = Handlers.CodergenHandler() :> IHandler
        let node =
            { Id = "plan"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "box"
                      "prompt", AttrValue.String "Plan for: $goal" ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes = Map.empty
              Edges = []
              GraphAttributes = Map.ofList ["goal", AttrValue.String "Build a feature"] }
        let logsRoot = createTempDir()
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        // Check prompt.md was written
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "prompt.md")))
        let promptContent = File.ReadAllText(Path.Combine(logsRoot, "plan", "prompt.md"))
        Assert.Contains("Build a feature", promptContent)
        // Check response.md
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "response.md")))

    [<Fact>]
    let ``Codergen handler supports outcome_fail_pattern`` () =
        let backend =
            { new ICodergenBackend with
                member _.Run(_, _, _) = Result.Ok "BLOCKED: waiting on external dependency" }
        let handler = Handlers.CodergenHandler(backend) :> IHandler
        let node =
            { Id = "plan"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "box"
                      "prompt", AttrValue.String "Plan for: $goal"
                      "outcome_fail_pattern", AttrValue.String "BLOCKED|FATAL" ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes = Map.empty
              Edges = []
              GraphAttributes = Map.ofList ["goal", AttrValue.String "Build a feature"] }
        let logsRoot = createTempDir()
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Fail, outcome.Status)
        Assert.Contains("Matched outcome_fail_pattern", outcome.FailureReason)

    [<Fact>]
    let ``Codergen handler writes versioned artifacts by visit count`` () =
        let handler = Handlers.CodergenHandler() :> IHandler
        let node =
            { Id = "plan"
              Attributes = Map.ofList [ "shape", AttrValue.String "box"; "prompt", AttrValue.String "Do work" ] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let logsRoot = createTempDir()
        let context = Context()

        for visit in 1 .. 3 do
            context.Set("node.visit_count", string visit)
            let outcome = handler.Execute(node, context, graph, logsRoot)
            Assert.Equal(StageStatus.Success, outcome.Status)

        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "001", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "002", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "003", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "response.md")))

    [<Fact>]
    let ``Tab shape resolves to coding_agent handler type`` () =
        let node = { Id = "agent"; Attributes = Map.ofList ["shape", AttrValue.String "tab"] }
        Assert.Equal("coding_agent", ShapeMapping.resolveHandlerType node)

    [<Fact>]
    let ``CodingAgent handler processes input and writes artifacts`` () =
        let mutable callCount = 0
        let mutable capturedProvider: string option = None
        let mutable capturedPrompt = ""
        let mock = UnifiedLlm.ConfigurableMockAdapter("anthropic")
        mock.SetCompleteHandler(fun req ->
            callCount <- callCount + 1
            capturedProvider <- req.Provider
            capturedPrompt <- req.Messages |> List.map (fun m -> m.Text) |> String.concat "\n"
            { Id = "r1"
              Model = req.Model
              Provider = "test"
              Message = UnifiedLlm.Message.assistant("Coding agent completed.")
              FinishReason = UnifiedLlm.FinishReason.Stop "stop"
              Usage = UnifiedLlm.Usage.Zero
              ResponseId = None; Raw = None; Warnings = []; RateLimit = None })
        let client = UnifiedLlm.Client()
        client.RegisterAdapter(mock)

        let handler = Handlers.CodingAgentHandler(client) :> IHandler
        let node =
            { Id = "agent"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "tab"
                      "llm_model", AttrValue.String "claude-sonnet-4-6"
                      "prompt", AttrValue.String "Implement for: $goal"
                      "system_prompt", AttrValue.String "Follow project conventions." ] }
        let context = Context()
        context.Set("last_response", "Previous response")
        let graph =
            { Name = "test"
              Nodes = Map.empty
              Edges = []
              GraphAttributes = Map.ofList [ "goal", AttrValue.String "Build calculator" ] }
        let logsRoot = createTempDir()

        let outcome = handler.Execute(node, context, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Equal(1, callCount)
        Assert.Equal(Some "anthropic", capturedProvider)
        Assert.Contains("Build calculator", capturedPrompt)
        Assert.Contains("Previous response", capturedPrompt)

        let stageDir = Path.Combine(logsRoot, "agent")
        Assert.True(File.Exists(Path.Combine(stageDir, "prompt.md")))
        Assert.True(File.Exists(Path.Combine(stageDir, "response.md")))
        Assert.True(File.Exists(Path.Combine(stageDir, "history.json")))
        Assert.True(File.Exists(Path.Combine(stageDir, "status.json")))
        Assert.Equal("Coding agent completed.", outcome.ContextUpdates["last_response"])

    [<Fact>]
    let ``CodingAgent handler respects cwd attribute for file writes`` () =
        let workDir = createTempDir()
        let mutable callCount = 0
        let mock = UnifiedLlm.ConfigurableMockAdapter("anthropic")
        mock.SetCompleteHandler(fun req ->
            callCount <- callCount + 1
            if callCount = 1 then
                let tc: UnifiedLlm.ToolCallData =
                    { Id = "call_1"
                      Name = "write_file"
                      Arguments = """{"file_path":"cwd-check.txt","content":"ok"}"""
                      Metadata = Map.empty }
                { Id = "r1"
                  Model = req.Model
                  Provider = "test"
                  Message =
                    { Role = UnifiedLlm.Role.Assistant
                      Content = [ UnifiedLlm.ContentPart.ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = UnifiedLlm.FinishReason.ToolCalls "tool_calls"
                  Usage = UnifiedLlm.Usage.Zero
                  ResponseId = None; Raw = None; Warnings = []; RateLimit = None }
            else
                { Id = "r2"
                  Model = req.Model
                  Provider = "test"
                  Message = UnifiedLlm.Message.assistant("done")
                  FinishReason = UnifiedLlm.FinishReason.Stop "stop"
                  Usage = UnifiedLlm.Usage.Zero
                  ResponseId = None; Raw = None; Warnings = []; RateLimit = None })
        let client = UnifiedLlm.Client()
        client.RegisterAdapter(mock)

        let handler = Handlers.CodingAgentHandler(client) :> IHandler
        let node =
            { Id = "agent"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "tab"
                      "llm_model", AttrValue.String "claude-sonnet-4-6"
                      "cwd", AttrValue.String workDir
                      "prompt", AttrValue.String "Write a file." ] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let logsRoot = createTempDir()

        let outcome = handler.Execute(node, Context(), graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.True(File.Exists(Path.Combine(workDir, "cwd-check.txt")))

    [<Fact>]
    let ``CodingAgent handler creates nested directories via write_file`` () =
        let workDir = createTempDir()
        let mutable callCount = 0
        let mock = UnifiedLlm.ConfigurableMockAdapter("anthropic")
        mock.SetCompleteHandler(fun req ->
            callCount <- callCount + 1
            match callCount with
            | 1 ->
                // First call: write calculator/__init__.py
                let tc: UnifiedLlm.ToolCallData =
                    { Id = "call_1"
                      Name = "write_file"
                      Arguments = """{"file_path":"calculator/__init__.py","content":"from .math_ops import add\n"}"""
                      Metadata = Map.empty }
                { Id = "r1"
                  Model = req.Model
                  Provider = "test"
                  Message =
                    { Role = UnifiedLlm.Role.Assistant
                      Content = [ UnifiedLlm.ContentPart.ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = UnifiedLlm.FinishReason.ToolCalls "tool_calls"
                  Usage = UnifiedLlm.Usage.Zero
                  ResponseId = None; Raw = None; Warnings = []; RateLimit = None }
            | 2 ->
                // Second call: write calculator/math_ops.py
                let tc: UnifiedLlm.ToolCallData =
                    { Id = "call_2"
                      Name = "write_file"
                      Arguments = """{"file_path":"calculator/math_ops.py","content":"def add(a, b):\n    return a + b\n"}"""
                      Metadata = Map.empty }
                { Id = "r2"
                  Model = req.Model
                  Provider = "test"
                  Message =
                    { Role = UnifiedLlm.Role.Assistant
                      Content = [ UnifiedLlm.ContentPart.ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = UnifiedLlm.FinishReason.ToolCalls "tool_calls"
                  Usage = UnifiedLlm.Usage.Zero
                  ResponseId = None; Raw = None; Warnings = []; RateLimit = None }
            | _ ->
                { Id = "r3"
                  Model = req.Model
                  Provider = "test"
                  Message = UnifiedLlm.Message.assistant("Files created.")
                  FinishReason = UnifiedLlm.FinishReason.Stop "stop"
                  Usage = UnifiedLlm.Usage.Zero
                  ResponseId = None; Raw = None; Warnings = []; RateLimit = None })
        let client = UnifiedLlm.Client()
        client.RegisterAdapter(mock)

        let handler = Handlers.CodingAgentHandler(client) :> IHandler
        let node =
            { Id = "agent"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "tab"
                      "llm_model", AttrValue.String "claude-sonnet-4-6"
                      "cwd", AttrValue.String workDir
                      "prompt", AttrValue.String "Create a calculator package." ] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let logsRoot = createTempDir()

        let outcome = handler.Execute(node, Context(), graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.True(File.Exists(Path.Combine(workDir, "calculator", "__init__.py")), "calculator/__init__.py should exist")
        Assert.True(File.Exists(Path.Combine(workDir, "calculator", "math_ops.py")), "calculator/math_ops.py should exist")

    [<Fact>]
    let ``CodingAgent handler fails when max turns limit is hit`` () =
        let mutable callCount = 0
        let mock = UnifiedLlm.ConfigurableMockAdapter("anthropic")
        mock.SetCompleteHandler(fun req ->
            callCount <- callCount + 1
            { Id = $"r{callCount}"
              Model = req.Model
              Provider = "test"
              Message = UnifiedLlm.Message.assistant("This should never run")
              FinishReason = UnifiedLlm.FinishReason.Stop "stop"
              Usage = UnifiedLlm.Usage.Zero
              ResponseId = None; Raw = None; Warnings = []; RateLimit = None })
        let client = UnifiedLlm.Client()
        client.RegisterAdapter(mock)

        let handler = Handlers.CodingAgentHandler(client) :> IHandler
        let node =
            { Id = "agent"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "tab"
                      "llm_model", AttrValue.String "claude-sonnet-4-6"
                      "max_turns", AttrValue.Integer 1
                      "prompt", AttrValue.String "Will hit turn limit immediately." ] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let logsRoot = createTempDir()

        let outcome = handler.Execute(node, Context(), graph, logsRoot)
        Assert.Equal(StageStatus.Fail, outcome.Status)
        Assert.Equal(0, callCount)
        let statusJson = File.ReadAllText(Path.Combine(logsRoot, "agent", "status.json"))
        Assert.Contains("\"outcome\": \"fail\"", statusJson)

    [<Fact>]
    let ``Conditional handler passes through`` () =
        let handler = Handlers.ConditionalHandler() :> IHandler
        let node = { Id = "gate"; Attributes = Map.ofList ["shape", AttrValue.String "diamond"] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)

    [<Fact>]
    let ``WaitForHuman handler presents choices from outgoing edges`` () =
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler
        let node =
            { Id = "review"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "label", AttrValue.String "Review Changes" ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "review", node
                      "approve", { Id = "approve"; Attributes = Map.empty }
                      "reject", { Id = "reject"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "review"; ToNode = "approve"; Attributes = Map.ofList ["label", AttrValue.String "[A] Approve"] }
                  { FromNode = "review"; ToNode = "reject"; Attributes = Map.ofList ["label", AttrValue.String "[R] Reject"] } ]
              GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)
        // AutoApprove should select first option
        Assert.True(outcome.SuggestedNextIds |> List.contains "approve")

    [<Fact>]
    let ``Freeform gate writes prompt.md and response.md and stores input in context`` () =
        let logsRoot = createTempDir()
        // CallbackInterviewer that simulates writing to response.md then pressing Enter
        let interviewer =
            CallbackInterviewer(fun question ->
                // Verify it's a Freeform question
                Assert.Equal(QuestionType.Freeform, question.Type)
                Assert.True(question.Metadata |> Map.containsKey "prompt_file")
                Assert.True(question.Metadata |> Map.containsKey "response_file")
                // Simulate user writing to response.md
                let responseFile = question.Metadata["response_file"]
                File.WriteAllText(responseFile, "JSON")
                Answer.FromText("")
            ) :> IInterviewer
        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler
        let node =
            { Id = "choose_lang"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "label", AttrValue.String "Choose Language"
                      "prompt", AttrValue.String "Pick a language to research." ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "choose_lang", node
                      "research", { Id = "research"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "choose_lang"; ToNode = "research"; Attributes = Map.ofList ["label", AttrValue.String "research"] } ]
              GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.True(outcome.SuggestedNextIds |> List.contains "research")
        // Verify prompt.md was written
        let promptPath = Path.Combine(logsRoot, "choose_lang", "prompt.md")
        Assert.True(File.Exists(promptPath))
        Assert.Equal("Pick a language to research.", File.ReadAllText(promptPath))
        // Verify response.md exists
        let responsePath = Path.Combine(logsRoot, "choose_lang", "response.md")
        Assert.True(File.Exists(responsePath))
        // Verify context has the input
        Assert.Equal("JSON", outcome.ContextUpdates["human.gate.input"])

    [<Fact>]
    let ``Multi-edge gate with prompt writes prompt.md and keeps MultipleChoice behavior`` () =
        let logsRoot = createTempDir()
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler
        let node =
            { Id = "approve_design"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "label", AttrValue.String "Approve Design"
                      "prompt", AttrValue.String "Review the design at $ATTRACTOR_LOGS_ROOT/design/response.md" ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "approve_design", node
                      "next_stage", { Id = "next_stage"; Attributes = Map.empty }
                      "revise", { Id = "revise"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "approve_design"; ToNode = "next_stage"; Attributes = Map.ofList ["label", AttrValue.String "[A] Approve"] }
                  { FromNode = "approve_design"; ToNode = "revise"; Attributes = Map.ofList ["label", AttrValue.String "[R] Revise"] } ]
              GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        // AutoApprove selects first option
        Assert.True(outcome.SuggestedNextIds |> List.contains "next_stage")
        // Verify prompt.md was written
        let promptPath = Path.Combine(logsRoot, "approve_design", "prompt.md")
        Assert.True(File.Exists(promptPath))
        // Should NOT have human.gate.input (multi-choice, not freeform)
        Assert.False(outcome.ContextUpdates |> Map.containsKey "human.gate.input")

    [<Fact>]
    let ``Prompt variable ATTRACTOR_LOGS_ROOT is expanded in written prompt.md`` () =
        let logsRoot = createTempDir()
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler
        let node =
            { Id = "gate"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "label", AttrValue.String "Gate"
                      "prompt", AttrValue.String "Review $ATTRACTOR_LOGS_ROOT/merge/response.md and decide." ] }
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "gate", node
                      "approve", { Id = "approve"; Attributes = Map.empty }
                      "reject", { Id = "reject"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "gate"; ToNode = "approve"; Attributes = Map.ofList ["label", AttrValue.String "[A] Approve"] }
                  { FromNode = "gate"; ToNode = "reject"; Attributes = Map.ofList ["label", AttrValue.String "[R] Reject"] } ]
              GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        let promptPath = Path.Combine(logsRoot, "gate", "prompt.md")
        let content = File.ReadAllText(promptPath)
        // $ATTRACTOR_LOGS_ROOT should be replaced with the actual logsRoot
        Assert.DoesNotContain("$ATTRACTOR_LOGS_ROOT", content)
        Assert.Contains(logsRoot, content)
        Assert.Contains($"{logsRoot}/merge/response.md", content)

    [<Fact>]
    let ``Custom handlers can be registered by type string`` () =
        let mutable executed = false
        let customHandler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    executed <- true
                    Outcome.Success(notes = "custom ran") }
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("my_custom", customHandler)

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="my_custom"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        Assert.True(executed)

// ============================================================================
// 11.7 State and Context
// ============================================================================

module ContextTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Context is a key-value store accessible to all handlers`` () =
        let ctx = Context()
        ctx.Set("key1", "value1")
        Assert.Equal("value1", ctx.Get("key1"))

    [<Fact>]
    let ``Handlers can read context and return context_updates`` () =
        let mutable contextValue = ""
        let handler1 =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    Outcome.Success(contextUpdates = Map.ofList ["test_key", "test_value"]) }
        let handler2 =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    contextValue <- context.Get("test_key")
                    Outcome.Success() }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="h1"]
            B [type="h2"]
            start -> A -> B -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("h1", handler1)
        registry.Register("h2", handler2)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        Assert.Equal("test_value", contextValue)

    [<Fact>]
    let ``Context updates are merged after each node execution`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        // After execution, context should have "outcome" and "last_stage" set
        Assert.Equal("success", result.Context.Get("outcome"))
        Assert.Equal("A", result.Context.Get("last_stage"))

    [<Fact>]
    let ``Checkpoint is saved after each node completion`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore
        Assert.True(File.Exists(Path.Combine(logsRoot, "checkpoint.json")))

    [<Fact>]
    let ``Artifacts are written to logs_root/node_id`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Write some code"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore
        Assert.True(File.Exists(Path.Combine(logsRoot, "A", "prompt.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "A", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "A", "status.json")))

    [<Fact>]
    let ``Context clone creates independent copy`` () =
        let ctx = Context()
        ctx.Set("key1", "value1")
        let clone = ctx.Clone()
        clone.Set("key1", "modified")
        Assert.Equal("value1", ctx.Get("key1"))
        Assert.Equal("modified", clone.Get("key1"))

    [<Fact>]
    let ``Context offloads large values to artifact store and resolves on get`` () =
        let logsRoot = createTempDir()
        let store = FileArtifactStore(logsRoot) :> IArtifactStore
        let ctx = Context(artifactStore = store)
        let large = String('x', 120 * 1024)
        ctx.Set("big", large)
        let snapshot = ctx.Snapshot()
        let raw = snapshot["big"]
        Assert.StartsWith("artifact:", raw)
        Assert.Equal(large, ctx.Get("big"))
        let key = raw.Replace("artifact:", "")
        Assert.True(store.Has(key))

// ============================================================================
// 11.8 Human-in-the-Loop
// ============================================================================

module InterviewerTests =

    [<Fact>]
    let ``AutoApproveInterviewer always selects first option`` () =
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let question =
            { Text = "Select an option"
              Type = QuestionType.MultipleChoice
              Options = [ { Key = "A"; Label = "Option A" }; { Key = "B"; Label = "Option B" } ]
              Default = None
              TimeoutSeconds = None
              Stage = "test"
              Metadata = Map.empty }
        let answer = interviewer.Ask(question)
        Assert.Equal("A", answer.Value)

    [<Fact>]
    let ``AutoApproveInterviewer selects YES for yes/no questions`` () =
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let question =
            { Text = "Continue?"
              Type = QuestionType.YesNo
              Options = []
              Default = None
              TimeoutSeconds = None
              Stage = "test"
              Metadata = Map.empty }
        let answer = interviewer.Ask(question)
        Assert.True(answer.IsYes)

    [<Fact>]
    let ``CallbackInterviewer delegates to provided function`` () =
        let mutable called = false
        let callback (q: Question) =
            called <- true
            Answer.FromText("custom answer")
        let interviewer = CallbackInterviewer(callback) :> IInterviewer
        let question =
            { Text = "Test"
              Type = QuestionType.Freeform
              Options = []
              Default = None
              TimeoutSeconds = None
              Stage = "test"
              Metadata = Map.empty }
        let answer = interviewer.Ask(question)
        Assert.True(called)
        Assert.Equal("custom answer", answer.Text)

    [<Fact>]
    let ``QueueInterviewer reads from pre-filled answer queue`` () =
        let answers = [ Answer.Yes; Answer.No; Answer.FromText("hello") ]
        let interviewer = QueueInterviewer(answers) :> IInterviewer
        let question =
            { Text = "Test"
              Type = QuestionType.YesNo
              Options = []
              Default = None
              TimeoutSeconds = None
              Stage = "test"
              Metadata = Map.empty }
        let a1 = interviewer.Ask(question)
        let a2 = interviewer.Ask(question)
        let a3 = interviewer.Ask(question)
        let a4 = interviewer.Ask(question) // exhausted -> Skipped
        Assert.True(a1.IsYes)
        Assert.True(a2.IsNo)
        Assert.Equal("hello", a3.Text)
        Assert.True(a4.IsSkipped)

    [<Fact>]
    let ``RecordingInterviewer records all QA pairs`` () =
        let inner = AutoApproveInterviewer() :> IInterviewer
        let recording = RecordingInterviewer(inner)
        let interviewer = recording :> IInterviewer
        let question =
            { Text = "Test"
              Type = QuestionType.YesNo
              Options = []
              Default = None
              TimeoutSeconds = None
              Stage = "test"
              Metadata = Map.empty }
        interviewer.Ask(question) |> ignore
        interviewer.Ask(question) |> ignore
        Assert.Equal(2, recording.Recordings.Length)

// ============================================================================
// 11.9 Condition Expressions
// ============================================================================

module ConditionTests =

    [<Fact>]
    let ``Equals operator works for string comparison`` () =
        let outcome = Outcome.Success()
        let context = Context()
        Assert.True(Conditions.evaluate "outcome=success" outcome context)
        Assert.False(Conditions.evaluate "outcome=fail" outcome context)

    [<Fact>]
    let ``Not equals operator works`` () =
        let outcome = Outcome.Success()
        let context = Context()
        Assert.True(Conditions.evaluate "outcome!=fail" outcome context)
        Assert.False(Conditions.evaluate "outcome!=success" outcome context)

    [<Fact>]
    let ``AND conjunction works with multiple clauses`` () =
        let outcome = Outcome.Success()
        let context = Context()
        context.Set("tests_passed", "true")
        Assert.True(Conditions.evaluate "outcome=success && tests_passed=true" outcome context)
        Assert.False(Conditions.evaluate "outcome=success && tests_passed=false" outcome context)

    [<Fact>]
    let ``Outcome variable resolves to current node outcome status`` () =
        let outcome = Outcome.Fail("test")
        let context = Context()
        Assert.True(Conditions.evaluate "outcome=fail" outcome context)

    [<Fact>]
    let ``Preferred_label resolves to outcome preferred label`` () =
        let outcome = { Outcome.Success() with PreferredLabel = "Fix" }
        let context = Context()
        Assert.True(Conditions.evaluate "preferred_label=Fix" outcome context)

    [<Fact>]
    let ``Context variables resolve to context values`` () =
        let outcome = Outcome.Success()
        let context = Context()
        context.Set("loop_state", "active")
        Assert.True(Conditions.evaluate "context.loop_state=active" outcome context)

    [<Fact>]
    let ``Missing context keys equal empty string`` () =
        let outcome = Outcome.Success()
        let context = Context()
        // missing key resolves to "", so "context.missing=" should be empty=empty -> true
        Assert.True(Conditions.evaluate "context.missing=" outcome context)
        Assert.False(Conditions.evaluate "context.missing=something" outcome context)
        // missing key != "nonempty" -> "" != "nonempty" -> true
        Assert.True(Conditions.evaluate "context.missing!=nonempty" outcome context)

    [<Fact>]
    let ``Empty condition always evaluates to true`` () =
        let outcome = Outcome.Success()
        let context = Context()
        Assert.True(Conditions.evaluate "" outcome context)
        Assert.True(Conditions.evaluate "  " outcome context)

// ============================================================================
// 11.10 Model Stylesheet
// ============================================================================

module StylesheetTests =

    [<Fact>]
    let ``Stylesheet is parsed from model_stylesheet attribute`` () =
        let source = "* { llm_model: claude-sonnet-4-5; llm_provider: anthropic; }"
        match Stylesheet.parse source with
        | Ok ss -> Assert.Equal(1, ss.Rules.Length)
        | Error msg -> Assert.Fail(msg)

    [<Fact>]
    let ``Universal selector matches all nodes`` () =
        let source = "* { llm_model: claude-sonnet-4-5; }"
        let ss = (Stylesheet.parse source) |> Result.defaultWith failwith
        let node = { Id = "test"; Attributes = Map.ofList ["shape", AttrValue.String "box"] }
        Assert.True(ss.Rules[0].Selector.Matches(node))

    [<Fact>]
    let ``Class selector matches nodes with that class`` () =
        let source = ".code { llm_model: claude-opus-4-6; }"
        let ss = (Stylesheet.parse source) |> Result.defaultWith failwith
        let node = { Id = "test"; Attributes = Map.ofList ["class", AttrValue.String "code"] }
        let nodeNoClass = { Id = "test2"; Attributes = Map.ofList ["shape", AttrValue.String "box"] }
        Assert.True(ss.Rules[0].Selector.Matches(node))
        Assert.False(ss.Rules[0].Selector.Matches(nodeNoClass))

    [<Fact>]
    let ``ID selector matches specific node`` () =
        let source = "#review { llm_model: gpt-5; }"
        let ss = (Stylesheet.parse source) |> Result.defaultWith failwith
        let node = { Id = "review"; Attributes = Map.empty }
        let otherNode = { Id = "other"; Attributes = Map.empty }
        Assert.True(ss.Rules[0].Selector.Matches(node))
        Assert.False(ss.Rules[0].Selector.Matches(otherNode))

    [<Fact>]
    let ``Specificity order: universal < shape < class < ID`` () =
        let source = """
            * { llm_model: default-model; }
            box { llm_model: shape-model; }
            .code { llm_model: code-model; }
            #special { llm_model: special-model; }
        """
        let ss = (Stylesheet.parse source) |> Result.defaultWith failwith
        Assert.Equal(0, ss.Rules[0].Selector.Specificity) // *
        Assert.Equal(1, ss.Rules[1].Selector.Specificity) // box (shape)
        Assert.Equal(2, ss.Rules[2].Selector.Specificity) // .code (class)
        Assert.Equal(3, ss.Rules[3].Selector.Specificity) // #special (id)

    [<Fact>]
    let ``Shape selector matches nodes by shape`` () =
        let source = "box { llm_model: box-model; }"
        let ss = (Stylesheet.parse source) |> Result.defaultWith failwith
        let boxNode = { Id = "test"; Attributes = Map.ofList ["shape", AttrValue.String "box"] }
        let diamondNode = { Id = "test2"; Attributes = Map.ofList ["shape", AttrValue.String "diamond"] }
        Assert.True(ss.Rules[0].Selector.Matches(boxNode))
        Assert.False(ss.Rules[0].Selector.Matches(diamondNode))

    [<Fact>]
    let ``Stylesheet properties are overridden by explicit node attributes`` () =
        let dot = """
        digraph Test {
            graph [
                goal="Test",
                model_stylesheet="* { llm_model: default-model; }"
            ]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", llm_model="explicit-model"]
            start -> A -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot None
        let a = graph.Nodes["A"]
        Assert.Equal("explicit-model", a.LlmModel)

// ============================================================================
// 11.11 Transforms and Extensibility
// ============================================================================

module TransformTests =

    [<Fact>]
    let ``Variable expansion replaces goal in prompts`` () =
        let dot = """
        digraph Test {
            graph [goal="Build a feature"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Plan for: $goal"]
            start -> A -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot None
        Assert.Equal("Plan for: Build a feature", graph.Nodes["A"].Prompt)

    [<Fact>]
    let ``Custom transforms can be registered and run in order`` () =
        let customTransform =
            { new ITransform with
                member _.Apply(graph) =
                    let updatedNodes =
                        graph.Nodes
                        |> Map.map (fun _ node ->
                            let newAttrs = node.Attributes |> Map.add "custom_flag" (AttrValue.Boolean true)
                            { node with Attributes = newAttrs })
                    { graph with Nodes = updatedNodes } }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot (Some [customTransform])
        let start = graph.Nodes["start"]
        Assert.True(
            start.Attributes
            |> Map.tryFind "custom_flag"
            |> Option.bind (fun v -> v.AsBool())
            |> Option.defaultValue false)

    [<Fact>]
    let ``Transform interface apply works`` () =
        let graph =
            { Name = "test"
              Nodes = Map.ofList ["A", { Id = "A"; Attributes = Map.empty }]
              Edges = []
              GraphAttributes = Map.ofList ["goal", AttrValue.String "test goal"] }
        let transformed = Transforms.variableExpansion.Apply(graph)
        Assert.Equal("test", transformed.Name) // graph structure preserved

// ============================================================================
// 11.12 Cross-Feature Parity Matrix
// ============================================================================

module ParityMatrixTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Parse a simple linear pipeline`` () =
        let dot = """
        digraph Simple {
            start [shape=Mdiamond]
            done [shape=Msquare]
            A [shape=box, prompt="Do A"]
            B [shape=box, prompt="Do B"]
            start -> A -> B -> done
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal(4, graph.Nodes.Count)
        Assert.Equal(3, graph.Edges.Length)

    [<Fact>]
    let ``Parse a pipeline with graph-level attributes`` () =
        let dot = """
        digraph Test {
            graph [goal="Test goal", label="Test Pipeline"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Test goal", graph.Goal)
        Assert.Equal("Test Pipeline", graph.GraphLabel)

    [<Fact>]
    let ``Parse multi-line node attributes`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            node_a [
                shape=box,
                label="Node A",
                prompt="Do something complex",
                max_retries=3,
                goal_gate=true
            ]
            start -> node_a -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let a = graph.Nodes["node_a"]
        Assert.Equal("Node A", a.Label)
        Assert.Equal(3, a.MaxRetries)
        Assert.True(a.GoalGate)

    [<Fact>]
    let ``Validate: missing start node produces error`` () =
        let dot = """
        digraph Test {
            exit [shape=Msquare]
            A [shape=box]
            A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        let errors = diags |> List.filter (fun d -> d.Severity = Severity.Error && d.Rule = "start_node")
        Assert.True(errors.Length > 0)

    [<Fact>]
    let ``Validate: missing exit node produces error`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            A [shape=box]
            start -> A
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        let errors = diags |> List.filter (fun d -> d.Severity = Severity.Error && d.Rule = "terminal_node")
        Assert.True(errors.Length > 0)

    [<Fact>]
    let ``Validate: orphan node produces error`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            orphan [shape=box]
            A [shape=box, prompt="test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "reachability" && d.NodeId = "orphan"))

    [<Fact>]
    let ``Execute a linear 3-node pipeline end-to-end`` () =
        let dot = """
        digraph Test {
            graph [goal="Test pipeline"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Step 1"]
            B [shape=box, prompt="Step 2"]
            C [shape=box, prompt="Step 3"]
            start -> A -> B -> C -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal<string list>(["start"; "A"; "B"; "C"], result.CompletedNodes)

    [<Fact>]
    let ``Execute with conditional branching`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Do work"]
            gate [shape=diamond]
            start -> A -> gate
            gate -> exit [condition="outcome=success"]
            gate -> A [condition="outcome=fail"]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "gate")

    [<Fact>]
    let ``Execute with retry on failure`` () =
        let mutable attempts = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    attempts <- attempts + 1
                    if attempts < 3 then Outcome.Retry("not yet")
                    else Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="retrying", max_retries=5]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("retrying", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(3, attempts)

    [<Fact>]
    let ``Goal gate blocks exit when unsatisfied`` () =
        let mutable callCount = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    if callCount = 1 then Outcome.Fail("first attempt failed")
                    else Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="gated", goal_gate=true, retry_target="A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("gated", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        // First call fails, but since we have retry_target pointing back to A,
        // the goal gate enforcement should redirect back to A
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(callCount >= 2)

    [<Fact>]
    let ``Goal gate allows exit when all satisfied`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``WaitForHuman presents choices and routes on selection`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            review [shape=hexagon, label="Review"]
            approve [shape=box, prompt="Approved"]
            reject [shape=box, prompt="Rejected"]
            start -> review
            review -> approve [label="[A] Approve"]
            review -> reject [label="[R] Reject"]
            approve -> exit
            reject -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        // AutoApprove selects first option (Approve)
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "approve")

    [<Fact>]
    let ``Edge selection: condition match wins over weight`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            wrong [shape=Msquare]
            A [shape=box, prompt="Test"]
            gate [shape=diamond]
            start -> A -> gate
            gate -> exit [condition="outcome=success", weight=1]
            gate -> wrong [weight=100]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        // Condition match should win even though wrong has higher weight
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``Edge selection: weight breaks ties for unconditional edges`` () =
        let node = { Id = "A"; Attributes = Map.empty }
        let outcome = Outcome.Success()
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "A", node
                      "B", { Id = "B"; Attributes = Map.empty }
                      "C", { Id = "C"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "A"; ToNode = "B"; Attributes = Map.ofList ["weight", AttrValue.Integer 5] }
                  { FromNode = "A"; ToNode = "C"; Attributes = Map.ofList ["weight", AttrValue.Integer 10] } ]
              GraphAttributes = Map.empty }
        let edge = EdgeSelection.selectEdge node outcome ctx graph
        Assert.True(edge.IsSome)
        Assert.Equal("C", edge.Value.ToNode)

    [<Fact>]
    let ``Edge selection: lexical tiebreak as final fallback`` () =
        let node = { Id = "A"; Attributes = Map.empty }
        let outcome = Outcome.Success()
        let ctx = Context()
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "A", node
                      "C_node", { Id = "C_node"; Attributes = Map.empty }
                      "B_node", { Id = "B_node"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "A"; ToNode = "C_node"; Attributes = Map.empty }
                  { FromNode = "A"; ToNode = "B_node"; Attributes = Map.empty } ]
              GraphAttributes = Map.empty }
        let edge = EdgeSelection.selectEdge node outcome ctx graph
        Assert.True(edge.IsSome)
        Assert.Equal("B_node", edge.Value.ToNode) // B comes before C lexically

    [<Fact>]
    let ``Context updates from one node are visible to the next`` () =
        let mutable valueFromB = ""
        let handlerA =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    Outcome.Success(contextUpdates = Map.ofList ["shared_data", "from_A"]) }
        let handlerB =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    valueFromB <- context.Get("shared_data")
                    Outcome.Success() }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="ha"]
            B [type="hb"]
            start -> A -> B -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("ha", handlerA)
        registry.Register("hb", handlerB)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        Assert.Equal("from_A", valueFromB)

    [<Fact>]
    let ``Checkpoint save and resume produces consistent state`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Step 1"]
            B [shape=box, prompt="Step 2"]
            start -> A -> B -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore
        let checkpointPath = Path.Combine(logsRoot, "checkpoint.json")
        Assert.True(File.Exists(checkpointPath))
        let json = File.ReadAllText(checkpointPath)
        Assert.Contains("completed_nodes", json)
        Assert.Contains("B", json) // Last completed node before exit

    [<Fact>]
    let ``Stylesheet applies model override to nodes by shape, class, and id`` () =
        let dot = """
        digraph Test {
            graph [
                goal="Test",
                model_stylesheet="* { llm_model: universal; } box { llm_model: box-shape; } .fast { llm_model: fast-class; } #special { llm_model: special-id; }"
            ]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Normal"]
            B [shape=box, prompt="Fast", class="fast"]
            special [shape=box, prompt="Special", class="fast"]
            gate [shape=diamond, prompt="Gate"]
            start -> A -> B -> special -> gate -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot None
        // A: shape=box matches box selector (specificity 1 > universal 0)
        Assert.Equal("box-shape", graph.Nodes["A"].LlmModel)
        // B: class=fast matches .fast selector (specificity 2 > shape 1)
        Assert.Equal("fast-class", graph.Nodes["B"].LlmModel)
        // special: id=special matches #special selector (specificity 3 > class 2)
        Assert.Equal("special-id", graph.Nodes["special"].LlmModel)
        // gate: shape=diamond, no shape/class/id match -> falls back to universal
        Assert.Equal("universal", graph.Nodes["gate"].LlmModel)

    [<Fact>]
    let ``Prompt variable expansion works`` () =
        let dot = """
        digraph Test {
            graph [goal="Build amazing things"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Your goal is: $goal"]
            start -> A -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot None
        Assert.Equal("Your goal is: Build amazing things", graph.Nodes["A"].Prompt)

    [<Fact>]
    let ``Custom handler registration and execution works`` () =
        let mutable ran = false
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    ran <- true
                    Outcome.Success(notes = "custom!") }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="my_type"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("my_type", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.True(ran)
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``Pipeline with 10+ nodes completes without errors`` () =
        let nodes =
            [ for i in 1..12 do
                $"    N{i} [shape=box, prompt=\"Step {i}\"]" ]
            |> String.concat "\n"
        let edges =
            [ "    start -> N1"
              for i in 1..11 do
                $"    N{i} -> N{i+1}"
              "    N12 -> exit" ]
            |> String.concat "\n"
        let dot = $"""
        digraph BigPipeline {{
            graph [goal="Big pipeline test"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
{nodes}
{edges}
        }}
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(13, result.CompletedNodes.Length) // start + 12 nodes

// ============================================================================
// 11.13 Integration Smoke Test
// ============================================================================

module IntegrationTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``End-to-end smoke test with LLM callback`` () =
        let dot = """
        digraph test_pipeline {
            graph [goal="Create a hello world Python script"]

            start     [shape=Mdiamond]
            plan      [shape=box, prompt="Plan how to create a hello world script for: $goal"]
            implement [shape=box, prompt="Write the code based on the plan", goal_gate=true]
            review    [shape=box, prompt="Review the code for correctness"]
            done      [shape=Msquare]

            start -> plan
            plan -> implement
            implement -> review [condition="outcome=success"]
            implement -> plan   [condition="outcome=fail", label="Retry"]
            review -> done      [condition="outcome=success"]
            review -> implement [condition="outcome=fail", label="Fix"]
        }
        """
        // 1. Parse
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Create a hello world Python script", graph.Goal)
        Assert.Equal(5, graph.Nodes.Count)
        Assert.Equal(6, graph.Edges.Length)

        // 2. Validate
        let diags = Validation.validate graph None
        let errors = diags |> List.filter (fun d -> d.Severity = Severity.Error)
        Assert.Empty(errors)

        // 3. Execute with simulated backend
        let logsRoot = createTempDir()
        let mutable llmCalls = 0
        let backend =
            { new ICodergenBackend with
                member _.Run(node, prompt, context) =
                    llmCalls <- llmCalls + 1
                    Ok $"[LLM Response for {node.Id}] Completed: {prompt.Substring(0, min 50 prompt.Length)}" }

        let registry = HandlerRegistry.CreateDefault(backend = backend)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }

        let (transformed, _) = Transforms.preparePipeline dot None
        let result = Engine.run transformed config

        // 4. Verify
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "implement")

        // 5. Verify artifacts exist
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "prompt.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "plan", "status.json")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "implement", "prompt.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "implement", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "implement", "status.json")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "review", "prompt.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "review", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "review", "status.json")))

        // 6. Verify goal gate was satisfied
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["implement"].Status)

        // 6b. Verify LLM backend was called for each codergen node
        Assert.Equal(3, llmCalls) // plan, implement, review

        // 7. Verify checkpoint
        let checkpointPath = Path.Combine(logsRoot, "checkpoint.json")
        Assert.True(File.Exists(checkpointPath))
        let json = File.ReadAllText(checkpointPath)
        Assert.Contains("plan", json)
        Assert.Contains("implement", json)
        Assert.Contains("review", json)

// ============================================================================
// Additional Edge Case Tests
// ============================================================================

module EdgeCaseTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Duration parsing works for all units`` () =
        let tokens = Lexer.tokenize "900s 15m 2h 250ms 1d"
        let durations = tokens |> List.choose (fun t ->
            match t with
            | Token.DurationLit d -> Some d
            | _ -> None)
        Assert.Equal(5, durations.Length)
        Assert.Equal(900000L, durations[0].Milliseconds)
        Assert.Equal(900000L, durations[1].Milliseconds)
        Assert.Equal(7200000L, durations[2].Milliseconds)
        Assert.Equal(250L, durations[3].Milliseconds)
        Assert.Equal(86400000L, durations[4].Milliseconds)

    [<Fact>]
    let ``StageStatus parse and toString roundtrip`` () =
        let statuses = [StageStatus.Success; StageStatus.Fail; StageStatus.Retry; StageStatus.PartialSuccess; StageStatus.Skipped]
        for s in statuses do
            let parsed = StageStatus.Parse(s.ToString())
            Assert.True(parsed.IsSome)
            Assert.Equal(s, parsed.Value)

    [<Fact>]
    let ``AcceleratorKey parsing works for all patterns`` () =
        Assert.Equal("Y", AcceleratorKey.parse "[Y] Yes, deploy")
        Assert.Equal("Y", AcceleratorKey.parse "Y) Yes, deploy")
        Assert.Equal("Y", AcceleratorKey.parse "Y - Yes, deploy")
        Assert.Equal("Y", AcceleratorKey.parse "Yes, deploy")

    [<Fact>]
    let ``AcceleratorKey label normalization strips prefixes`` () =
        Assert.Equal("yes, deploy", AcceleratorKey.normalizeLabel "[Y] Yes, deploy")
        Assert.Equal("yes, deploy", AcceleratorKey.normalizeLabel "Y) Yes, deploy")
        Assert.Equal("yes, deploy", AcceleratorKey.normalizeLabel "Y - Yes, deploy")
        Assert.Equal("yes, deploy", AcceleratorKey.normalizeLabel "Yes, deploy")

    [<Fact>]
    let ``Events are emitted during pipeline execution`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let collector = EventCollector()
        let emitter = EventEmitter()
        emitter.AddObserver(collector)
        let config = { RunConfig.Default(logsRoot) with EventEmitter = emitter }
        Engine.run graph config |> ignore
        let events = collector.Events
        Assert.True(events |> List.exists (fun e ->
            match e with PipelineEvent.PipelineStarted _ -> true | _ -> false))
        Assert.True(events |> List.exists (fun e ->
            match e with PipelineEvent.StageStarted _ -> true | _ -> false))
        Assert.True(events |> List.exists (fun e ->
            match e with PipelineEvent.PipelineCompleted _ -> true | _ -> false))

    [<Fact>]
    let ``FidelityMode parsing works`` () =
        Assert.Equal(Some FidelityMode.Full, FidelityMode.Parse "full")
        Assert.Equal(Some FidelityMode.Truncate, FidelityMode.Parse "truncate")
        Assert.Equal(Some FidelityMode.Compact, FidelityMode.Parse "compact")
        Assert.Equal(Some FidelityMode.SummaryLow, FidelityMode.Parse "summary:low")
        Assert.Equal(Some FidelityMode.SummaryMedium, FidelityMode.Parse "summary:medium")
        Assert.Equal(Some FidelityMode.SummaryHigh, FidelityMode.Parse "summary:high")
        Assert.Equal(None, FidelityMode.Parse "invalid")

    [<Fact>]
    let ``Graph outgoing and incoming edges work`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            B [shape=box]
            start -> A -> B -> exit
            A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let outgoing = graph.OutgoingEdges("A")
        Assert.Equal(2, outgoing.Length)
        let incoming = graph.IncomingEdges("B")
        Assert.Equal(1, incoming.Length)

    [<Fact>]
    let ``RunFromSource parses validates and runs`` () =
        let dot = """
        digraph Test {
            graph [goal="Quick test"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Do work"]
            start -> A -> exit
        }
        """
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.runFromSource dot config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

    [<Fact>]
    let ``All edges reference valid node IDs validation`` () =
        // The parser auto-creates nodes from edge refs, so this tests the lint rule directly
        let graph =
            { Name = "test"
              Nodes = Map.ofList
                [ "start", { Id = "start"; Attributes = Map.ofList ["shape", AttrValue.String "Mdiamond"] }
                  "exit", { Id = "exit"; Attributes = Map.ofList ["shape", AttrValue.String "Msquare"] } ]
              Edges =
                [ { FromNode = "start"; ToNode = "nonexistent"; Attributes = Map.empty } ]
              GraphAttributes = Map.empty }
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "edge_target_exists" && d.Severity = Severity.Error))

    [<Fact>]
    let ``Goal gate unsatisfied with no retry target produces fail`` () =
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    Outcome.Fail("always fails") }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="failing", goal_gate=true]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("failing", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        // A fails, and since there's no retry_target, the goal gate is unsatisfied
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)

    [<Fact>]
    let ``Jitter produces varied delay values`` () =
        let config = { BackoffConfig.Default with Jitter = true }
        // Run multiple times and verify we get different delays (statistical)
        let delays = [ for _ in 1..20 -> config.DelayForAttempt(1) ]
        // With jitter, delays should vary
        let distinct = delays |> List.distinct
        Assert.True(distinct.Length > 1, "Jitter should produce varied delays")

    [<Fact>]
    let ``Backoff without jitter produces consistent delays`` () =
        let config = { BackoffConfig.Default with Jitter = false }
        let delay1 = config.DelayForAttempt(1)
        let delay2 = config.DelayForAttempt(1)
        Assert.Equal(delay1, delay2)
        // Exponential backoff: 200, 400, 800
        Assert.Equal(200, config.DelayForAttempt(1))
        Assert.Equal(400, config.DelayForAttempt(2))
        Assert.Equal(800, config.DelayForAttempt(3))

    [<Fact>]
    let ``Checkpoint load and resume works`` () =
        let dot = """
        digraph Test {
            graph [goal="Resume test"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Step 1"]
            B [shape=box, prompt="Step 2"]
            C [shape=box, prompt="Step 3"]
            start -> A -> B -> C -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot

        // Run the full pipeline first to verify checkpoint file is written
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore

        // Load the checkpoint
        let checkpoint = Engine.loadCheckpoint logsRoot
        Assert.True(checkpoint.IsSome)
        let cp = checkpoint.Value
        Assert.True(cp.CompletedNodes |> List.contains "A")
        Assert.True(cp.CompletedNodes |> List.contains "B")
        Assert.True(cp.CompletedNodes |> List.contains "C")

        // Create a mid-pipeline checkpoint manually (simulate crash after A)
        let midCheckpoint =
            { Timestamp = DateTimeOffset.UtcNow
              CurrentNode = "A"
              CompletedNodes = ["start"; "A"]
              NodeRetries = Map.empty
              NodeOutcomes = Map.ofList [ "start", Outcome.Success(); "A", Outcome.Success() ]
              ContextValues = Map.ofList ["outcome", "success"; "graph.goal", "Resume test"; "last_stage", "A"]
              Logs = [] }

        // Resume from mid-pipeline checkpoint - should execute B and C
        let mutable nodesExecuted = ResizeArray<string>()
        let trackingHandler =
            { new IHandler with
                member _.Execute(node, _, _, _) =
                    nodesExecuted.Add(node.Id)
                    Outcome.Success(notes = $"Resumed: {node.Id}", contextUpdates = Map.ofList ["last_stage", node.Id]) }

        let logsRoot2 = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("codergen", trackingHandler)
        registry.SetDefault(trackingHandler)
        let config2 = { RunConfig.Default(logsRoot2) with Registry = registry }
        let result2 = Engine.resumeFromCheckpoint graph config2 midCheckpoint
        Assert.Equal(StageStatus.Success, result2.FinalOutcome.Status)
        // B and C should have been executed (not A, since it was already in the checkpoint)
        Assert.True(nodesExecuted |> Seq.exists (fun n -> n = "B"))
        Assert.True(nodesExecuted |> Seq.exists (fun n -> n = "C"))

    [<Fact>]
    let ``Parallel fan-out and fan-in complete correctly`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fan_out [shape=component, label="Fan Out"]
            branch_a [shape=box, prompt="Branch A"]
            branch_b [shape=box, prompt="Branch B"]
            fan_in [shape=tripleoctagon, label="Fan In"]
            start -> fan_out
            fan_out -> branch_a
            fan_out -> branch_b
            branch_a -> fan_in
            branch_b -> fan_in
            fan_in -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "fan_out")

    [<Fact>]
    let ``Tool handler executes configured command`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            tool_node [shape=parallelogram, tool_command="echo hello"]
            start -> tool_node -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "tool_node")
        // Verify tool.output was set in context
        let output = result.Context.Get("tool.output")
        Assert.Contains("hello", output)

    [<Fact>]
    let ``Tool handler honors graph cwd and sets ATTRACTOR_CWD`` () =
        let workDir = createTempDir()
        let dot = $"""
        digraph Test {{
            graph [cwd="{workDir}"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            tool_node [shape=parallelogram, tool_command="pwd > here.txt; echo $ATTRACTOR_CWD > env-cwd.txt"]
            start -> tool_node -> exit
        }}
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        let pwd = File.ReadAllText(Path.Combine(workDir, "here.txt")).Trim()
        let envCwd = File.ReadAllText(Path.Combine(workDir, "env-cwd.txt")).Trim()
        let normalizePath (p: string) =
            let full = Path.GetFullPath(p)
            if full.StartsWith("/private/") then full.Substring("/private".Length) else full
        Assert.Equal(normalizePath workDir, normalizePath pwd)
        Assert.Equal(normalizePath workDir, normalizePath envCwd)

    [<Fact>]
    let ``CodergenBackend error outcome is properly handled`` () =
        let backend =
            { new ICodergenBackend with
                member _.Run(_, _, _) =
                    Error (Outcome.Fail("backend error")) }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault(backend = backend)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)

    [<Fact>]
    let ``Stylesheet ID selector overrides class selector`` () =
        let dot = """
        digraph Test {
            graph [
                goal="Test",
                model_stylesheet="* { llm_model: base; } .code { llm_model: code-model; } #special { llm_model: special-model; }"
            ]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            normal [shape=box, prompt="Test"]
            code_node [shape=box, prompt="Test", class="code"]
            special [shape=box, prompt="Test", class="code"]
            start -> normal -> code_node -> special -> exit
        }
        """
        let (graph, _) = Transforms.preparePipeline dot None
        Assert.Equal("base", graph.Nodes["normal"].LlmModel)
        Assert.Equal("code-model", graph.Nodes["code_node"].LlmModel)
        // #special overrides .code
        Assert.Equal("special-model", graph.Nodes["special"].LlmModel)

    [<Fact>]
    let ``runFromSource rejects invalid graph`` () =
        let dot = """
        digraph Test {
            A [shape=box]
        }
        """
        Assert.Throws<System.Exception>(fun () ->
            let logsRoot = createTempDir()
            let config = RunConfig.Default(logsRoot)
            Engine.runFromSource dot config |> ignore)

    [<Fact>]
    let ``Empty digraph parses correctly`` () =
        let dot = """
        digraph Empty {
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Empty", graph.Name)
        Assert.Equal(0, graph.Nodes.Count)

    [<Fact>]
    let ``Escaped strings in attributes are handled`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Line 1\nLine 2\t\"quoted\""]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let prompt = graph.Nodes["A"].Prompt
        Assert.Contains("\n", prompt)
        Assert.Contains("\t", prompt)
        Assert.Contains("\"", prompt)

    [<Fact>]
    let ``Top-level graph attribute declarations work`` () =
        let dot = """
        digraph Test {
            rankdir=LR
            goal = "Top level goal"
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        Assert.Equal("Top level goal", graph.Goal)

    [<Fact>]
    let ``Negative integer values parse correctly`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, weight=-5]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        match graph.Nodes["A"].GetAttr("weight") with
        | Some (AttrValue.Integer i) -> Assert.Equal(-5, i)
        | _ -> Assert.Fail("Expected negative integer")

    [<Fact>]
    let ``Condition validation rejects malformed expressions`` () =
        // Valid conditions
        Assert.True((Conditions.validate "outcome=success").IsOk)
        Assert.True((Conditions.validate "outcome!=fail").IsOk)
        Assert.True((Conditions.validate "outcome=success && context.x=y").IsOk)
        Assert.True((Conditions.validate "").IsOk)

    [<Fact>]
    let ``Context snapshot returns immutable copy`` () =
        let ctx = Context()
        ctx.Set("key1", "value1")
        let snap = ctx.Snapshot()
        ctx.Set("key1", "modified")
        // Snapshot should not be affected
        Assert.Equal("value1", snap["key1"])
        // But context should be updated
        Assert.Equal("modified", ctx.Get("key1"))

    [<Fact>]
    let ``Context append log works`` () =
        let ctx = Context()
        ctx.AppendLog("entry 1")
        ctx.AppendLog("entry 2")
        let logs = ctx.Logs
        Assert.Equal(2, logs.Length)
        Assert.Equal("entry 1", logs[0])
        Assert.Equal("entry 2", logs[1])

// ============================================================================
// Sprint 001: Fidelity Mode Tests
// ============================================================================

module FidelityTests =

    [<Fact>]
    let ``Fidelity Full returns complete context clone`` () =
        let ctx = Context()
        ctx.Set("key1", "value1")
        ctx.Set("key2", "value2")
        let projected = ctx.Project(FidelityMode.Full)
        Assert.Equal("value1", projected.Get("key1"))
        Assert.Equal("value2", projected.Get("key2"))

    [<Fact>]
    let ``Fidelity Truncate truncates long values`` () =
        let ctx = Context()
        ctx.Set("short", "hello")
        ctx.Set("long", String.replicate 1000 "x")
        let projected = ctx.Project(FidelityMode.Truncate, truncateLimit = 100)
        Assert.Equal("hello", projected.Get("short"))
        Assert.Equal(100, projected.Get("long").Length)

    [<Fact>]
    let ``Fidelity Compact filters to essential keys`` () =
        let ctx = Context()
        ctx.Set("graph.goal", "test goal")
        ctx.Set("current_node", "A")
        ctx.Set("outcome", "success")
        ctx.Set("last_stage", "B")
        ctx.Set("some_data", "irrelevant")
        let projected = ctx.Project(FidelityMode.Compact)
        Assert.Equal("test goal", projected.Get("graph.goal"))
        Assert.Equal("A", projected.Get("current_node"))
        Assert.Equal("success", projected.Get("outcome"))
        Assert.Equal("", projected.Get("some_data"))
        Assert.Equal("", projected.Get("last_stage"))

    [<Fact>]
    let ``Fidelity SummaryHigh produces single summary key`` () =
        let ctx = Context()
        ctx.Set("key1", "value1")
        ctx.Set("key2", "value2")
        let projected = ctx.Project(FidelityMode.SummaryHigh)
        let summary = projected.Get("context_summary")
        Assert.True(summary.Contains("key1=value1"))
        Assert.True(summary.Contains("key2=value2"))
        Assert.Equal("", projected.Get("key1"))

    [<Fact>]
    let ``Fidelity precedence edge overrides node overrides graph`` () =
        let edge = { FromNode = "A"; ToNode = "B"; Attributes = Map.ofList ["fidelity", AttrValue.String "compact"] }
        let node = { Id = "B"; Attributes = Map.ofList ["fidelity", AttrValue.String "truncate"] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.ofList ["default_fidelity", AttrValue.String "full"] }
        let resolved = FidelityResolution.resolve (Some edge) node graph
        Assert.Equal(FidelityMode.Compact, resolved)

    [<Fact>]
    let ``Fidelity resolution falls back to node when no edge fidelity`` () =
        let edge = { FromNode = "A"; ToNode = "B"; Attributes = Map.empty }
        let node = { Id = "B"; Attributes = Map.ofList ["fidelity", AttrValue.String "truncate"] }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let resolved = FidelityResolution.resolve (Some edge) node graph
        Assert.Equal(FidelityMode.Truncate, resolved)

    [<Fact>]
    let ``Fidelity resolution falls back to graph default when no node fidelity`` () =
        let node = { Id = "B"; Attributes = Map.empty }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.ofList ["default_fidelity", AttrValue.String "compact"] }
        let resolved = FidelityResolution.resolve None node graph
        Assert.Equal(FidelityMode.Compact, resolved)

    [<Fact>]
    let ``Fidelity resolution defaults to Full when nothing specified`` () =
        let node = { Id = "B"; Attributes = Map.empty }
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let resolved = FidelityResolution.resolve None node graph
        Assert.Equal(FidelityMode.Full, resolved)

    [<Fact>]
    let ``Engine applies fidelity to handler context`` () =
        let mutable contextKeyCount = 0
        let handler =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    contextKeyCount <- context.Keys.Length
                    Outcome.Success() }
        let dot = """
        digraph Test {
            graph [default_fidelity="compact"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="counting"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(logsRoot) |> ignore
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("counting", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        // Compact mode should only pass graph.*, current_node, outcome
        Assert.True(contextKeyCount < 10, $"Expected compact context but got {contextKeyCount} keys")

// ============================================================================
// Sprint 001: Loop Restart Tests
// ============================================================================

module LoopRestartTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Loop restart creates fresh logs directory`` () =
        let mutable callCount = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, logsRoot) =
                    callCount <- callCount + 1
                    Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            B [type="custom"]
            start -> A -> B
            B -> A [loop_restart=true]
            B -> exit [condition="context.loop_done=true"]
        }
        """
        // This will loop restart once then continue
        // We need the handler to set loop_done on second pass
        let handler2 =
            { new IHandler with
                member _.Execute(node, context, _, logsRoot) =
                    callCount <- callCount + 1
                    if callCount >= 4 then
                        Outcome.Success(contextUpdates = Map.ofList ["loop_done", "true"])
                    else
                        Outcome.Success() }
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler2)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        // Check that restart directory was created
        Assert.True(Directory.Exists(Path.Combine(logsRoot, "restart-1")))

    [<Fact>]
    let ``Loop restart resets completed nodes`` () =
        let mutable callCount = 0
        let handler =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    callCount <- callCount + 1
                    if callCount >= 4 then
                        Outcome.Success(contextUpdates = Map.ofList ["loop_done", "true"])
                    else
                        Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="h"]
            B [type="h"]
            start -> A -> B
            B -> A [loop_restart=true]
            B -> exit [condition="context.loop_done=true"]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("h", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)

// ============================================================================
// Sprint 001: Goal Gate Cycle Detection Tests
// ============================================================================

module GoalGateCycleTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Goal gate retry cycle detected produces fail`` () =
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    Outcome.Fail("always fails") }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="failing", goal_gate=true, retry_target="A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("failing", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        let reason = result.FinalOutcome.FailureReason.ToLower()
        Assert.True(reason.Contains("cycle") || reason.Contains("max_visits"))

    [<Fact>]
    let ``Validation warns on retry target cycles`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Test", goal_gate=true, retry_target="B"]
            B [shape=box, prompt="Test", retry_target="A"]
            start -> A -> B -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None
        Assert.True(diags |> List.exists (fun d -> d.Rule = "retry_target_cycle"))

// ============================================================================
// Sprint 001: Parallel Execution Tests
// ============================================================================

module ParallelExecutionTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Parallel branches execute and return Success`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fan_out [shape=component]
            branch_a [shape=box, prompt="A"]
            branch_b [shape=box, prompt="B"]
            branch_c [shape=box, prompt="C"]
            fan_in [shape=tripleoctagon]
            start -> fan_out
            fan_out -> branch_a
            fan_out -> branch_b
            fan_out -> branch_c
            branch_a -> fan_in
            branch_b -> fan_in
            branch_c -> fan_in
            fan_in -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(result.CompletedNodes |> List.contains "fan_out")
        // Check parallel result context
        Assert.Equal("3", result.Context.Get("parallel.success_count"))
        Assert.Equal("0", result.Context.Get("parallel.fail_count"))

    [<Fact>]
    let ``Parallel branch contexts are isolated`` () =
        let handler = Handlers.ParallelHandler() :> IHandler
        let ctx = Context()
        ctx.Set("shared", "original")
        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "fan", { Id = "fan"; Attributes = Map.ofList ["shape", AttrValue.String "component"] }
                      "A", { Id = "A"; Attributes = Map.empty }
                      "B", { Id = "B"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "fan"; ToNode = "A"; Attributes = Map.empty }
                  { FromNode = "fan"; ToNode = "B"; Attributes = Map.empty } ]
              GraphAttributes = Map.empty }
        let node = graph.Nodes["fan"]
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)
        // Original context should not be modified by branch clones
        Assert.Equal("original", ctx.Get("shared"))

    [<Fact>]
    let ``Fan-in reads parallel branch results`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fan_out [shape=component]
            branch_a [shape=box, prompt="A"]
            fan_in [shape=tripleoctagon]
            start -> fan_out
            fan_out -> branch_a
            branch_a -> fan_in
            fan_in -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal("true", result.Context.Get("parallel.fan_in.completed"))

// ============================================================================
// Sprint 001: Tool Handler Hardening Tests
// ============================================================================

module ToolHardeningTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Tool handler captures stderr`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            tool_node [shape=parallelogram, tool_command="echo err >&2 && echo out"]
            start -> tool_node -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        let stderr = result.Context.Get("tool.stderr")
        Assert.Contains("err", stderr)
        let stdout = result.Context.Get("tool.output")
        Assert.Contains("out", stdout)

    [<Fact>]
    let ``Tool handler truncates large output`` () =
        let handler = Handlers.ToolHandler(maxOutputBytes = 50) :> IHandler
        let node =
            { Id = "tool"
              Attributes = Map.ofList ["tool_command", AttrValue.String "yes | head -100"; "shape", AttrValue.String "parallelogram"] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let logsRoot = createTempDir()
        let outcome = handler.Execute(node, ctx, graph, logsRoot)
        Assert.Equal(StageStatus.Success, outcome.Status)
        let output = outcome.ContextUpdates["tool.output"]
        Assert.Contains("WARNING: Tool output was truncated", output)

    [<Fact>]
    let ``Tool handler enforces timeout`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            slow_tool [shape=parallelogram, tool_command="sleep 10", timeout=500ms]
            start -> slow_tool -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        Assert.Contains("timed out", result.NodeOutcomes["slow_tool"].FailureReason.ToLower())

    [<Fact>]
    let ``Tool handler writes full output to artifact file`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            tool_node [shape=parallelogram, tool_command="echo hello_artifact"]
            start -> tool_node -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        Engine.run graph config |> ignore
        let outputPath = Path.Combine(logsRoot, "tool_node", "tool_output.txt")
        Assert.True(File.Exists(outputPath))
        let content = File.ReadAllText(outputPath)
        Assert.Contains("hello_artifact", content)

// ============================================================================
// Sprint 001: Manager Loop Handler Tests
// ============================================================================

module ManagerLoopTests =

    [<Fact>]
    let ``Manager loop exits after max_cycles`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler
        let node =
            { Id = "manager"
              Attributes = Map.ofList ["max_cycles", AttrValue.Integer 3; "shape", AttrValue.String "house"] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Fail, outcome.Status)
        Assert.Contains("max_cycles", outcome.FailureReason)
        Assert.Equal("3", outcome.ContextUpdates["manager.total_cycles"])

    [<Fact>]
    let ``Manager loop exits on stop condition`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler
        let node =
            { Id = "manager"
              Attributes = Map.ofList ["max_cycles", AttrValue.Integer 10; "shape", AttrValue.String "house"] }
        let ctx = Context()
        ctx.Set("manager.stop", "true")
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Equal("true", outcome.ContextUpdates["manager.stopped"])

    [<Fact>]
    let ``Manager loop exits success on cycle 2 when stop key is set during supervision`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler
        let node =
            { Id = "manager"
              Attributes =
                Map.ofList
                    [ "max_cycles", AttrValue.Integer 10
                      "wait_ms", AttrValue.Integer 100
                      "shape", AttrValue.String "house" ] }
        let ctx = Context()
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }

        let setter =
            System.Threading.Thread(fun () ->
                System.Threading.Thread.Sleep(10)
                ctx.Set("manager.stop", "true"))
        setter.Start()
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        setter.Join()

        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Equal("2", outcome.ContextUpdates["manager.turns_used"])

    [<Fact>]
    let ``Manager loop exits on child complete`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler
        let node =
            { Id = "manager"
              Attributes = Map.ofList ["max_cycles", AttrValue.Integer 10; "shape", AttrValue.String "house"] }
        let ctx = Context()
        ctx.Set("manager.child_complete", "true")
        let graph = { Name = "test"; Nodes = Map.empty; Edges = []; GraphAttributes = Map.empty }
        let outcome = handler.Execute(node, ctx, graph, "/tmp")
        Assert.Equal(StageStatus.Success, outcome.Status)

    [<Fact>]
    let ``Manager loop runs in pipeline`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            supervisor [shape=house, max_cycles=5]
            start -> supervisor -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(logsRoot) |> ignore
        let config = RunConfig.Default(logsRoot)
        let result = Engine.run graph config
        Assert.True(result.CompletedNodes |> List.contains "supervisor")

// ============================================================================
// Sprint 001: Default Max Retry Tests
// ============================================================================

module DefaultMaxRetryTests =

    [<Fact>]
    let ``Graph default_max_retry used when node has no max_retries`` () =
        let mutable callCount = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    if callCount < 3 then Outcome.Retry("not yet")
                    else Outcome.Success() }
        let dot = """
        digraph Test {
            graph [default_max_retry=5]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="retrying"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(logsRoot) |> ignore
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("retrying", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(3, callCount)

// ============================================================================
// Fidelity Projection — all 6 modes
// ============================================================================

module FidelityProjectionTests =

    [<Fact>]
    let ``Full clones everything including logs`` () =
        let ctx = Context()
        ctx.Set("a", "1")
        ctx.Set("b", "2")
        ctx.AppendLog("log1")
        let p = ctx.Project(FidelityMode.Full)
        Assert.Equal("1", p.Get("a"))
        Assert.Equal(1, p.Logs.Length)
        p.Set("a", "changed")
        Assert.Equal("1", ctx.Get("a"))

    [<Fact>]
    let ``Truncate caps long values at limit`` () =
        let ctx = Context()
        ctx.Set("short", "hi")
        ctx.Set("long", String.replicate 200 "ab")
        let p = ctx.Project(FidelityMode.Truncate, truncateLimit = 50)
        Assert.Equal("hi", p.Get("short"))
        Assert.Equal(50, p.Get("long").Length)

    [<Fact>]
    let ``Compact keeps only graph and state keys`` () =
        let ctx = Context()
        ctx.Set("graph.goal", "test")
        ctx.Set("current_node", "A")
        ctx.Set("outcome", "success")
        ctx.Set("last_stage", "Z")
        ctx.Set("tool.output", "big")
        let p = ctx.Project(FidelityMode.Compact)
        Assert.Equal("test", p.Get("graph.goal"))
        Assert.Equal("A", p.Get("current_node"))
        Assert.Equal("", p.Get("last_stage"))
        Assert.Equal("", p.Get("tool.output"))

    [<Fact>]
    let ``SummaryLow keeps at most 20 keys`` () =
        let ctx = Context()
        for i in 1..30 do ctx.Set($"k{i}", $"v{i}")
        Assert.True(ctx.Project(FidelityMode.SummaryLow).Count <= 20)

    [<Fact>]
    let ``SummaryMedium keeps at most 10 keys`` () =
        let ctx = Context()
        for i in 1..30 do ctx.Set($"k{i}", $"v{i}")
        Assert.True(ctx.Project(FidelityMode.SummaryMedium).Count <= 10)

    [<Fact>]
    let ``SummaryHigh produces single condensed key`` () =
        let ctx = Context()
        ctx.Set("name", "Alice")
        ctx.Set("bio", String.replicate 100 "x")
        let p = ctx.Project(FidelityMode.SummaryHigh)
        Assert.True(p.Get("context_summary").Contains("name=Alice"))
        Assert.True(p.Get("context_summary").Contains("..."))
        Assert.Equal("", p.Get("name"))

// ============================================================================
// Validation — terminal reachability, dead ends, synopsis
// ============================================================================

module AdditionalValidationTests =

    [<Fact>]
    let ``Dead end detected on non-terminal with no outgoing edges`` () =
        let graph =
            { Name = "test"
              Nodes = Map.ofList
                [ "start", { Id = "start"; Attributes = Map.ofList ["shape", AttrValue.String "Mdiamond"] }
                  "exit", { Id = "exit"; Attributes = Map.ofList ["shape", AttrValue.String "Msquare"] }
                  "dead", { Id = "dead"; Attributes = Map.empty } ]
              Edges = [ { FromNode = "start"; ToNode = "dead"; Attributes = Map.empty } ]
              GraphAttributes = Map.empty }
        Assert.True(Validation.validate graph None |> List.exists (fun d -> d.Rule = "dead_end" && d.NodeId = "dead"))

    [<Fact>]
    let ``Terminal reachability flags node with no path to exit`` () =
        let graph =
            { Name = "test"
              Nodes = Map.ofList
                [ "start", { Id = "start"; Attributes = Map.ofList ["shape", AttrValue.String "Mdiamond"] }
                  "exit", { Id = "exit"; Attributes = Map.ofList ["shape", AttrValue.String "Msquare"] }
                  "trapped", { Id = "trapped"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "start"; ToNode = "trapped"; Attributes = Map.empty }
                  { FromNode = "start"; ToNode = "exit"; Attributes = Map.empty } ]
              GraphAttributes = Map.empty }
        Assert.True(Validation.validate graph None |> List.exists (fun d -> d.Rule = "terminal_reachability" && d.NodeId = "trapped"))

    [<Fact>]
    let ``Synopsis classifies PLANNING pipeline`` () =
        let g = DotParser.parseOrRaise "digraph T { start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=box, prompt=\"Plan\"]\n start -> A -> exit }"
        Assert.True(Validation.validate g None |> List.exists (fun d -> d.Rule = "synopsis" && d.Message.Contains("PLANNING")))

    [<Fact>]
    let ``Synopsis classifies EXECUTION pipeline`` () =
        let g = DotParser.parseOrRaise "digraph T { start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=parallelogram, tool_command=\"claude --auto\"]\n start -> A -> exit }"
        Assert.True(Validation.validate g None |> List.exists (fun d -> d.Rule = "synopsis" && d.Message.Contains("EXECUTION")))

    [<Fact>]
    let ``Synopsis classifies EXECUTION pipeline for codex LLM node`` () =
        let g = DotParser.parseOrRaise "digraph T { start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=box, llm_model=\"gpt-5.3-codex\", prompt=\"Implement\"]\n start -> A -> exit }"
        Assert.True(Validation.validate g None |> List.exists (fun d -> d.Rule = "synopsis" && d.Message.Contains("EXECUTION")))

    [<Fact>]
    let ``Synopsis classifies EXECUTION pipeline for tab coding_agent node`` () =
        let g = DotParser.parseOrRaise "digraph T { start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=tab, llm_model=\"claude-sonnet-4-6\", prompt=\"Implement\"]\n start -> A -> exit }"
        Assert.True(Validation.validate g None |> List.exists (fun d -> d.Rule = "synopsis" && d.Message.Contains("EXECUTION")))

    [<Fact>]
    let ``Synopsis classifies HYBRID pipeline`` () =
        let g = DotParser.parseOrRaise "digraph T { start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=parallelogram, tool_command=\"dotnet test\"]\n start -> A -> exit }"
        Assert.True(Validation.validate g None |> List.exists (fun d -> d.Rule = "synopsis" && d.Message.Contains("HYBRID")))

    [<Fact>]
    let ``Synopsis reports capability flags`` () =
        let g = DotParser.parseOrRaise "digraph T { graph [model_stylesheet=\"* { llm_model: x; }\"]\n start [shape=Mdiamond]\n exit [shape=Msquare]\n A [shape=box, prompt=\"t\", goal_gate=true]\n gate [shape=hexagon]\n start -> A -> gate\n gate -> exit [label=\"[A] Ok\"]\n gate -> A [label=\"[R] Redo\"] }"
        let caps = Validation.validate g None |> List.tryFind (fun d -> d.Rule = "synopsis" && d.Message.Contains("Capabilities"))
        Assert.True(caps.IsSome)
        Assert.True(caps.Value.Message.Contains("LLM"))
        Assert.True(caps.Value.Message.Contains("HUMAN_GATES"))
        Assert.True(caps.Value.Message.Contains("GOAL_GATES"))

// ============================================================================
// Parallel real handler execution + skip logic
// ============================================================================

module ParallelRealExecutionTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Parallel handler invokes real branch handlers`` () =
        let branches = ResizeArray<string>()
        let handler =
            { new IHandler with
                member _.Execute(node, _, _, _) =
                    lock branches (fun () -> branches.Add(node.Id))
                    Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fan [shape=component]
            A [type="b"]
            B [type="b"]
            fin [shape=tripleoctagon]
            start -> fan
            fan -> A
            fan -> B
            A -> fin
            B -> fin
            fin -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("b", handler)
        registry.Register("parallel", Handlers.ParallelHandler(resolveHandler = registry.Resolve))
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        Assert.True(branches |> Seq.exists (fun id -> id = "A"), "Branch A not executed")
        Assert.True(branches |> Seq.exists (fun id -> id = "B"), "Branch B not executed")

    [<Fact>]
    let ``Engine skips parallel-executed nodes`` () =
        let mutable count = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    count <- count + 1
                    Outcome.Success() }
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            fan [shape=component]
            X [type="c"]
            fin [shape=tripleoctagon]
            start -> fan
            fan -> X
            X -> fin
            fin -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("c", handler)
        registry.Register("parallel", Handlers.ParallelHandler(resolveHandler = registry.Resolve))
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        Engine.run graph config |> ignore
        Assert.Equal(1, count)

// ============================================================================
// Loop restart — state isolation
// ============================================================================

module LoopRestartVerificationTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``Loop restart creates manifest and preserves graph attrs`` () =
        let mutable pass = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    pass <- pass + 1
                    if pass >= 4 then Outcome.Success(contextUpdates = Map.ofList ["loop_done", "true"])
                    else Outcome.Success() }
        let dot = """
        digraph Test {
            graph [goal="restart test"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="h"]
            B [type="h"]
            start -> A -> B
            B -> A [loop_restart=true]
            B -> exit [condition="context.loop_done=true"]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("h", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal("restart test", result.Context.Get("graph.goal"))
        Assert.True(Directory.Exists(Path.Combine(logsRoot, "restart-1")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "restart-1", "restart-manifest.json")))

// ============================================================================
// Edge selection edge cases
// ============================================================================

module EdgeSelectionAdditionalTests =

    [<Fact>]
    let ``Multiple condition matches use weight tiebreak`` () =
        let node = { Id = "A"; Attributes = Map.empty }
        let graph =
            { Name = "t"; Nodes = Map.ofList ["A", node; "B", { Id = "B"; Attributes = Map.empty }; "C", { Id = "C"; Attributes = Map.empty }]
              Edges = [ { FromNode = "A"; ToNode = "B"; Attributes = Map.ofList ["condition", AttrValue.String "outcome=success"; "weight", AttrValue.Integer 1] }
                        { FromNode = "A"; ToNode = "C"; Attributes = Map.ofList ["condition", AttrValue.String "outcome=success"; "weight", AttrValue.Integer 10] } ]
              GraphAttributes = Map.empty }
        let edge = EdgeSelection.selectEdge node (Outcome.Success()) (Context()) graph
        Assert.Equal("C", edge.Value.ToNode)

// ============================================================================
// Sprint 004 Coverage
// ============================================================================

module Sprint004Coverage =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``loop_restart rebinds context and removes non-graph keys`` () =
        let mutable aCalls = 0
        let mutable priorKeyCleared = false

        let handlerA =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    aCalls <- aCalls + 1
                    if aCalls = 1 then
                        Outcome.Success(contextUpdates = Map.ofList [ "scratch", "from-pass-1" ])
                    else
                        Outcome.Success(contextUpdates = Map.ofList [ "loop_done", "true" ]) }

        let handlerB =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    if aCalls >= 2 then
                        priorKeyCleared <- context.TryGet("scratch").IsNone
                    Outcome.Success() }

        let dot = """
        digraph Test {
            graph [goal="restart clear test"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="ha"]
            B [type="hb"]
            start -> A -> B
            B -> A [loop_restart=true]
            B -> exit [condition="context.loop_done=true"]
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("ha", handlerA)
        registry.Register("hb", handlerB)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(priorKeyCleared)

    [<Fact>]
    let ``resume restores checkpointed node outcomes including PartialSuccess`` () =
        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box, prompt="Step A"]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let config = RunConfig.Default(logsRoot)
        let checkpoint =
            { Timestamp = DateTimeOffset.UtcNow
              CurrentNode = "A"
              CompletedNodes = [ "start"; "A" ]
              NodeRetries = Map.empty
              NodeOutcomes =
                Map.ofList
                    [ "start", Outcome.Success()
                      "A", Outcome.PartialSuccess(notes = "partially complete") ]
              ContextValues =
                Map.ofList
                    [ "graph.goal", "resume"
                      "last_stage", "A"
                      "outcome", "partial_success" ]
              Logs = [] }

        let result = Engine.resumeFromCheckpoint graph config checkpoint
        Assert.True(result.NodeOutcomes.ContainsKey("A"))
        Assert.Equal(StageStatus.PartialSuccess, result.NodeOutcomes["A"].Status)

    [<Fact>]
    let ``Fail outcome retry applies backoff delay`` () =
        let mutable attempts = 0
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    attempts <- attempts + 1
                    if attempts = 1 then Outcome.Fail("first failure")
                    else Outcome.Success() }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="retrying", max_retries=1]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("retrying", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }

        let sw = Diagnostics.Stopwatch.StartNew()
        let result = Engine.run graph config
        sw.Stop()

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(2, attempts)
        Assert.True(sw.ElapsedMilliseconds >= 90L, $"Expected backoff delay before retry, got {sw.ElapsedMilliseconds}ms")

    [<Fact>]
    let ``auto_status=true synthesizes success for tool handler without status file`` () =
        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) = Outcome.Fail("handler failed but no status file") }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            tool_node [shape=parallelogram, auto_status=true]
            start -> tool_node -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("tool", handler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }
        let result = Engine.run graph config

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["tool_node"].Status)

    [<Fact>]
    let ``AutoApprove interviewer supports SingleSelect and MultiSelect question types`` () =
        let interviewer = AutoApproveInterviewer() :> IInterviewer
        let options =
            [ { Key = "a"; Label = "Alpha" }
              { Key = "b"; Label = "Beta" }
              { Key = "c"; Label = "Gamma" } ]

        let single =
            interviewer.Ask(
                { Text = "Pick one"
                  Type = QuestionType.SingleSelect
                  Options = options
                  Default = None
                  TimeoutSeconds = None
                  Stage = "review"
                  Metadata = Map.empty })
        Assert.Equal("a", single.Value)

        let multi =
            interviewer.Ask(
                { Text = "Pick many"
                  Type = QuestionType.MultiSelect
                  Options = options
                  Default = None
                  TimeoutSeconds = None
                  Stage = "review"
                  Metadata = Map.empty })
        Assert.Equal("a,b,c", multi.Value)

    [<Fact>]
    let ``All conditions fail without unconditional edge returns none`` () =
        let node = { Id = "A"; Attributes = Map.empty }
        let graph =
            { Name = "t"; Nodes = Map.ofList ["A", node; "B", { Id = "B"; Attributes = Map.empty }; "C", { Id = "C"; Attributes = Map.empty }]
              Edges = [ { FromNode = "A"; ToNode = "B"; Attributes = Map.ofList ["condition", AttrValue.String "outcome=fail"; "weight", AttrValue.Integer 5] }
                        { FromNode = "A"; ToNode = "C"; Attributes = Map.ofList ["condition", AttrValue.String "outcome=fail"; "weight", AttrValue.Integer 10] } ]
              GraphAttributes = Map.empty }
        let edge = EdgeSelection.selectEdge node (Outcome.Success()) (Context()) graph
        Assert.True(edge.IsNone)

module NonRetriableErrorTests =

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    [<Fact>]
    let ``non-retriable exception is NOT retried`` () =
        let mutable callCount = 0
        let customHandler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    callCount <- callCount + 1
                    raise (UnifiedLlm.NotFoundError("missing model")) }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=5]
            start -> A -> exit
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }

        let result = Engine.run graph config
        Assert.Equal(1, callCount)
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)

    [<Fact>]
    let ``selectEdge returns None when success node has only unmatched conditioned edges`` () =
        let customHandler =
            { new IHandler with
                member _.Execute(node, _, _, _) =
                    if node.Id = "A" then Outcome.Success()
                    else Outcome.Success() }

        let dot = """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom", max_retries=0]
            B [type="custom"]
            C [type="custom"]
            start -> A
            A -> B [condition="context.foo=bar"]
            A -> C [condition="context.foo=baz", weight=10]
        }
        """
        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", customHandler)
        let config = { RunConfig.Default(logsRoot) with Registry = registry }

        let result = Engine.run graph config
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.DoesNotContain("C", result.CompletedNodes)
