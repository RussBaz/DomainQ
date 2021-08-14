# DomainQ

F# Bounded Mailbox and other types of queues and simple synchronisation primitives for Async workflows with a secret sauce.

It can easily force asynchronous tasks to be processed in order as a simple async sequence of messages.
```F#
// The Secret Sauce
open DomainQ.DataStructures
open FSharp.Control

let mb = BoundedMb.create ( QueueSize 100 )

async {
    for i in BoundedMb.stream mb do
        printfn $"Message: {i}"
}
```
## BoundedMb module
`BoundedMb` will block new messages once the capacity is reached. It will resume accepting new messages once it is no longer full.

```F#
open DomainQ.DataStructures

// Creates a new Bounded Mailbox with a maximum capacity of 100 messages
let mb = BoundedMb.create ( QueueSize 100 )

// The message type can be manually restricted to any type without any constraints
let bm = BoundedMb.create<int> ( QueueSize expectedCapacity )

// You can send any data of the same type to the BoundedMb
async {
    do! mb |> BoundedMb.put "Hello World!"
}

// This snippet shows how to receive a message
async {
    let! r = mb |> BoundedMb.take
    printfn $"Message: {r}"
}

// You can check the maximum message capacity and the current count of stored messages
async {
    let! count = mb |> BoundedMb.count
    let! isFull = mb |> BoundedMb.isFull
}

// Note that the capacity is not an async function
let capacity = mb |> BoundedMb.capacity |> QueueSize.value
// Nor is the queue ID
let qId = mb |> BoundedMb.id
// Each queue gets a unique GUID queue ID

// You can 'stream' messages by returning an AsyncSeq from the BoundedMb

// Requires FSharp.Control.AsyncSeq
// https://github.com/fsprojects/FSharp.Control.AsyncSeq

// Warning! It will exist for as long as the Bounded Mailbox exists
// The seq will be automatically exhausted once the queue is disposed
// All the outstanding (not taken) messages would disappear once that happens 
open FSharp.Control

async {
    for i in BoundedMb.stream mb do
        printfn $"Message: {i}"
}
|> Async.StartImmediate
```

## WriteOnlyQueue / ReadOnlyQueue modules
If you ever need to prevent the consumer from accessing the read or write side of your Bounded Mailbox, then you can use these modules to wrap it in a proxy object which only exposes the `IWriteOnlyQueue` or `IReadOnlyQueue` interfaces.

In addition, Write Only Queue wrapper can create its own BoundedMb if you need to create a quick message consumer.

```F#
let mb = BoundedMb.create<string> ( QueueSize 100 )
       
let woq = WriteOnlyQueue.ofBoundedMb mb
let roq = ReadOnlyQueue.ofBoundedMb mb

// A quick way to crate a write only message sink
let woq = WriteOnlyQueue.create ( QueueSize 10 ) ( fun i -> async {
    printfn $"Message: {i}"
} )

// Maximum capacity is still available
let capacityW = woq |> BoundedMb.capacity |> QueueSize.value
let capacityR = roq |> BoundedMb.capacity |> QueueSize.value

// As well as their current message count
async {
    let! countW = woq |> BoundedMb.count
    let! countR = roq |> BoundedMb.count
}
// Read and Write Wrappers get their own queue IDs
// Howver, the ID of the wrapped can still be accessed through a special proerty

// Wrapper ID - Write Only Wrapper
let wid = woq |> BoundedMb.id
// Wrapped ID - Write Only Wrapper
let originalWId = ( woq :?> WriteOnlyQueueWrapper<string> ).WrappedId
// Wrapper ID - Read Only Wrapper
let rid = roq |> BoundedMb.id
// Wrapped ID - Read Only Wrapper
let originalRId = ( roq :?> ReadOnlyQueueWrapper<string> ).WrappedId

// Wrapper can be wrapped in another wrapper but why would you?
// In that case its WrappedId property will show the inner wrapper id.
```