module UnifiedLlm.CircuitBreakerTests

open System
open Xunit
open UnifiedLlm

// Drain the agent's mailbox by querying State (PostAndAsyncReply, FIFO with
// prior Posts). Returns the state once all queued messages have been processed
// — eliminates the Sleep-based race where the agent could process queued
// failures and Check back-to-back faster than the CooldownPeriod, leaving the
// breaker stuck in Open even though the test "waited long enough".
let private drainState (breaker: CircuitBreaker) = Async.RunSynchronously breaker.State

let private requireState (breaker: CircuitBreaker) (predicate: CircuitState -> bool) (label: string) =
    let state = drainState breaker

    if not (predicate state) then
        Assert.Fail($"{label}: unexpected state {state}")

[<Fact>]
let ``circuit breaker opens after threshold transient failures`` () =
    let breaker =
        CircuitBreaker(
            "openai",
            { FailureThreshold = 2
              CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
              ProbeSuccessThreshold = 2 }
        )

    breaker.RecordFailure ProviderFailureKind.Timeout
    breaker.RecordFailure ProviderFailureKind.Timeout
    let state = Async.RunSynchronously breaker.State

    match state with
    | CircuitState.Open _ -> ()
    | _ -> Assert.Fail("expected open state")

[<Fact>]
let ``retry hooks reject attempt when breaker is open`` () =
    let breaker =
        CircuitBreaker(
            "openai",
            { FailureThreshold = 1
              CooldownPeriod = TimeSpan.FromSeconds(30.0)
              ProbeSuccessThreshold = 2 }
        )

    breaker.RecordFailure ProviderFailureKind.Timeout
    let hooks = RetryHooks.fromCircuitBreaker breaker
    Assert.ThrowsAny<exn>(fun () -> Retry.executeWithHooks RetryConfig.Default hooks (fun () -> 1) |> ignore)

// ── New Sprint-010 tests ──

[<Fact>]
let ``initial state is Closed 0`` () =
    let breaker = CircuitBreaker("test-init", CircuitBreakerConfig.Default)
    let state = Async.RunSynchronously breaker.State
    Assert.Equal(CircuitState.Closed 0, state)

[<Fact>]
let ``check in Closed returns Ok`` () =
    let breaker = CircuitBreaker("test-check", CircuitBreakerConfig.Default)
    let result = Async.RunSynchronously(breaker.Check())
    Assert.True(Result.isOk result)

