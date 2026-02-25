namespace UnifiedLlm

open System

/// Base SDK error
type SDKError(message: string, ?cause: Exception) =
    inherit Exception(message, cause |> Option.defaultValue null)

/// Base provider error with HTTP status and retryable flag
type ProviderError(message: string, statusCode: int option, retryable: bool, ?retryAfter: float, ?cause: Exception) =
    inherit SDKError(message, ?cause = cause)
    member _.StatusCode = statusCode
    member _.Retryable = retryable
    member _.RetryAfter = retryAfter

/// 401 - Invalid API key or expired token
type AuthenticationError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 401, false, ?cause = cause)

/// 403 - Insufficient permissions
type AccessDeniedError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 403, false, ?cause = cause)

/// 413 - Request exceeds model context constraints
type ContextLengthError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 413, false, ?cause = cause)

/// 429 - Account or organization quota exhausted (distinct from transient rate limiting)
type QuotaExceededError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 429, false, ?cause = cause)

/// 400/422 - Invalid request payload or parameter combination
type InvalidRequestError(message: string, statusCode: int, ?cause: Exception) =
    inherit ProviderError(message, Some statusCode, false, ?cause = cause)

/// Model emitted a malformed or schema-incompatible tool call
type InvalidToolCallError(message: string, ?cause: Exception) =
    inherit SDKError(message, ?cause = cause)

/// 404 - Model or endpoint not found
type NotFoundError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 404, false, ?cause = cause)

/// 429 - Rate limit exceeded
type RateLimitError(message: string, ?retryAfter: float, ?cause: Exception) =
    inherit ProviderError(message, Some 429, true, ?retryAfter = retryAfter, ?cause = cause)

/// 5xx - Provider internal error
type ServerError(message: string, statusCode: int, ?cause: Exception) =
    inherit ProviderError(message, Some statusCode, true, ?cause = cause)

/// Network-level failure
type NetworkError(message: string, ?cause: Exception) =
    inherit ProviderError(message, None, true, ?cause = cause)

/// Request timed out
type TimeoutError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 408, true, ?cause = cause)

/// Request-level timeout configured by caller
type RequestTimeoutError(message: string, ?cause: Exception) =
    inherit ProviderError(message, Some 408, true, ?cause = cause)

/// Caller-triggered cancellation (abort signal)
type AbortError(message: string, ?cause: Exception) =
    inherit ProviderError(message, None, false, ?cause = cause)

/// Validation error (bad input)
type ValidationError(message: string, ?cause: Exception) =
    inherit SDKError(message, ?cause = cause)

/// Configuration error (missing provider, etc.)
type ConfigurationError(message: string, ?cause: Exception) =
    inherit SDKError(message, ?cause = cause)

/// Content filter blocked the response
type ContentFilterError(message: string, ?cause: Exception) =
    inherit ProviderError(message, None, false, ?cause = cause)

/// Structured output parsing failed
type NoObjectGeneratedError(message: string, ?cause: Exception) =
    inherit SDKError(message, ?cause = cause)

/// Map HTTP status codes to error types
module ErrorMapping =

    let private isQuotaMessage (message: string) =
        let lower = message.ToLowerInvariant()
        lower.Contains("quota")
        || lower.Contains("insufficient_quota")
        || lower.Contains("billing hard limit")

    /// Create the appropriate error type from an HTTP status code and message
    let fromStatusCode (statusCode: int) (message: string) (retryAfter: float option) : ProviderError =
        match statusCode with
        | 401 -> AuthenticationError(message) :> ProviderError
        | 403 -> AccessDeniedError(message) :> ProviderError
        | 404 -> NotFoundError(message) :> ProviderError
        | 413 -> ContextLengthError(message) :> ProviderError
        | 429 ->
            if isQuotaMessage message then
                QuotaExceededError(message) :> ProviderError
            else
                match retryAfter with
                | Some ra -> RateLimitError(message, ra) :> ProviderError
                | Option.None -> RateLimitError(message) :> ProviderError
        | code when code >= 500 && code < 600 -> ServerError(message, code) :> ProviderError
        | 408 -> TimeoutError(message) :> ProviderError
        | 400 | 422 -> InvalidRequestError(message, statusCode) :> ProviderError
        | _ -> ProviderError(message, Some statusCode, true) // unknown defaults to retryable

    /// Classify error by message content for ambiguous cases
    let classifyByMessage (message: string) (statusCode: int) : ProviderError =
        let lower = message.ToLowerInvariant()
        if lower.Contains("not found") || lower.Contains("does not exist") then
            NotFoundError(message) :> ProviderError
        elif lower.Contains("unauthorized") || lower.Contains("invalid key") then
            AuthenticationError(message) :> ProviderError
        elif lower.Contains("context length") || lower.Contains("too many tokens") then
            ContextLengthError(message) :> ProviderError
        elif isQuotaMessage message then
            QuotaExceededError(message) :> ProviderError
        elif lower.Contains("content filter") || lower.Contains("safety") then
            ContentFilterError(message) :> ProviderError
        else
            fromStatusCode statusCode message Option.None
