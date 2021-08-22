module DomainQ.DataStructures

open System
open System.Collections.Generic
open FSharp.Control

[<Measure>] type ms
type QueueSize = QueueSize of int

type SVarResult = Result<unit, unit>

type TimeoutOption =
    | WithoutTimeout
    | WithTimeoutOf of int<ms>

type IDomainQueue =
    abstract member Id: Guid
    abstract member Capacity: int
    abstract member Count: unit -> Async<int>

type IWriteOnlyQueue<'T> =
    inherit IDomainQueue
    abstract member Put: 'T -> Async<unit>
    abstract member Put: int<ms> * 'T -> Async<unit>
    
type IReadOnlyQueue<'T> =
    inherit IDomainQueue
    abstract member Take: unit -> Async<'T>
    abstract member Take: int<ms> -> Async<'T>
    
type ISubscribable<'T> =
    abstract member Subscribe: unit -> Guid * IReadOnlyQueue<'T>
    abstract member Unsubscribe: Guid -> unit
    abstract member UnsubscribeAll: unit -> unit
    
type IVar<'T> =
    abstract member Fill: 'T -> Async<SVarResult>
    abstract member Fill: int<ms> * 'T -> Async<SVarResult>
    abstract member Read: unit -> Async<'T>
    abstract member Read: int<ms> -> Async<'T>
    abstract member Value: unit -> Async<'T option>

type BoundedMbRequest<'T> =
    | Put of 'T * AsyncReplyChannel<unit>
    | Take of AsyncReplyChannel<'T>
    | Count of AsyncReplyChannel<int>
    
type SVarRequest<'T> =
    | Fill of 'T * AsyncReplyChannel<Result<unit, unit>>
    | Read of AsyncReplyChannel<'T>
    | State of AsyncReplyChannel<'T option>

module QueueSize =
    let value ( QueueSize v ) = v
   
type BoundedMb<'T> internal ( capacity: int ) =
    do
        if capacity < 1 then failwith "BoundedMb capacity must be larger than 0"
    
    let queueId = Guid.NewGuid ()
    let agent = MailboxProcessor.Start <| fun inbox ->
        let queue = Queue<_> ()
        let isWithinMaxCapacity = queue.Count < capacity
        
        let receive ( a: 'T ) ( response: AsyncReplyChannel<unit> ) = async {
            queue.Enqueue a
            response.Reply ()
        }
        
        let send ( response: AsyncReplyChannel<'T> ) = async {
            let a = queue.Dequeue ()
            response.Reply a
        }
        
        let count ( response: AsyncReplyChannel<int> ) = async {
            response.Reply queue.Count
        }
        
        let trySend () =
            inbox.Scan <| function
                | Take m -> send m |> Some
                | Count c -> count c |> Some
                | _ -> None
        
        let tryReceive () =
            inbox.Scan <| function
                | Put ( m, r ) -> receive m r |> Some
                | Count c -> count c |> Some
                | _ -> None
            
        let trySendOrReceive () = async {
            let! m = inbox.Receive ()
            
            match m with
            | Take m -> return! send m
            | Put ( m, r ) -> return! receive m r
            | Count c -> return! count c
        }
            
        let rec loop () = async {
            match queue.Count with
            | 0 -> do! tryReceive ()
            | _ when isWithinMaxCapacity -> do! trySendOrReceive ()
            | _ -> do! trySend ()
            
            return! loop ()
        }
        
        loop ()
        
    interface IDomainQueue with
        member _.Id = queueId
        member _.Capacity = capacity
        member this.Count () =
            agent.PostAndAsyncReply Count
        
    interface IWriteOnlyQueue<'T> with
        member _.Put ( a: 'T ) =
            agent.PostAndAsyncReply ( fun ch -> Put ( a, ch ) )
            
        member _.Put ( timeout: int<ms>, a: 'T ) =
            let put = fun ch -> Put ( a, ch )
            agent.PostAndAsyncReply ( put, int timeout )

    interface IReadOnlyQueue<'T> with
        member _.Take () =
            agent.PostAndAsyncReply Take
        member this.Take ( timeout: int<ms> ) =
            agent.PostAndAsyncReply ( Take, int timeout )

    interface IDisposable with
        member _.Dispose () = ( agent :> IDisposable ).Dispose ()
        
type WriteOnlyQueueWrapper<'T> internal ( ofQ: IWriteOnlyQueue<'T> ) =
    let queueId = Guid.NewGuid ()
    member _.WrappedId = ofQ.Id
    
    interface IWriteOnlyQueue<'T> with
        member _.Put a = ofQ.Put a
        member _.Put ( timeout, a ) = ofQ.Put ( timeout, a )
        member _.Id = queueId
        member _.Capacity = ofQ.Capacity
        member _.Count () = ofQ.Count ()
    
type ReadOnlyQueueWrapper<'T> internal ( ofQ: IReadOnlyQueue<'T> ) =
    let queueId = Guid.NewGuid ()
    member _.WrappedId = ofQ.Id
    
    interface IReadOnlyQueue<'T> with
        member _.Take () = ofQ.Take ()
        member _.Take timeout = ofQ.Take timeout
        member _.Id = queueId
        member _.Capacity = ofQ.Capacity
        member _.Count () = ofQ.Count ()
    
