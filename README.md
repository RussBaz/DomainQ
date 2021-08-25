# DomainQ
[![Build Status](https://img.shields.io/github/workflow/status/RussBaz/DomainQ/.NET%20Core)](https://github.com/russbaz/domainq/actions/workflows/github-actions.yml)
[![Latest Published Nuget Version](https://img.shields.io/nuget/v/RussBaz.DomainQ)](https://www.nuget.org/packages/RussBaz.DomainQ/)

*docs for version: v2.0.0*
### Overview
F# Bounded Mailbox and other types of queues and simple synchronisation primitives for Async workflows with a secret sauce.

It can easily force asynchronous tasks to be processed in the same order as the message arrive in. It returns a simple async sequence of ordered messages.

The preferred use case is multiple writers and a single reader.

**WARNING! Never send over any data with mutable properties!**

`RussBaz.DomainQ` types do not guarantee any thread safety if you choose to do so.
```F#
// The Secret Sauce
open DomainQ.DataStructures
open FSharp.Control

let mb = BoundedMb.create ( QueueSize 100 )

// Reader
async {
    // Returns an AsynqSeq from FSharp.Control.AsyncSeq
    // https://github.com/fsprojects/FSharp.Control.AsyncSeq
    for i in BoundedMb.stream mb do
        printfn $"Message: {i}"
}

// Writer (will block when the queue is full)
let put = BoundedMb.put WithoutTimeout

async {
    do! mb |> put "Hello World!"
}
```
### Fast Travel:
* [Installation](#installation)
* [Available modules and types](#available-modules-and-types)
    * [BoundedMb - basic building block](#boundedmb-module)
    * [WriteOnlyQueue / ReadOnlyQueue - special case wrappers](#writeonlyqueue--readonlyqueue-modules)
    * [SVar - single write synchronisation variable](#svar-module)
* [Changelog](#changelog)
## Installation
**Prerequisites**

The package runs on .NET Core and uses F# 5.0.

**Installation**

With local `paket`
```
dotnet paket add RussBaz.DomainQ
```
With pure `dotnet`
```
dotnet add package RussBaz.DomainQ
```
## Available modules and types
### BoundedMb module
`BoundedMb` will block new messages once the capacity is reached. It will resume accepting new messages once it is no longer full.

```F#
open DomainQ.DataStructures

// Creates a new Bounded Mailbox with a maximum capacity of 100 messages
let mb = BoundedMb.create ( QueueSize 100 )

// The message type can be manually restricted to any type without any constraints
let bm = BoundedMb.create<int> ( QueueSize expectedCapacity )

// You can send any data of the same type to the BoundedMb
// WithoutTimeout option will block the writer
// till the queue has enough spare acapcity to accept a new message
async {
    do! mb |> BoundedMb.put WithoutTimeout "Hello World!"
}

// This snippet shows how to receive a message
// WithoutTimeout option will block the reader
// till there is a new message available in the queue
async {
    let! r = mb |> BoundedMb.take WithoutTimeout
    printfn $"Message: {r}"
}

// If you need to unblock the writer or reader after a certain timeout
// for example, after 1s
// You can use ( WithTimeoutOf 1000<ms> ) construct
async {
    do! mb |> BoundedMb.put ( WithTimeoutOf 1000<ms> ) "Hello World!"
}
// or if the timeout is always the same, you can use partial application
let put = BoundedMb.put ( WithTimeoutOf 1000<ms> )

async {
    do! mb |> put "Hello World!"
}
// The same applies to the take method
async {
    let! r = mb |> BoundedMb.take ( WithTimeoutOf 1000<ms> )
    printfn $"Message: {r}"
}

// Once the timeout is triggered, then the code will throw an exception
// When this happens, the message will no longer be taken in or out
// Lastly, timouts, especially for short periods of time
// can be very inaccurate in the async code
// Please do take this into an account and do not rely on their accuracy

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

### WriteOnlyQueue / ReadOnlyQueue modules
If you ever need to prevent the consumer from accessing the read or write side of your Bounded Mailbox, then you can use these modules to wrap it in a proxy object which only exposes the `IWriteOnlyQueue` or `IReadOnlyQueue` interfaces.

In addition, Write Only Queue wrapper can create its own BoundedMb if you need to create a quick message consumer.

```F#
open DomainQ.DataStructures

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
### SVar module
When you need a single write variable to share a state between async workflows, then you can use `SVar`.
```F#
open DomainQ.DataStructures

// SVar implements IDisposable
use v = SVar.create ()

// An example of common usage:
Async.Parallel [
    async {
        do! Async.Sleep 1000
        do! v |> SVar.fill 2
    }
    // Next async workflows would block till the variable is filled 
    async {
        let! r = v |> SVar.read WithoutTimeout
        printfn $"Value: {r}"
    }
    // If you do not want to wait forever
    // Then you can set up a timeout for a read operation
    async {
        try
            let! r = v |> SVar.read ( WithTimeoutOf 500<ms> )
            printfn $"Value: {r}"
        with
        | _ -> printfn "A timeout occured"
    }
    // This workflow would not block on read
    // because by that time the SVar is already set
    // However, if it was, WithoutTimeout option will ensure
    // that it will stay blocked till the SVar is set
    async {
        do! Async.Sleep 2000
        let! r = v |> SVar.read WithoutTimeout
        printfn $"Value: {r}"
    }
]

// You can specify the SVar type explicitly
let v2 = SVar.create<string> ()
// If ever need to check if the SVar is set, then you can use the following method
let isFilled = v2 |> SVar.isFilled

// Once the SVar is set, any further attempts to fill it will raise a Failure exception
// If you do not want any exceptions, you can try the following
async {
    let! r = v2 |> SVar.tryFill "Hello World!"
    match r with
    | Ok () -> printfn "Success."
    | Error () -> printfn "Failure. The SVar is already filled."
}
// Alternatively, if you do not care about the result of filling the SVar
// you can use ignoreFill function
// It performs exactly like tryFill but discards the result
async {
    do! v2 |> SVar.ignoreFill "Hello World!"
}
```
## Changelog
### 2.0.0 - 25.08.2021
* Breaking Changes - interfaces and signatures of existing functions were changed
* Updated BoundedMb put/take functions to receive timeout options as a parameter
* Updated SVar read function to receive timeout options as a parameter
* Major Bug fixed and tests adjusted - queue capacityt was not properly tracked
### 1.1.0 - 16.08.2021
* Quality of life improvements to SVar
    * New function `ignoreFill` which automatically discards the result of `tryFill`
### 1.0.1 - 15.08.2021
* Initial public release
* CI/CD with GitHub Actions to Nuget
* Available modules:
    * BoundedMb
    * WriteOnlyQueue / ReadOnlyQueue
    * SVar