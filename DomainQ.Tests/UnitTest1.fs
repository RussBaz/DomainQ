module DomainQ.Tests

open NUnit.Framework
open FsUnitTyped

open FSharp.Control

open DomainQ.DataStructures

[<SetUp>]
let Setup () =
    ()

let putMsgIn mb msg = async { do! mb |> BoundedMb.put msg }
let takeMsgFrom mb () = async { return! mb |> BoundedMb.take }

[<Test>]
let ``Verifying Basic BoundedMb Behaviour`` () =
    let getCurrentStats mb = async {
        let capacity = mb |> BoundedMb.capacity |> QueueSize.value
        let! count = mb |> BoundedMb.count
        let! isFull = mb |> BoundedMb.isFull
        return ( capacity, count, isFull )
    }
    let expectedCapacity = 2
    let mb = BoundedMb.create ( QueueSize expectedCapacity )
    let put = putMsgIn mb
    let take = takeMsgFrom mb
    
    // Trying to fill the queue till its full
    async {
        let expectedCount = 0
        let! capacity, count, isFull = getCurrentStats mb
        
        capacity |> shouldEqual expectedCapacity
        count |> shouldEqual expectedCount
        isFull |> shouldEqual false
        
        let expectedCount = 1
        do! put $"Message {expectedCount}"
        
        let! capacity, count, isFull = getCurrentStats mb
        
        capacity |> shouldEqual expectedCapacity
        count |> shouldEqual expectedCount
        isFull |> shouldEqual false
        
        let expectedCount = 2
        do! put $"Message {expectedCount}"
        
        let! capacity, count, isFull = getCurrentStats mb
        
        capacity |> shouldEqual expectedCapacity
        count |> shouldEqual expectedCount   
        isFull |> shouldEqual true
        
        let expectedCount = 1
        // Do not forget to take an item before getting the count
        let! r = take ()
        let! capacity, count, isFull = getCurrentStats mb
        
        r |> shouldEqual "Message 1"
        capacity |> shouldEqual expectedCapacity
        count |> shouldEqual expectedCount   
        isFull |> shouldEqual false
        
        let expectedCount = 0
        // Do not forget to take an item before getting the count
        let! r = take ()
        let! capacity, count, isFull = getCurrentStats mb
        
        r |> shouldEqual "Message 2"
        capacity |> shouldEqual expectedCapacity
        count |> shouldEqual expectedCount   
        isFull |> shouldEqual false
        
    }
    |> Async.RunSynchronously
    
[<Test>]
let ``Bounded Mb will stop accepting new messages when full`` () =
    let expectedCapacity = 2
    let mb = BoundedMb.create ( QueueSize expectedCapacity )
    let put = putMsgIn mb
    let take = takeMsgFrom mb
    
    let checkIfFull () =
        async {
            let! count = mb |> BoundedMb.count
            let! isFull = mb |> BoundedMb.isFull
            
            count |> shouldEqual expectedCapacity
            isFull |> shouldEqual true
        }
        |> Async.RunSynchronously
        
    let checkIfEmpty () =
        async {
            let! count = mb |> BoundedMb.count
            let! isFull = mb |> BoundedMb.isFull
            
            count |> shouldEqual 0
            isFull |> shouldEqual false
        }
        |> Async.RunSynchronously
    
    let fill = async {
        do! put "message"
        return Some ()
    }
    
    let retrieve = async {
        let! _ = take ()
        return Some ()
    }
    
    let timeout ( ms: int ) = async {
        do! Async.Sleep ms
        return None
    }
    
    let r =
        Async.Choice [
            timeout 100
            fill
            fill
        ]
        |> Async.RunSynchronously
        
    match r with
    | Some () -> Assert.Pass ()
    | None -> Assert.Fail "Timeout occured before the messages were put in the queue"
    
    checkIfFull ()
    
    let r =
        Async.Choice [
            timeout 1000
            fill
        ]
        |> Async.RunSynchronously
        
    match r with
    | Some () -> Assert.Fail "Timeout did not occur and hence the queue did not block new messages once it filled up"
    | None -> Assert.Pass ()
    
    // The queue stats should have not changed
    checkIfFull ()
    
    // Verifies that the blocked message putting operation would complete
    // once the queue has a capacity for a new message
    let r =
        Async.Choice [
            timeout 100
            fill
            retrieve
            retrieve
            fill
            retrieve
            retrieve
        ]
        |> Async.RunSynchronously
        
    match r with
    | Some () -> Assert.Pass ()
    | None -> Assert.Fail "Timeout occured before the messages were put in the queue"
    
    checkIfEmpty ()
    
[<Test>]
let ``Verifying Streaming Behaviour of BoundedMb`` () =
    let expectedCapacity = 2
    
    let expected = [ 1; 2; 4; 8 ]
    let mutable result = []
    
    let performTheTests () =
        // BoundedMb is IDisposable
        use mb = BoundedMb.create ( QueueSize expectedCapacity )
        let put = putMsgIn mb
    
        // Testing streaming with AsyncSeq
        async {
            for i in BoundedMb.stream mb do
                result <- i :: result
        }
        |> Async.StartImmediate
    
        async {
            do! put 8
            do! put 4
            do! put 2
            do! put 1
            // Generating a tiny delay, allowing the result to get updated
            // Otherwise the queue would be disposed before we can finish processing the messages
            do! Async.Sleep 1
        }
        |> Async.RunSynchronously
            
    performTheTests ()
    
    result |> shouldEqual expected
    
[<Test>]
let ``Nonsensical queue sizes should result in a failure`` () =
    let capacity = 0
    
    shouldFail ( fun _ -> BoundedMb.create ( QueueSize capacity ) |> ignore )
    
    let capacity = -1
    
    shouldFail ( fun _ -> BoundedMb.create ( QueueSize capacity ) |> ignore )
