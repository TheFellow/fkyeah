namespace UnifiedLlm

/// Middleware interface for processing requests and responses
type IMiddleware =
    /// Process a request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member Process: request: Request * next: (Request -> Response) -> Response

    /// Process a streaming request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member ProcessStream: request: Request * next: (Request -> StreamEvent seq) -> StreamEvent seq

/// A chain of middleware that wraps provider calls
type MiddlewareChain() =
    let middlewares = System.Collections.Generic.List<IMiddleware>()

    /// Add a middleware to the chain
    member _.Add(middleware: IMiddleware) =
        middlewares.Add(middleware)

    /// Get count of registered middleware
    member _.Count = middlewares.Count

    /// Execute the chain: request flows forward through middleware in registration order,
    /// response flows back in reverse order (onion pattern via nested next calls)
    member _.Execute(request: Request, handler: Request -> Response) : Response =
        let mutable next = handler
        // Build the chain from innermost to outermost
        for i in (middlewares.Count - 1) .. -1 .. 0 do
            let mw = middlewares.[i]
            let currentNext = next
            next <- fun req -> mw.Process(req, currentNext)
        next request

    /// Execute the stream chain with the same onion ordering as non-streaming calls.
    member _.ExecuteStream(request: Request, handler: Request -> StreamEvent seq) : StreamEvent seq =
        let mutable next = handler
        for i in (middlewares.Count - 1) .. -1 .. 0 do
            let mw = middlewares.[i]
            let currentNext = next
            next <- fun req -> mw.ProcessStream(req, currentNext)
        next request
