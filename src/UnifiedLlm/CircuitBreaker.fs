namespace UnifiedLlm

open System
open System.Collections.Concurrent

type CircuitBreakerConfig =
    { FailureThreshold: int
      CooldownPeriod: TimeSpan
      ProbeSuccessThreshold: int }
    static member Default =
        { FailureThreshold = 5
          CooldownPeriod = TimeSpan.FromSeconds(30.0)
          ProbeSuccessThreshold = 2 }

type private BreakerMessage =
    | Check of AsyncReplyChannel<Result<unit, ProviderError>>
    | RecordSuccess
    | RecordFailure of ProviderFailureKind
    | GetState of AsyncReplyChannel<CircuitState>

type CircuitBreaker(provider: string, config: CircuitBreakerConfig) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec closed consecutiveFailures =
                async {
                    let! message = inbox.Receive()
                    match message with
                    | Check reply ->
                        reply.Reply(Result.Ok ())
                        return! closed consecutiveFailures
                    | RecordSuccess ->
                        return! closed 0
                    | RecordFailure kind ->
                        if CircuitFailureClassification.isTransient kind then
                            let nextFailures = consecutiveFailures + 1
                            if nextFailures >= config.FailureThreshold then
                                let openedAt = DateTimeOffset.UtcNow
                                let retryAt = openedAt + config.CooldownPeriod
                                return! opened openedAt retryAt
                            else
                                return! closed nextFailures
                        else
                            return! closed consecutiveFailures
                    | GetState reply ->
                        reply.Reply(CircuitState.Closed consecutiveFailures)
                        return! closed consecutiveFailures
                }

            and opened openedAt retryAt =
                async {
                    let! message = inbox.Receive()
                    match message with
                    | Check reply ->
                        if DateTimeOffset.UtcNow >= retryAt then
                            reply.Reply(Result.Ok ())
                            return! halfOpen DateTimeOffset.UtcNow 0
                        else
                            reply.Reply(Result.Error (CircuitOpenError(provider, retryAt) :> ProviderError))
                            return! opened openedAt retryAt
                    | RecordSuccess
                    | RecordFailure _ ->
                        return! opened openedAt retryAt
                    | GetState reply ->
                        reply.Reply(CircuitState.Open(openedAt, retryAt))
                        return! opened openedAt retryAt
                }

            and halfOpen probeStartedAt successCount =
                async {
                    let! message = inbox.Receive()
                    match message with
                    | Check reply ->
                        reply.Reply(Result.Ok ())
                        return! halfOpen probeStartedAt successCount
                    | RecordSuccess ->
                        let nextSuccessCount = successCount + 1
                        if nextSuccessCount >= config.ProbeSuccessThreshold then
                            return! closed 0
                        else
                            return! halfOpen probeStartedAt nextSuccessCount
                    | RecordFailure kind ->
                        if CircuitFailureClassification.isTransient kind then
                            let openedAt = DateTimeOffset.UtcNow
                            let retryAt = openedAt + config.CooldownPeriod
                            return! opened openedAt retryAt
                        else
                            return! halfOpen probeStartedAt successCount
                    | GetState reply ->
                        reply.Reply(CircuitState.HalfOpen(probeStartedAt, successCount))
                        return! halfOpen probeStartedAt successCount
                }

            closed 0)

    member _.Provider = provider
    member _.Config = config
    member _.Check() = agent.PostAndAsyncReply(Check)
    member _.RecordSuccess() = agent.Post RecordSuccess
    member _.RecordFailure(kind: ProviderFailureKind) = agent.Post(RecordFailure kind)
    member _.State = agent.PostAndAsyncReply(GetState)

module CircuitBreakerRegistry =
    let private breakers = ConcurrentDictionary<string, CircuitBreaker>()

    let getOrCreate (provider: string) (config: CircuitBreakerConfig) =
        breakers.GetOrAdd(provider, fun _ -> CircuitBreaker(provider, config))

    let reset () =
        breakers.Clear()

    let snapshot () =
        async {
            let! states =
                breakers
                |> Seq.map (fun entry ->
                    async {
                        let! state = entry.Value.State
                        return entry.Key, state
                    })
                |> Async.Parallel

            return states |> Map.ofArray
        }
