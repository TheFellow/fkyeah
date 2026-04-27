module Sprint016Tests

open System
open System.IO
open System.Text.Json
open Xunit
open Attractor

let private createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), $"attractor-sprint016-{Guid.NewGuid():N}")

    Directory.CreateDirectory(dir) |> ignore
    dir

module OutcomeAndConditionTests =

    [<Fact>]
    let ``Conditions evaluate uses RawOutcome for outcome == custom`` () =
        let outcome =
            { Outcome.Success() with
                RawOutcome = Some "needs_dod" }

        let context = Context()
        Assert.True(Conditions.evaluate "outcome == \"needs_dod\"" outcome context)

    [<Fact>]
    let ``Conditions evaluate outcome == success remains compatible when RawOutcome is None`` () =
        let outcome = Outcome.Success()
        let context = Context()
        Assert.True(Conditions.evaluate "outcome == \"success\"" outcome context)

    [<Fact>]
    let ``OutcomeString falls back to Status string when RawOutcome is None`` () =
        let outcome = Outcome.Success()
        Assert.Equal("success", outcome.OutcomeString)

module StatusRoundTripTests =

    [<Fact>]
    let ``Engine status loader preserves raw outcome while status falls back`` () =
        let handler =
            { new IHandler with
                member _.Execute(node, _, _, logsRoot) =
                    let stageDir = Path.Combine(logsRoot, node.Id)
                    Directory.CreateDirectory(stageDir) |> ignore

                    File.WriteAllText(Path.Combine(stageDir, "status.json"), """{"outcome":"needs_dod"}""")

                    Outcome.Success() }

        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            start -> A -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let config =
            { RunConfig.Default(logsRoot) with
                Registry = registry }

        let result = Engine.run graph config
        let loaded = result.NodeOutcomes["A"]

        Assert.Equal(StageStatus.Success, loaded.Status)
        Assert.Equal(Some "needs_dod", loaded.RawOutcome)

    [<Fact>]
    let ``HandlerArtifacts.writeStatus writes RawOutcome string to outcome field`` () =
        let logsRoot = createTempDir ()

        let outcome =
            { Outcome.Success() with
                RawOutcome = Some "needs_dod" }

        HandlerArtifacts.writeStatus logsRoot logsRoot outcome

        use doc =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(logsRoot, "status.json")))

        let raw = doc.RootElement.GetProperty("outcome").GetString()
        Assert.Equal("needs_dod", raw)
