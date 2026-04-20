module UnifiedLlmErrorSprint007Tests

open System
open System.Net
open System.Net.Http
open Xunit
open UnifiedLlm

module ErrorSprint007 =

    [<Fact>]
    let ``RetryAfterParsing handles delta seconds and HTTP dates`` () =
        Assert.Equal(Some 5.0, RetryAfterParsing.parse "5")
        Assert.Equal(Some 120.0, RetryAfterParsing.parse "120")

        let future = DateTimeOffset.UtcNow.AddSeconds(30.0).ToString("R")
        let futureParsed = RetryAfterParsing.parse future
        Assert.True(futureParsed.IsSome && futureParsed.Value > 0.0)

        let past = DateTimeOffset.UtcNow.AddSeconds(-30.0).ToString("R")
        Assert.Equal(Some 0.0, RetryAfterParsing.parse past)

    [<Fact>]
    let ``RetryAfterParsing rejects invalid values`` () =
        Assert.Equal(None, RetryAfterParsing.parse "")
        Assert.Equal(None, RetryAfterParsing.parse "invalid")
        Assert.Equal(None, RetryAfterParsing.parse "-5")

    [<Fact>]
    let ``ProviderError Kind classifies concrete error types`` () =
        let cases =
            [ AuthenticationError("auth") :> ProviderError, ProviderFailureKind.Authentication
              AccessDeniedError("denied") :> ProviderError, ProviderFailureKind.AccessDenied
              ContextLengthError("context") :> ProviderError, ProviderFailureKind.ContextLength
              QuotaExceededError("quota") :> ProviderError, ProviderFailureKind.QuotaExceeded
              InvalidRequestError("bad", 422) :> ProviderError, ProviderFailureKind.InvalidRequest 422
              NotFoundError("missing") :> ProviderError, ProviderFailureKind.NotFound
              RateLimitError("slow down", 5.0) :> ProviderError, ProviderFailureKind.RateLimited
              ServerError("boom", 503) :> ProviderError, ProviderFailureKind.ServerFailure 503
              NetworkError("net") :> ProviderError, ProviderFailureKind.Network
              TimeoutError("timeout") :> ProviderError, ProviderFailureKind.Timeout
              ContentFilterError("blocked") :> ProviderError, ProviderFailureKind.ContentFilter ]

        for (error, expected) in cases do
            Assert.Equal(expected, error.Kind)

    [<Fact>]
    let ``classifyHttpResponse extracts Retry-After for server and rate limit errors`` () =
        use serverResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        serverResponse.Headers.TryAddWithoutValidation("Retry-After", "5") |> ignore
        let classifiedServer = ErrorMapping.classifyHttpResponse serverResponse "try again"
        let serverError = Assert.IsType<ServerError>(classifiedServer)
        Assert.Equal(Some 5.0, serverError.RetryAfter)

        use rateLimitResponse = new HttpResponseMessage(enum<HttpStatusCode> 429)
        rateLimitResponse.Headers.TryAddWithoutValidation("Retry-After", "60") |> ignore

        let classifiedRateLimit =
            ErrorMapping.classifyHttpResponse rateLimitResponse "slow down"

        let rateLimitError = Assert.IsType<RateLimitError>(classifiedRateLimit)
        Assert.Equal(Some 60.0, rateLimitError.RetryAfter)

    [<Fact>]
    let ``quota responses are not treated as retryable rate limits`` () =
        use response = new HttpResponseMessage(enum<HttpStatusCode> 429)
        let classified = ErrorMapping.classifyHttpResponse response "quota exceeded"
        Assert.IsType<QuotaExceededError>(classified) |> ignore

    [<Fact>]
    let ``Retry effectiveDelay rejects Retry-After values beyond max delay`` () =
        let config =
            { RetryConfig.Default with
                MaxDelayMs = 1000
                Jitter = false }

        Assert.Equal(None, Retry.effectiveDelay config 0 (Some 2.0))
