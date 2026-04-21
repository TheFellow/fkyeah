module UnifiedLlmMiddlewareSprint007Tests

open Xunit
open UnifiedLlm

module MiddlewareSprint007 =

    [<Fact>]
    let ``middleware computation expression composes middleware in onion order`` () =
        let events = ResizeArray<string>()

        let outer =
            { Complete =
                fun req next ->
                    events.Add("outer-request")
                    let response = next req
                    events.Add("outer-response")
                    response
              Stream = fun req next -> next req }

        let inner =
            { Complete =
                fun req next ->
                    events.Add("inner-request")
                    let response = next req
                    events.Add("inner-response")
                    response
              Stream = fun req next -> next req }

        let pipeline =
            middleware {
                yield outer
                yield inner
            }

        let response =
            pipeline.Execute(
                Request.Create("model", [ Message.User("hello") ]),
                fun request ->
                    events.Add($"handler:{request.Model}")

                    Message.Assistant("ok")
                    |> fun message ->
                        { Id = "r1"
                          Model = request.Model
                          Provider = "test"
                          Message = message
                          FinishReason = Stop "stop"
                          Usage = Usage.Zero
                          ResponseId = None
                          Raw = None
                          Warnings = []
                          RateLimit = None }
            )

        Assert.Equal(2, pipeline.Count)
        Assert.Equal("model", response.Model)

        Assert.Equal<string list>(
            [ "outer-request"
              "inner-request"
              "handler:model"
              "inner-response"
              "outer-response" ],
            events |> Seq.toList
        )

    [<Fact>]
    let ``middleware builder supports empty and single pipelines`` () =
        let emptyPipeline = middleware { () }

        let singleMiddleware =
            Middleware.fromRequestTransform (fun request -> { request with Model = "rewritten" })

        let singlePipeline = middleware { yield singleMiddleware }

        let emptyResponse =
            emptyPipeline.Execute(
                Request.Create("original", [ Message.User("hello") ]),
                fun request ->
                    { Id = "empty"
                      Model = request.Model
                      Provider = "test"
                      Message = Message.Assistant("ok")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
            )

        let singleResponse =
            singlePipeline.Execute(
                Request.Create("original", [ Message.User("hello") ]),
                fun request ->
                    { Id = "single"
                      Model = request.Model
                      Provider = "test"
                      Message = Message.Assistant("ok")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
            )

        Assert.Equal(0, emptyPipeline.Count)
        Assert.Equal("original", emptyResponse.Model)
        Assert.Equal(1, singlePipeline.Count)
        Assert.Equal("rewritten", singleResponse.Model)

    [<Fact>]
    let ``functional middleware adapters preserve backward compatibility`` () =
        let wrapped =
            { new IMiddleware with
                member _.Process(request, next) =
                    next
                        { request with
                            Model = request.Model + "-wrapped" }

                member _.ProcessStream(request, next) =
                    next
                        { request with
                            Model = request.Model + "-wrapped-stream" } }

        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.AddMiddlewareFn(Middleware.ofInterface wrapped)

        let response = client.Complete(Request.Create("base", [ Message.User("hello") ]))
        Assert.Equal("base-wrapped", response.Model)

        let chain = MiddlewareChain()
        chain.Add(wrapped)

        let bridged =
            chain.Execute(
                Request.Create("chain", [ Message.User("hello") ]),
                fun request ->
                    { Id = "chain"
                      Model = request.Model
                      Provider = "test"
                      Message = Message.Assistant("ok")
                      FinishReason = Stop "stop"
                      Usage = Usage.Zero
                      ResponseId = None
                      Raw = None
                      Warnings = []
                      RateLimit = None }
            )

        Assert.Equal("chain-wrapped", bridged.Model)
