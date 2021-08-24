module DomainQ.Tests.IVarTests

open NUnit.Framework
open FsUnitTyped

open FSharp.Control

open DomainQ.DataStructures

type SampleMutableRecord = {
    mutable Value: string
    Count: int
}

let timeout ( ms: int ) = async {
    do! Async.Sleep ms
    return Some false
}

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
            let! r = v |> SVar.read WithoutTimeout
            a <- r
        }
        async {
            let! r = v |> SVar.read WithoutTimeout
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
            let! r = v2 |> SVar.read WithoutTimeout
            a <- r
        }
        async {
            let! r = v2 |> SVar.tryFill 4
            match r with
            | Ok () -> Assert.Pass ()
            | Error () -> Assert.Fail "The SVar should not be filled at this point yet"
        }
        async {
            let! r = v2 |> SVar.read WithoutTimeout
            b <- r
        }
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    a |> shouldEqual 4
    b |> shouldEqual 4
    
    let isFilled = v2 |> SVar.isFilled
    
    isFilled |> shouldEqual true
    
    // Anonymous records would work too
    let v3 = SVar.create<{| Count: int |}> ()
    
    let isFilled = v3 |> SVar.isFilled
    
    isFilled |> shouldEqual false
    
    // Using ignoreFill instead
    // If one tries to write to the variable once it is set
    // ignoreFill will do nothing
    Async.Parallel [
        async {
            let! r = v3 |> SVar.read WithoutTimeout
            a <- r.Count
        }
        async {
            do! v3 |> SVar.ignoreFill {| Count = 18 |}
            // Subsequent calls to ignoreFIll result in nothing
            // It will neither update the value, nor throw an exception
            do! v3 |> SVar.ignoreFill {| Count = 7 |}
            do! v3 |> SVar.ignoreFill {| Count = 42 |}
        }
        async {
            let! r = v3 |> SVar.read WithoutTimeout
            b <- r.Count
        }
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    a |> shouldEqual 18
    b |> shouldEqual 18
    
    let isFilled = v3 |> SVar.isFilled
    
    isFilled |> shouldEqual true
    
[<Test>]
let ``Checking different timeout behaviours for SVar functions`` () =
    let reduce ( data: Async<bool option []> ) = async {
        let! data = data
        for i, v in data |> Seq.indexed do
            match v with
            | Some b when b -> ()
            | _ -> failwith $"Something went wrong at pos: {i}"
        return Some true
    }
    
    let v = SVar.create<int> ()
        
    Async.Choice [
        timeout 1000
        async {
            let! _ = v |> SVar.read WithoutTimeout
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> Assert.Fail "Timeout did not occur 1"
        | _ -> ()
        
    v |> SVar.isFilled |> shouldEqual false
    
    Async.Choice [
        timeout 60
        async {
            try
                let! _ = v |> SVar.read ( WithTimeoutOf 50<ms> )
                Assert.Fail "Timeout did not occur 4"
            with
            | _ -> ()
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "Timeout did not occur on time 5"
        
    v |> SVar.isFilled |> shouldEqual false
    
    Async.Choice [
        timeout 10
        async {
            do! v |> SVar.fill 10
            try
                do! v |> SVar.fill 11
            with
            | _ -> ()
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "Timeout occured 6"
        
    v |> SVar.isFilled |> shouldEqual true
    
    let v2 = SVar.create<int> ()
    
    v2 |> SVar.isFilled |> shouldEqual false
    
    Async.Choice [
        timeout 520
        Async.Parallel [
            async {
                let! _ = v2 |> SVar.read WithoutTimeout
                return Some true
            }
            async {
                try
                    let! _ = v2 |> SVar.read ( WithTimeoutOf 500<ms> )
                    return Some false
                with
                | _ -> return Some true
            }
            async {
                try
                    let! _ = v2 |> SVar.read ( WithTimeoutOf 1000<ms> )
                    return Some true
                with
                | _ -> return Some false
            }
            async {
                do! Async.Sleep 500
                do! v2 |> SVar.fill 1
                return Some true
            }
        ]
        |> reduce
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "SVar filling and reading was not processed fast enough"
        
    v2 |> SVar.isFilled |> shouldEqual true
    
[<Test>]
let ``Check SVar with mutable data`` () =
    // An incredibly dangerous test case
    // SVar should never return mutable data
    // Because such data can be updated by any async workflow at any time without notice
    // Such behaviour is very dangerous for correctness of your code
    // and of course it is not thread safe as is
    let v = SVar.create<SampleMutableRecord> ()
    
    let mutable result = ""
    
    let isFilled = v |> SVar.isFilled
    
    isFilled |> shouldEqual false
    
    Async.Parallel [
        async {
            let! r = v |> SVar.read WithoutTimeout
            result <- r.Value
            r.Value <- "Option A"
        }
        async {
            let data = {
                Value = "Hello World"
                Count = 1
            }
            do! v |> SVar.fill data
        }
        async {
            let data = {
                Value = "Strange"
                Count = 42
            }
            let! r = v |> SVar.read WithoutTimeout
            result <- r.Value
            r.Value <- "Option B"
            do! v |> SVar.ignoreFill data
            let! r = v |> SVar.read WithoutTimeout
            result <- r.Value
        }
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    let isFilled = v |> SVar.isFilled
    
    isFilled |> shouldEqual true
    result |> shouldNotEqual "Hello World"
