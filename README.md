# DomainQ

F# Bounded Mailbox and other types of queues and simple synchronisation primitives for Async workflows.

## BoundedMb module
```BoundedMb``` will block new messages once the capacity is reached. It will resume accepting new messages once it is no longer full.

```F#
open DomainQ.DataStructures

// Creates a new Bounded Mailbox with a maximum capacity of 100 messages
let mb = BoundedMb.create ( QueueSize 100 )

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
    // Note that the capacity is not an async function
    let capacity = mb |> BoundedMb.capacity |> QueueSize.value
    let! count = mb |> BoundedMb.count
    let! isFull = mb |> BoundedMb.isFull
}

// You can 'stream' messages by returning an AsyncSeq from the BoundedMb

// Requires FSharp.Control.AsyncSeq
// https://github.com/fsprojects/FSharp.Control.AsyncSeq

// Warning! It will exist for as long as the BOunded Mailbox exists
open FSharp.Control

async {
    for i in BoundedMb.stream mb do
        printfn $"Message: {i}"
}
|> Async.StartImmediate
```