namespace UnifiedLlm

open System

/// Configuration for retry behavior
type RetryConfig = {
    MaxRetries: int
    InitialDelayMs: int
    MaxDelayMs: int
    BackoffFactor: float
    Jitter: bool
} with
    static member Default = {
        MaxRetries = 2
        InitialDelayMs = 1000
        MaxDelayMs = 60000
        BackoffFactor = 2.0
        Jitter = true
    }

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
        | Option.None ->
            Some (calculateDelay config attempt)

    /// Check if an error is retryable.
    /// NetworkError and TimeoutError inherit from ProviderError, so they are covered by the first case.
    let isRetryable (error: exn) : bool =
        match error with
        | :? ProviderError as pe -> pe.Retryable
        | _ -> false

    /// Execute a function with retry logic. Returns the result or the last error.
    let execute (config: RetryConfig) (fn: unit -> 'T) : 'T =
        let mutable attempt = 0
        let mutable lastError: exn option = Option.None
        let mutable result: 'T option = Option.None

        while attempt <= config.MaxRetries && result.IsNone do
            try
                result <- Some (fn())
            with ex ->
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
                        // Retry-After exceeds max, don't retry
                        attempt <- config.MaxRetries + 1
                else
                    attempt <- config.MaxRetries + 1

        match result with
        | Some r -> r
        | Option.None ->
            raise (lastError |> Option.defaultWith (fun () -> SDKError("Retry failed") :> exn))
