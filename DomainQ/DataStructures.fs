module DomainQ.DataStructures

open System
open System.Collections.Generic
open FSharp.Control
open NodaTime

[<Measure>] type ms
exception PutTimeout
exception TakeTimeout
exception FillTimeout
exception ReadTimeout
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
    abstract member Read: unit -> Async<'T>
    abstract member Read: int<ms> -> Async<'T>
    abstract member Value: unit -> Async<'T option>

type BoundedMbRequest<'T> =
    | Put of 'T * AsyncReplyChannel<unit option>
    | Take of AsyncReplyChannel<'T option>
    | Count of AsyncReplyChannel<int option>
    
type SVarRequest<'T> =
    | Fill of 'T * AsyncReplyChannel<SVarResult>
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
        let isWithinMaxCapacity () = queue.Count < capacity
        
        let receive ( a: 'T ) ( response: AsyncReplyChannel<unit option> ) = async {
            queue.Enqueue a
            response.Reply ( Some () )
        }
        
        let send ( response: AsyncReplyChannel<'T option> ) = async {
            let a = queue.Dequeue ()
            response.Reply ( Some a )
        }
        
        let count ( response: AsyncReplyChannel<int option> ) = async {
            response.Reply ( Some queue.Count )
        }
        
        let skip m = async {
            match m with
            | Put ( _, r ) -> r.Reply None
            | Take r -> r.Reply None
            | Count r -> r.Reply None
        }
        
        let trySend () =
            let instant = SystemClock.Instance.GetCurrentInstant ()
            inbox.Scan <| function
                | Some i, r when i > instant -> skip r |> Some
                | _, Take m -> send m |> Some
                | _, Count c -> count c |> Some
                | _ -> None
        
        let tryReceive () =
            let instant = SystemClock.Instance.GetCurrentInstant ()
            inbox.Scan <| function
                | Some i, r when i > instant -> skip r |> Some
                | _, Put ( m, r ) -> receive m r |> Some
                | _, Count c -> count c |> Some
                | _ -> None
            
        let trySendOrReceive () = async {
            let instant = SystemClock.Instance.GetCurrentInstant ()
            let! m = inbox.Receive ()
            
            match m with
            | Some i, r when i > instant -> return! skip r
            | _, Take m -> return! send m
            | _, Put ( m, r ) -> return! receive m r
            | _, Count c -> return! count c
        }
            
        let rec loop () = async {
            match queue.Count with
            | 0 -> do! tryReceive ()
            | _ when isWithinMaxCapacity () -> do! trySendOrReceive ()
            | _ -> do! trySend ()
            
            return! loop ()
        }
        
        loop ()
        
    
    let put ( timeout: int<ms> option ) =
        match timeout with
        | Some timeout ->
            let timeout = int64 timeout
            let c = SystemClock.Instance.GetCurrentInstant ()
            let d = Duration.FromMilliseconds timeout
            let i = c + d
            let put a = fun ch -> Some i, Put ( a, ch )
            put
        | None ->
            let put a = fun ch -> None, Put ( a, ch )
            put
            
    let take ( timeout: int<ms> option ) =
        match timeout with
        | Some timeout ->
            let timeout = int64 timeout
            let c = SystemClock.Instance.GetCurrentInstant ()
            let d = Duration.FromMilliseconds timeout
            let i = c + d
            fun ch -> Some i, Take ch
        | None ->
            fun ch -> None, Take ch
        
    interface IDomainQueue with
        member _.Id = queueId
        member _.Capacity = capacity
        member this.Count () = async {
            match! agent.PostAndAsyncReply ( fun ch -> None, Count ch ) with
            | Some v -> return v
            | None -> return failwith "Timeout!"
        }
        
    interface IWriteOnlyQueue<'T> with
        member _.Put ( a: 'T ) = async {
            let put = put None a
            match! agent.PostAndAsyncReply put with
            | Some () -> return ()
            | None -> return failwith "Timeout!"
        }
            
        member _.Put ( timeout: int<ms>, a: 'T ) = async {
            let put = put ( Some timeout ) a
            match! agent.PostAndAsyncReply put with
            | Some () -> ()
            | None -> failwith "Timeout!"
        }

    interface IReadOnlyQueue<'T> with
        member _.Take () = async {
            let take = take None
            match! agent.PostAndAsyncReply take with
            | Some v -> return v
            | None -> return failwith "Timeout!"
        }
        member this.Take ( timeout: int<ms> ) = async {
            let take = take ( Some timeout )
            match! agent.PostAndAsyncReply ( take, int timeout + 10 ) with
            | Some v -> return v
            | None -> return failwith "Timeout!"
        }

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
        member _.Read () =
            agent.PostAndAsyncReply Read
        member this.Read ( timeout: int<ms> ) = async {
            let mutable result = None
            let! r =
                Async.Choice [
                    async {
                        do! Async.Sleep ( int timeout )
                        return Some false
                    }
                    async {
                        let! r = agent.PostAndAsyncReply Read
                        result <- Some r
                        return Some true
                    }
                ]
                
            match r with
            | Some b when b ->
                match result with
                | Some v -> return v
                | None -> return failwith "Result was not returned"
            | _ -> return failwith "Timeout occured"
        }
        member _.Value () =
            agent.PostAndAsyncReply State

    interface IDisposable with
        member _.Dispose () = ( agent :> IDisposable ).Dispose ()
    
module SVar =
    let create<'T> () = new SVar<'T> ()
    let tryFill ( m: 'T ) ( mb: IVar<'T> ) = mb.Fill m
    let fill ( m: 'T ) ( mb: IVar<'T> ) : Async<unit> = async {
        let! r = tryFill m mb
        match r with
        | Ok _ -> ()
        | Error _ -> failwith "IVar is already filled"
    }
    let ignoreFill ( m: 'T ) ( mb: IVar<'T> ) = mb |> tryFill m |> Async.Ignore   
    let read ( timoutOptions: TimeoutOption ) ( mb: IVar<'T> ) : Async<'T> =
        match timoutOptions with
        | WithoutTimeout -> mb.Read ()
        | WithTimeoutOf t -> mb.Read t
    let isFilled ( mb: IVar<_> ) : bool =
        async {
            let! s = mb.Value ()
            match s with
            | Some _ -> return true
            | None -> return false
        }
        |> Async.RunSynchronously