module BoundedMb =
    let create<'T> ( capacity: QueueSize ) =
        new BoundedMb<'T> ( QueueSize.value capacity )
    let put ( timoutOptions: TimeoutOption ) ( m: 'T ) ( mb: IWriteOnlyQueue<'T> ) : Async<unit> =
        match timoutOptions with
        | WithoutTimeout -> mb.Put m
        | WithTimeoutOf t -> mb.Put ( t, m )
    let take ( timoutOptions: TimeoutOption ) ( mb: IReadOnlyQueue<'T> ) : Async<'T> =
        match timoutOptions with
        | WithoutTimeout -> mb.Take ()
        | WithTimeoutOf t -> mb.Take t
    let stream ( mb: IReadOnlyQueue<'T> ) = asyncSeq {
        let rec loop () = asyncSeq {
            let! v = take WithoutTimeout mb
            yield v
            yield! loop ()
        }
        
        yield! loop ()
    }
    let id ( mb: IDomainQueue ) = mb.Id
    let capacity ( mb: IDomainQueue ) = mb.Capacity |> QueueSize
    let count ( mb: IDomainQueue ) : Async<int> = mb.Count ()
    let isFull ( mb: IDomainQueue ) : Async<bool> = async {
        let! r = mb.Count ()
        return r = mb.Capacity
    }
    
module WriteOnlyQueue =
    let create<'T> ( size: QueueSize ) ( handler: 'T -> Async<unit> ) =
        let q = BoundedMb.create size
        
        async {
            for i in BoundedMb.stream q do
                do! handler i
        }
        |> Async.StartImmediate
        
        WriteOnlyQueueWrapper q :> IWriteOnlyQueue<'T>
        
    let ofBoundedMb ( m: IWriteOnlyQueue<'T> ) = WriteOnlyQueueWrapper m :> IWriteOnlyQueue<'T>
    
module ReadOnlyQueue =
    let ofBoundedMb ( m: IReadOnlyQueue<'T> ) = ReadOnlyQueueWrapper m :> IReadOnlyQueue<'T>
    
type SVar<'T> internal () =
    let agent = MailboxProcessor.Start <| fun inbox ->
        let mutable value: 'T option = None
        let mutable readers: AsyncReplyChannel<'T> list = []
        
        let fill ( a: 'T ) ( response: AsyncReplyChannel<SVarResult> ) = async {
            match value with
            | Some _ -> Error () |> response.Reply
            | None ->
                value <- Some a
                readers
                |> List.iter ( fun r -> r.Reply a )
                readers <- []
                Ok () |> response.Reply
        }
        
        let read ( response: AsyncReplyChannel<'T> ) = async {
            match value with
            | Some v -> response.Reply v
            | None -> readers <- response :: readers
        }
        
        let state ( response: AsyncReplyChannel<'T option> ) = async {
            response.Reply value
        }
        
        let rec loop () = async {
            let! m = inbox.Receive ()
            
            match m with
            | Fill ( v, r ) -> do! fill v r
            | Read r -> do! read r
            | State r -> do! state r
            
            return! loop ()
        }
        
        loop ()
        
    interface IVar<'T> with
        member _.Fill ( a: 'T ) =
            agent.PostAndAsyncReply ( fun ch -> Fill ( a, ch ) )

        member this.Fill ( timeout: int<ms>, a: 'T ) =
            let fill = fun ch -> Fill ( a, ch )
            agent.PostAndAsyncReply ( fill, int timeout )

        member _.Read () =
            agent.PostAndAsyncReply Read
        member this.Read ( timeout: int<ms> ) =
            agent.PostAndAsyncReply ( Read, int timeout )
            
        member _.Value () =
            agent.PostAndAsyncReply State

    interface IDisposable with
        member _.Dispose () = ( agent :> IDisposable ).Dispose ()
    
module SVar =
    let create<'T> () = new SVar<'T> ()
    let fill ( m: 'T ) ( mb: IVar<'T> ) : Async<unit> = async {
        let! r = mb.Fill m
        match r with
        | Ok _ -> ()
        | Error _ -> failwith "IVar is already filled"
    }
    let tryFill ( m: 'T ) ( mb: IVar<'T> ) = mb.Fill m
    let ignoreFill ( m: 'T ) ( mb: IVar<'T> ) = mb |> tryFill m |> Async.Ignore
    
    let fillWithTimeout ( timeout: int<ms> ) ( m: 'T ) ( mb: IVar<'T> ) : Async<unit> = async {
        let! r = mb.Fill ( timeout, m )
        match r with
        | Ok _ -> ()
        | Error _ -> failwith "IVar is already filled"
    }
    let tryFillWithTimeout ( timeout: int<ms> ) ( m: 'T ) ( mb: IVar<'T> ) = mb.Fill ( timeout, m )
    let ignoreFillWithTimeout ( timeout: int<ms> ) ( m: 'T ) ( mb: IVar<'T> ) =
        mb
        |> tryFillWithTimeout timeout m
        |> Async.Ignore
    
    let read ( mb: IVar<'T> ) : Async<'T> = mb.Read ()
    let readWithTimeout ( timeout: int<ms> ) ( mb: IVar<'T> ) : Async<'T> = mb.Read timeout
    let isFilled ( mb: IVar<_> ) : bool =
        async {
            let! s = mb.Value ()
            match s with
            | Some _ -> return true
            | None -> return false
        }
        |> Async.RunSynchronously
