module DomainQ.Tests

open NUnit.Framework
open FsUnitTyped

open DomainQ.DataStructures

[<SetUp>]
let Setup () =
    ()

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
    let put msg = async { do! mb |> BoundedMb.put msg }
    let take () = async { return! mb |> BoundedMb.take }
    
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
