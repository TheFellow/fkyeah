namespace Attractor

open System

type PipelineEventContext =
    { TaskId: string option
      Lane: string option
      ReleaseId: string option
      ArtifactIds: string list }

    static member Empty =
        { TaskId = None
          Lane = None
          ReleaseId = None
          ArtifactIds = [] }

/// Pipeline execution events for observability
[<RequireQualifiedAccess>]
type PipelineEvent =
    // Pipeline lifecycle
    | PipelineStarted of name: string * id: string
    | PipelineCompleted of duration: TimeSpan * artifactCount: int
    | PipelineFailed of error: string * duration: TimeSpan
    // Stage lifecycle
    | StageStarted of name: string * index: int
    | StageCompleted of name: string * index: int * duration: TimeSpan
    | StageFailed of name: string * index: int * error: string * willRetry: bool
    | StageRetrying of name: string * index: int * attempt: int * delayMs: int
    // Parallel execution
    | ParallelStarted of branchCount: int
    | ParallelBranchStarted of branch: string * index: int
    | ParallelBranchCompleted of branch: string * index: int * duration: TimeSpan * success: bool
    | ParallelCompleted of duration: TimeSpan * successCount: int * failureCount: int
    // Human interaction
    | InterviewStarted of question: string * stage: string
    | InterviewCompleted of question: string * answer: string * duration: TimeSpan
    | InterviewTimeout of question: string * stage: string * duration: TimeSpan
    // Checkpoint
    | CheckpointSaved of nodeId: string
    // Loop restart
    | LoopRestarted of targetNode: string * restartCount: int * newLogsRoot: string
    // Change/deploy flow
    | ChangeImplementationStarted of ctx: PipelineEventContext * description: string
    | ChangeReviewCompleted of ctx: PipelineEventContext * success: bool
    | ChangeScenarioCompleted of ctx: PipelineEventContext * scenarioName: string * success: bool
    | DeployStarted of ctx: PipelineEventContext * environment: string
    | DeployVerified of ctx: PipelineEventContext * environment: string * duration: TimeSpan
    | DeployRolledBack of ctx: PipelineEventContext * environment: string * reason: string

/// Event observer/handler
type IEventObserver =
    abstract member OnEvent: PipelineEvent -> unit

type IAsyncEventObserver =
    abstract member OnEventAsync: PipelineEvent -> Async<unit>

/// Event emitter for the pipeline engine
type EventEmitter() =
    let observers = ResizeArray<IEventObserver>()
    let asyncObservers = ResizeArray<IAsyncEventObserver>()

    member _.AddObserver(observer: IEventObserver) =
        observers.Add(observer)

    member _.AddAsyncObserver(observer: IAsyncEventObserver) =
        asyncObservers.Add(observer)

    member _.RemoveObserver(observer: IEventObserver) =
        observers.Remove(observer) |> ignore

    member _.Emit(event: PipelineEvent) =
        for observer in observers do
            try
                observer.OnEvent(event)
            with _ ->
                () // Don't let observer failures break the pipeline

    member _.EmitAsync(event: PipelineEvent) =
        for observer in observers do
            try
                observer.OnEvent(event)
            with _ ->
                ()

        if asyncObservers.Count > 0 then
            asyncObservers
            |> Seq.map (fun observer ->
                async {
                    try
                        do! observer.OnEventAsync(event)
                    with _ ->
                        ()
                })
            |> Async.Parallel
            |> Async.Ignore
            |> Async.RunSynchronously

/// Simple event collector for testing
type EventCollector() =
    let events = ResizeArray<PipelineEvent>()

    member _.Events = events |> Seq.toList

    member _.Clear() = events.Clear()

    interface IEventObserver with
        member _.OnEvent(event) = events.Add(event)