[<Fact>]
let ``failures below threshold keep circuit Closed`` () =
    let config =
        { FailureThreshold = 5
          CooldownPeriod = TimeSpan.FromSeconds(30.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-below", config)
    breaker.RecordFailure ProviderFailureKind.Timeout
    breaker.RecordFailure ProviderFailureKind.Timeout
    breaker.RecordFailure ProviderFailureKind.Timeout
    // Drain mailbox via State query (FIFO with prior Posts).
    let state = drainState breaker

    match state with
    | CircuitState.Closed n -> Assert.Equal(3, n)
    | _ -> Assert.Fail($"expected Closed, got {state}")

[<Fact>]
let ``non-transient failure does not count toward threshold`` () =
    let config =
        { FailureThreshold = 2
          CooldownPeriod = TimeSpan.FromSeconds(30.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-nontransient", config)
    breaker.RecordFailure ProviderFailureKind.Authentication
    breaker.RecordFailure ProviderFailureKind.Authentication
    breaker.RecordFailure ProviderFailureKind.Authentication
    let state = drainState breaker

    match state with
    | CircuitState.Closed _ -> ()
    | _ -> Assert.Fail($"expected Closed, got {state}")

[<Fact>]
let ``check in Open before cooldown returns Error`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromSeconds(60.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-open-check", config)
    breaker.RecordFailure ProviderFailureKind.Timeout

    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    let result = Async.RunSynchronously(breaker.Check())
    Assert.True(Result.isError result)

[<Fact>]
let ``check in Open after cooldown transitions to HalfOpen`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-halfopen-transition", config)
    breaker.RecordFailure ProviderFailureKind.Timeout
    // Drain to confirm Open and start the cooldown clock from a known point.
    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    System.Threading.Thread.Sleep(50) // 5x cooldown — generous margin
    let result = Async.RunSynchronously(breaker.Check())
    Assert.True(Result.isOk result)

    requireState
        breaker
        (function
        | CircuitState.HalfOpen _ -> true
        | _ -> false)
        "after Check"

[<Fact>]
let ``single success in HalfOpen with ProbeSuccessThreshold 2 stays HalfOpen`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-halfopen-one-success", config)
    breaker.RecordFailure ProviderFailureKind.Timeout

    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    System.Threading.Thread.Sleep(50)
    Async.RunSynchronously(breaker.Check()) |> ignore

    requireState
        breaker
        (function
        | CircuitState.HalfOpen _ -> true
        | _ -> false)
        "after Check"

    breaker.RecordSuccess()
    let state = drainState breaker

    match state with
    | CircuitState.HalfOpen(_, successes) -> Assert.Equal(1, successes)
    | _ -> Assert.Fail($"expected HalfOpen with 1 success, got {state}")

[<Fact>]
let ``two successes in HalfOpen transitions to Closed`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-halfopen-close", config)
    breaker.RecordFailure ProviderFailureKind.Timeout

    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    System.Threading.Thread.Sleep(50)
    Async.RunSynchronously(breaker.Check()) |> ignore

    requireState
        breaker
        (function
        | CircuitState.HalfOpen _ -> true
        | _ -> false)
        "after Check"

    breaker.RecordSuccess()
    breaker.RecordSuccess()
    let state = drainState breaker
    Assert.Equal(CircuitState.Closed 0, state)

[<Fact>]
let ``transient failure in HalfOpen goes back to Open`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-halfopen-reopen", config)
    breaker.RecordFailure ProviderFailureKind.Timeout

    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    System.Threading.Thread.Sleep(50)
    Async.RunSynchronously(breaker.Check()) |> ignore

    requireState
        breaker
        (function
        | CircuitState.HalfOpen _ -> true
        | _ -> false)
        "after Check"

    breaker.RecordFailure ProviderFailureKind.Timeout
    let state = drainState breaker

    match state with
    | CircuitState.Open _ -> ()
    | _ -> Assert.Fail($"expected Open, got {state}")

[<Fact>]
let ``non-transient failure in HalfOpen stays HalfOpen`` () =
    let config =
        { FailureThreshold = 1
          CooldownPeriod = TimeSpan.FromMilliseconds(10.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-halfopen-nontransient", config)
    breaker.RecordFailure ProviderFailureKind.Timeout

    requireState
        breaker
        (function
        | CircuitState.Open _ -> true
        | _ -> false)
        "after Timeout"

    System.Threading.Thread.Sleep(50)
    Async.RunSynchronously(breaker.Check()) |> ignore

    requireState
        breaker
        (function
        | CircuitState.HalfOpen _ -> true
        | _ -> false)
        "after Check"

    breaker.RecordFailure ProviderFailureKind.Authentication
    let state = drainState breaker

    match state with
    | CircuitState.HalfOpen _ -> ()
    | _ -> Assert.Fail($"expected HalfOpen, got {state}")

[<Fact>]
let ``CircuitOpenError RetryAt is not in the past`` () =
    let retryAt = DateTimeOffset.UtcNow.AddSeconds(30.0)
    let error = CircuitOpenError("test-provider", retryAt)
    Assert.True(error.RetryAt >= DateTimeOffset.UtcNow.AddSeconds(-1.0), "RetryAt should not be in the distant past")

[<Fact>]
let ``CircuitBreakerRegistry getOrCreate returns same instance`` () =
    CircuitBreakerRegistry.reset ()
    let config = CircuitBreakerConfig.Default
    let b1 = CircuitBreakerRegistry.getOrCreate "same-provider" config
    let b2 = CircuitBreakerRegistry.getOrCreate "same-provider" config
    Assert.True(obj.ReferenceEquals(b1, b2))

[<Fact>]
let ``CircuitBreakerRegistry reset clears all breakers`` () =
    CircuitBreakerRegistry.reset ()
    let _ = CircuitBreakerRegistry.getOrCreate "p1" CircuitBreakerConfig.Default
    let _ = CircuitBreakerRegistry.getOrCreate "p2" CircuitBreakerConfig.Default
    CircuitBreakerRegistry.reset ()
    let snapshot = Async.RunSynchronously(CircuitBreakerRegistry.snapshot ())
    Assert.Empty(snapshot)

[<Fact>]
let ``RecordSuccess in Closed resets consecutive failures`` () =
    let config =
        { FailureThreshold = 5
          CooldownPeriod = TimeSpan.FromSeconds(30.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-reset-failures", config)
    breaker.RecordFailure ProviderFailureKind.Timeout
    breaker.RecordFailure ProviderFailureKind.Timeout
    breaker.RecordSuccess()
    let state = drainState breaker
    Assert.Equal(CircuitState.Closed 0, state)

[<Fact>]
let ``thread safety with concurrent RecordFailure posts`` () =
    let config =
        { FailureThreshold = 200
          CooldownPeriod = TimeSpan.FromSeconds(30.0)
          ProbeSuccessThreshold = 2 }

    let breaker = CircuitBreaker("test-concurrent-failures", config)

    let tasks =
        [| for _ in 1..100 ->
               System.Threading.Tasks.Task.Run(fun () -> breaker.RecordFailure ProviderFailureKind.Timeout) |]

    System.Threading.Tasks.Task.WaitAll(tasks)
    let state = drainState breaker

    match state with
    | CircuitState.Closed n -> Assert.Equal(100, n)
    | _ -> Assert.Fail($"expected Closed with 100 failures, got {state}")
