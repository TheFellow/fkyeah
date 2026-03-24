namespace UnifiedLlm

/// Backward-compatible middleware interface for processing requests and responses
type IMiddleware =
    /// Process a request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member Process: request: Request * next: (Request -> Response) -> Response

    /// Process a streaming request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member ProcessStream: request: Request * next: (Request -> StreamEvent seq) -> StreamEvent seq

/// Functional middleware for complete and streaming requests
type MiddlewareFn = Request -> (Request -> Response) -> Response
type StreamMiddlewareFn = Request -> (Request -> StreamEvent seq) -> StreamEvent seq

type Middleware =
    { Complete: MiddlewareFn
      Stream: StreamMiddlewareFn }

module Middleware =

    /// Create middleware from a request transform.
    let fromRequestTransform (transform: Request -> Request) : Middleware =
        { Complete = fun req next -> next (transform req)
          Stream = fun req next -> next (transform req) }

    /// Create middleware that only wraps complete requests.
    let fromComplete (fn: MiddlewareFn) : Middleware =
        { Complete = fn
          Stream = fun req next -> next req }

    /// Wrap a legacy interface middleware as functional middleware.
    let ofInterface (mw: IMiddleware) : Middleware =
        { Complete = fun req next -> mw.Process(req, next)
          Stream = fun req next -> mw.ProcessStream(req, next) }

/// Immutable middleware pipeline composed in onion order.
type MiddlewarePipeline(middlewares: Middleware list) =
    let chain = middlewares

    member _.Execute(request: Request, handler: Request -> Response) : Response =
        let composed =
            (handler, List.rev chain)
            ||> List.fold (fun next mw -> fun req -> mw.Complete req next)
        composed request

    member _.ExecuteStream(request: Request, handler: Request -> StreamEvent seq) : StreamEvent seq =
        let composed =
            (handler, List.rev chain)
            ||> List.fold (fun next mw -> fun req -> mw.Stream req next)
        composed request

    member _.Count = chain.Length

/// Computation expression builder for middleware pipelines.
type MiddlewareBuilder() =
    member _.Yield(mw: Middleware) : Middleware list = [ mw ]
    member _.Yield(()) : Middleware list = []
    member _.Combine(a: Middleware list, b: Middleware list) : Middleware list = a @ b
    member _.Delay(f: unit -> Middleware list) : Middleware list = f ()
    member _.Zero() : Middleware list = []
    member _.Run(middlewares: Middleware list) : MiddlewarePipeline =
        MiddlewarePipeline(middlewares)

/// Backward-compatible mutable middleware chain wrapping MiddlewarePipeline.
type MiddlewareChain() =
    let middlewares = System.Collections.Generic.List<Middleware>()

    /// Add a legacy middleware to the chain.
    member _.Add(middleware: IMiddleware) =
        middlewares.Add(Middleware.ofInterface middleware)

    /// Add a functional middleware to the chain.
    member _.AddFn(middleware: Middleware) =
        middlewares.Add(middleware)

    /// Get count of registered middleware.
    member _.Count = middlewares.Count

    member _.Execute(request: Request, handler: Request -> Response) : Response =
        MiddlewarePipeline(middlewares |> Seq.toList).Execute(request, handler)

    member _.ExecuteStream(request: Request, handler: Request -> StreamEvent seq) : StreamEvent seq =
        MiddlewarePipeline(middlewares |> Seq.toList).ExecuteStream(request, handler)

[<AutoOpen>]
module MiddlewareBuilderExtensions =
    let middleware = MiddlewareBuilder()
