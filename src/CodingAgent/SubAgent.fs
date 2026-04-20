namespace CodingAgent

open System
open UnifiedLlm

/// Result from a subagent
type SubAgentResult =
    { Output: string
      Success: bool
      TurnsUsed: int }

/// Handle to a spawned subagent
type SubAgentHandle(id: string, session: Session) =
    let mutable status = "running"

    /// The subagent ID
    member _.Id = id

    /// Current status
    member _.Status = status

    /// The underlying session
    member _.Session = session

    /// Send input to the subagent
    member _.SendInput(message: string) =
        if status = "running" then
            try
                session.ProcessInput(message)
                // If the session completed (Idle/AwaitingInput/Closed), mark as completed
                if session.State = Idle || session.State = AwaitingInput || session.State = Closed then
                    status <- "completed"
            with _ ->
                status <- "failed"

    /// Wait for the subagent to complete and return its result
    member _.Wait() : SubAgentResult =
        let lastAssistant =
            session.History
            |> List.rev
            |> List.tryPick (fun t ->
                match t with
                | AssistantTurn(content, _, _, _, _) -> Some content
                | _ -> None)

        let output = lastAssistant |> Option.defaultValue ""

        let turns =
            session.History
            |> List.filter (fun t ->
                match t with
                | UserTurn _
                | AssistantTurn _ -> true
                | _ -> false)
            |> List.length

        let success = status <> "failed"

        if success then
            status <- "completed"

        { Output = output
          Success = success
          TurnsUsed = turns }

    /// Close the subagent
    member _.Close() =
        session.Close()
        status <- "closed"

/// Subagent management
module SubAgent =

    /// Spawn a subagent. Shares parent execution environment.
    /// Depth limiting prevents recursive spawning.
    let spawn
        (parentProfile: IProviderProfile)
        (env: IExecutionEnvironment)
        (client: Client)
        (task: string)
        (currentDepth: int)
        (maxDepth: int)
        (config: SessionConfig option)
        : SubAgentHandle option =

        if currentDepth >= maxDepth then
            None // Depth limit reached
        else
            let subConfig = config |> Option.defaultValue SessionConfig.Default

            let subSession =
                Session(parentProfile, env, client, subConfig, depth = currentDepth + 1)

            let handle = SubAgentHandle(Guid.NewGuid().ToString("N"), subSession)
            handle.SendInput(task)
            Some handle
