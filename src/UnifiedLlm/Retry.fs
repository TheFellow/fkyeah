namespace UnifiedLlm

open System

/// Configuration for retry behavior
type RetryConfig =
    { MaxRetries: int
      InitialDelayMs: int
      MaxDelayMs: int
      BackoffFactor: float
      Jitter: bool }

    static member Default =
        { MaxRetries = 2
          InitialDelayMs = 1000
          MaxDelayMs = 60000
          BackoffFactor = 2.0
          Jitter = true }

type RetryHooks =
    { BeforeAttempt: int -> Result<unit, exn>
      OnSuccess: int -> unit
      OnFailure: int * exn -> unit
      OnGiveUp: int * exn -> unit }

module RetryHooks =

    let none =
        { BeforeAttempt = fun _ -> Result.Ok()
          OnSuccess = fun _ -> ()
          OnFailure = fun _ -> ()
          OnGiveUp = fun _ -> () }

    let fromCircuitBreaker (breaker: CircuitBreaker) =
        { BeforeAttempt =
            fun _ ->
                breaker.Check()
                |> Async.RunSynchronously
                |> Result.mapError (fun error -> error :> exn)
          OnSuccess = fun _ -> breaker.RecordSuccess()
          OnFailure =
            fun (_, exn) ->
                match exn with
                | :? ProviderError as providerError -> breaker.RecordFailure providerError.Kind
                | _ -> ()
          OnGiveUp = fun _ -> () }

/// Retry logic helpers
module Retry =

    let private rng = Random()

    /// Calculate the delay for a given attempt number (0-indexed)
    let calculateDelay (config: RetryConfig) (attempt: int) : int =
        let baseDelay =
            float config.InitialDelayMs * Math.Pow(config.BackoffFactor, float attempt)

        let capped = Math.Min(baseDelay, float config.MaxDelayMs)

        if config.Jitter then
            let jitterFactor = 0.5 + rng.NextDouble() // 0.5 to 1.5
            int (capped * jitterFactor)
        else
            int capped

    /// Determine effective delay, taking Retry-After header into account
    let effectiveDelay (config: RetryConfig) (attempt: int) (retryAfter: float option) : int option =
        match retryAfter with
        | Some ra ->
            let raMs = int (ra * 1000.0)

            if raMs > config.MaxDelayMs then
                Option.None // Do not retry if Retry-After exceeds max
            else
                Some raMs
        | Option.None -> Some(calculateDelay config attempt)

    /// Check if an error is retryable.
    /// NetworkError and TimeoutError inherit from ProviderError, so they are covered by the first case.
    let isRetryable (error: exn) : bool =
        match error with
        | :? ProviderError as pe -> pe.Retryable
        | _ -> false

    /// Execute a function with retry logic and hooks. Returns the result or the last error.
    let executeWithHooks (config: RetryConfig) (hooks: RetryHooks) (fn: unit -> 'T) : 'T =
        let mutable attempt = 0
        let mutable lastError: exn option = Option.None
        let mutable result: 'T option = Option.None

        while attempt <= config.MaxRetries && result.IsNone do
            match hooks.BeforeAttempt attempt with
            | Result.Error ex ->
                lastError <- Some ex
                attempt <- config.MaxRetries + 1
            | Result.Ok() ->
                try
                    result <- Some(fn ())
                    hooks.OnSuccess attempt
                with ex ->
                    hooks.OnFailure(attempt, ex)
                    lastError <- Some ex

                    if attempt < config.MaxRetries && isRetryable ex then
                        let retryAfter =
                            match ex with
                            | :? ProviderError as pe -> pe.RetryAfter
                            | _ -> Option.None

                        match effectiveDelay config attempt retryAfter with
                        | Some delayMs ->
                            System.Threading.Thread.Sleep(delayMs)
                            attempt <- attempt + 1
                        | Option.None ->
                            hooks.OnGiveUp(attempt, ex)
                            attempt <- config.MaxRetries + 1
                    else
                        hooks.OnGiveUp(attempt, ex)
                        attempt <- config.MaxRetries + 1

        match result with
        | Some r -> r
        | Option.None -> raise (lastError |> Option.defaultWith (fun () -> SDKError("Retry failed") :> exn))

    /// Execute a function with retry logic. Returns the result or the last error.
    let execute (config: RetryConfig) (fn: unit -> 'T) : 'T =
        executeWithHooks config RetryHooks.none fn
