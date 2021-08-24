module DomainQ.Tests.BoundedMbTests

open NUnit.Framework
open FsUnitTyped

open FSharp.Control

open DomainQ.DataStructures

[<SetUp>]
let Setup () =
    ()

let putMsgIn mb too msg = async { do! mb |> BoundedMb.put too msg }
let takeMsgFrom mb too = async { return! mb |> BoundedMb.take too }
let timeout ( ms: int ) = async {
    do! Async.Sleep ms
    return Some false
}

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
    let put = putMsgIn mb WithoutTimeout
    let take () = takeMsgFrom mb WithoutTimeout
    
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
    let put = putMsgIn mb WithoutTimeout
    let take () = takeMsgFrom mb WithoutTimeout
    
    let reduce ( data: Async<bool option []> ) = async {
        let! data = data
        for i in data do
            match i with
            | Some b when b -> ()
            | _ -> failwith "Something went wrong"
        return Some true
    }
    
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
        return Some true
    }
    
    let retrieve = async {
        let! _ = take ()
        return Some true
    }
        
    Async.Choice [
        timeout 100
        async {
            let! _ = fill
            let! _ = fill
            return ( Some true )
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "Timeout occured before the messages were put in the queue"
    
    checkIfFull ()
    
    let r =
        Async.Choice [
            timeout 1000
            fill
        ]
        |> Async.RunSynchronously
        
    match r with
    | Some b when b -> Assert.Fail "Timeout did not occur and hence the queue did not block new messages once it filled up"
    | _ -> Assert.Pass ()
    
    // The queue stats should have not changed
    checkIfFull ()
    
    // Verifies that the blocked message putting operation would complete
    // once the queue has a capacity for a new message
    let r =
        Async.Choice [
            timeout 100
            Async.Parallel [
                fill
                retrieve
                retrieve
                fill
                retrieve
                retrieve
            ]
            |> reduce
        ]
        |> Async.RunSynchronously
        
    match r with
    | Some b when b -> Assert.Pass ()
    | _ -> Assert.Fail "Timeout occured before the messages were put in the queue"
    
    checkIfEmpty ()
    
[<Test>]
let ``Verifying Streaming Behaviour of BoundedMb`` () =
    let expectedCapacity = 2
    
    let expected = [ 1; 2; 4; 8 ]
    let mutable result = []
    
    let performTheTests () =
        // BoundedMb is IDisposable
        use mb = BoundedMb.create ( QueueSize expectedCapacity )
        let put = putMsgIn mb WithoutTimeout
    
        // Testing streaming with AsyncSeq
        async {
            for i in BoundedMb.stream mb do
                result <- i :: result
        }
        |> Async.StartImmediate
    
        let main = async {
            do! put 8
            do! put 4
            do! put 2
            do! put 1
            // Generating a tiny delay, allowing the result to get updated
            // Otherwise the queue would be disposed before we can finish processing the messages
            while result.Length < expected.Length do
                do! Async.Sleep 5
                
            return Some true
        }
        
        Async.Choice [
            timeout 100
            main
        ]
        |> Async.RunSynchronously
        |> function
            | Some b when b -> ()
            | _ -> Assert.Fail "Timeout occured"
                    
    performTheTests ()
    
    result |> shouldEqual expected
    
[<Test>]
let ``Nonsensical queue sizes should result in a failure`` () =
    let capacity = 0
    
    shouldFail ( fun _ -> BoundedMb.create ( QueueSize capacity ) |> ignore )
    shouldFail ( fun _ -> WriteOnlyQueue.create ( QueueSize capacity ) ( fun _ -> async { return () } ) |> ignore )
    
    let capacity = -1
    
    shouldFail ( fun _ -> BoundedMb.create ( QueueSize capacity ) |> ignore )
    shouldFail ( fun _ -> WriteOnlyQueue.create ( QueueSize capacity ) ( fun _ -> async { return () } ) |> ignore )
    
[<Test>]
let ``Bounded Mailbox queue should have unique ids`` () =
    let mb1 = BoundedMb.create<string> ( QueueSize 10 )
    let mb2 = BoundedMb.create<string> ( QueueSize 10 )
    
    let id1 = mb1 |> BoundedMb.id
    let id2 = mb2 |> BoundedMb.id
    
    id1 |> shouldNotEqual id2
    
    // Ids also should not change while BoundedMb exists
    id1 |> shouldEqual ( mb1 |> BoundedMb.id )
    id1 |> shouldEqual ( mb1 |> BoundedMb.id )
    
[<Test>]
let ``Bounded Mailbox queue can be cast as IWriteOnly and IReadOnly queue`` () =
    let mb = BoundedMb.create<string> ( QueueSize 10 )
    
    let woq = mb :> IWriteOnlyQueue<string>
    let roq = mb :> IReadOnlyQueue<string>
    
    let idw = woq |> BoundedMb.id
    let idr = roq |> BoundedMb.id
    
    idw |> shouldEqual idr
    
    let put = putMsgIn woq WithoutTimeout
    let take () = takeMsgFrom roq WithoutTimeout
    
    let expected = "Hello World!"
    
    let result =
        async {
            do! put expected
            let! m = take ()
            return m
        }
        |> Async.RunSynchronously
    
    result |> shouldEqual expected
        
    let mbw = woq :?> BoundedMb<string>
    let mbr = roq :?> BoundedMb<string>
    
    let idw = mbw |> BoundedMb.id
    let idr = mbr |> BoundedMb.id
    
    idw |> shouldEqual idr
    
[<Test>]
let ``Verify timeout behaviour for the BoundedMb methods`` () =
    let mb = BoundedMb.create<int> ( QueueSize 2 )
    let put = putMsgIn mb
    let take = takeMsgFrom mb
    
    let expected = [ 2; 1 ]
    let mutable result = []
        
    Async.Choice [
        timeout 100
        async {
            do! put WithoutTimeout 1
            do! put WithoutTimeout 2
            try
                do! put ( WithTimeoutOf 10<ms> ) 3
                do! put ( WithTimeoutOf 1<ms> ) 4
                do! put ( WithTimeoutOf 1<ms> ) 5
                do! put ( WithTimeoutOf 1<ms> ) 6
            with
            | _ -> ()
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "The put operation did not timeout fast enough"
    
    Async.Choice [
        async {
            for i in BoundedMb.stream mb do
                result <- i :: result
            return Some true
        }
        timeout 2000
    ]
    |> Async.Ignore
    |> Async.RunSynchronously
    
    result |> shouldEqual expected
    
    Async.Choice [
        timeout 1000
        async {
            let! _ = take WithoutTimeout
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> Assert.Fail "The Take operation did not block on empty queue"
        | _ -> ()
        
    Async.Choice [
        timeout 501
        async {
            try
                let! _ = take ( WithTimeoutOf 500<ms> )
                Assert.Fail "Timeout was not raised"
            with
            | _ -> ()
            return Some true
        }
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "Timeout was not raised fast enough"
    
[<Test>]
let ``Check Read/Write Only Wrapper for the BoundedMb`` () =
    let c = 10
    let mb = BoundedMb.create<string> ( QueueSize c )
       
    let woq = WriteOnlyQueue.ofBoundedMb mb
    let roq = ReadOnlyQueue.ofBoundedMb mb
    
    let woqNested = WriteOnlyQueue.ofBoundedMb woq
    let roqNested = ReadOnlyQueue.ofBoundedMb roq
    
    let put = putMsgIn woq WithoutTimeout
    let take () = takeMsgFrom roq WithoutTimeout
    
    let putNested = putMsgIn woqNested WithoutTimeout
    let takeNested () = takeMsgFrom roqNested WithoutTimeout
    
    let mid = mb |> BoundedMb.id
    
    let wid = woq |> BoundedMb.id
    let widWrapped = ( woq :?> WriteOnlyQueueWrapper<string> ).WrappedId
    
    let rid = roq |> BoundedMb.id
    let ridWrapped = ( roq :?> ReadOnlyQueueWrapper<string> ).WrappedId
    
    let widNested = woqNested |> BoundedMb.id
    let widWrappedNested = ( woqNested :?> WriteOnlyQueueWrapper<string> ).WrappedId
    
    let ridNested = roqNested |> BoundedMb.id
    let ridWrappedNested = ( roqNested :?> ReadOnlyQueueWrapper<string> ).WrappedId
    
    let capacityM = mb |> BoundedMb.capacity |> QueueSize.value
    let capacityW = woq |> BoundedMb.capacity |> QueueSize.value
    let capacityR = roq |> BoundedMb.capacity |> QueueSize.value
    let capacityWN = woqNested |> BoundedMb.capacity |> QueueSize.value
    let capacityRN = roqNested |> BoundedMb.capacity |> QueueSize.value
    
    capacityM |> shouldEqual c 
    capacityW |> shouldEqual c 
    capacityR |> shouldEqual c 
    capacityWN |> shouldEqual c 
    capacityRN |> shouldEqual c 
    
    wid |> shouldNotEqual mid
    rid |> shouldNotEqual mid
    wid |> shouldNotEqual rid
    widWrapped |> shouldEqual mid
    ridWrapped |> shouldEqual mid
    
    widNested |> shouldNotEqual wid
    ridNested |> shouldNotEqual rid
    widNested |> shouldNotEqual ridNested
    widWrappedNested |> shouldEqual wid
    ridWrappedNested |> shouldEqual rid
    
    // It does not matter which wrapper one uses to put or take messages
    // They are all sharing one queue underneath
    async {
        let m1 = "hello world 1"
        let m2 = "hello world 2"
        let m3 = "hello world 3"
        
        do! put m1
        do! put m2
        do! putNested m3
        
        let! countM = mb |> BoundedMb.count
        let! countW = woq |> BoundedMb.count
        let! countR = roq |> BoundedMb.count
        
        countM |> shouldEqual 3
        countW |> shouldEqual 3
        countR |> shouldEqual 3
        
        // Different order of queues used
        let! r1 = take ()
        let! r2 = takeNested ()
        let! r3 = take ()
        
        let! countM = mb |> BoundedMb.count
        let! countW = woq |> BoundedMb.count
        let! countR = roq |> BoundedMb.count
        
        countM |> shouldEqual 0
        countW |> shouldEqual 0
        countR |> shouldEqual 0
        
        r1 |> shouldEqual m1
        r2 |> shouldEqual m2
        r3 |> shouldEqual m3
    }
    |> Async.RunSynchronously
    
    fun _ ->
        woq :?> BoundedMb<string>
        |> ignore
    |> shouldFail
    
    fun _ ->
        roq :?> BoundedMb<string>
        |> ignore
    |> shouldFail
    
    fun _ ->
        woqNested :?> BoundedMb<string>
        |> ignore
    |> shouldFail
    
    fun _ ->
        roqNested :?> BoundedMb<string>
        |> ignore
    |> shouldFail
    
[<Test>]
let ``Check Write Only Wrapper 'create' method with a handler`` () =
    let expected = [ "Hello"; "world"; ""; "!" ]
    let mutable result = []
    
    let woq = WriteOnlyQueue.create ( QueueSize 10 ) ( fun ( i: string ) -> async {
        result <- result @ [ i ]
    } )
    
    let put = putMsgIn woq WithoutTimeout
    
    Async.Choice [
        async {
            for i in expected do
                do! put i
            while result.Length < expected.Length do
                do! Async.Sleep 10
            return Some true
        }
        timeout 100
    ]
    |> Async.RunSynchronously
    |> function
        | Some b when b -> ()
        | _ -> Assert.Fail "Timeout occured"
    
    result |> shouldEqual expected
