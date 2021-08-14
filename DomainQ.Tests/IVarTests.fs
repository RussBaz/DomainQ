module DomainQ.Tests.IVarTests

open NUnit.Framework
open FsUnitTyped

open FSharp.Control

open DomainQ.DataStructures

[<SetUp>]
let Setup () =
    ()
    
[<Test>]
let ``Check basic SVar usage`` () =
    // SVar implements IDisposable
    use v = SVar.create ()
    
    let mutable a = 0
    let mutable b = 3
    
    let isFilled = v |> SVar.isFilled
    
    isFilled |> shouldEqual false
    
    Async.Parallel [
        async {
            do! v |> SVar.fill 2
        }
        async {
            let! r = v |> SVar.read
            a <- r
        }
        async {
            let! r = v |> SVar.read
            b <- r
            let! r = v |> SVar.tryFill 4
            match r with
            | Ok () -> Assert.Fail "Already filled SVar cannot be refilled"
            | Error () -> Assert.Pass ()
        }
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    a |> shouldEqual 2
    b |> shouldEqual 2
    
    let isFilled = v |> SVar.isFilled
    
    isFilled |> shouldEqual true
    
    fun _ ->
        async {
            do! v |> SVar.fill 8
        }
        |> Async.RunSynchronously
    |> shouldFail
    
    // use and let will both work
    let v2 = SVar.create<int> ()
    
    let isFilled = v2 |> SVar.isFilled
    
    isFilled |> shouldEqual false
    
    // Using tryFill instead of fill
    Async.Parallel [
        async {
            let! r = v2 |> SVar.read
            a <- r
        }
        async {
            let! r = v2 |> SVar.tryFill 4
            match r with
            | Ok () -> Assert.Pass ()
            | Error () -> Assert.Fail "The SVar should not be filled at this point yet"
        }
        async {
            let! r = v2 |> SVar.read
            b <- r
        }
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    a |> shouldEqual 4
    b |> shouldEqual 4
    
    let isFilled = v2 |> SVar.isFilled
    
    isFilled |> shouldEqual true
