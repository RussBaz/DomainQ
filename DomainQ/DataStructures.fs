module DomainQ.DataStructures

open System
open System.Collections.Generic
open FSharp.Control

type QueueSize = QueueSize of int

type SVarResult = Result<unit, unit>

type IDomainQueue =
    abstract member Capacity: int
    abstract member Count: unit -> Async<int>

type IWriteOnlyQueue<'T> =
    inherit IDomainQueue
    abstract member Put: 'T -> Async<unit>
    
type IReadOnlyQueue<'T> =
    inherit IDomainQueue
    abstract member Take: unit -> Async<'T>
    
type ISubscribable<'T> =
    abstract member Subscribe: unit -> Guid * IReadOnlyQueue<'T>
    abstract member Unsubscribe: Guid -> unit
    abstract member UnsubscribeAll: unit -> unit
    
type IVar<'T> =
    abstract member Fill: 'T -> Async<SVarResult>
    abstract member Read: unit -> Async<'T>
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
        member _.Capacity = capacity
        member this.Count () =
            agent.PostAndAsyncReply Count
        
    interface IWriteOnlyQueue<'T> with
        member _.Put ( a: 'T ) =
            agent.PostAndAsyncReply ( fun ch -> Put ( a, ch ) )

    interface IReadOnlyQueue<'T> with
        member _.Take () =
            agent.PostAndAsyncReply Take

    interface IDisposable with
        member _.Dispose () = ( agent :> IDisposable ).Dispose ()
        
type WriteOnlyQueueWrapper<'T> internal ( ofQ: IWriteOnlyQueue<'T> ) =
    interface IWriteOnlyQueue<'T> with
        member _.Put ( a: 'T ) = ofQ.Put a
        member _.Capacity = ofQ.Capacity
        member _.Count () = ofQ.Count ()
    
type ReadOnlyQueueWrapper<'T> internal ( ofQ: IReadOnlyQueue<'T> ) =
    interface IReadOnlyQueue<'T> with
        member _.Take () = ofQ.Take ()
        member _.Capacity = ofQ.Capacity
        member _.Count () = ofQ.Count ()
    
module BoundedMb =
    let create<'T> ( capacity: QueueSize ) =
        new BoundedMb<'T> ( QueueSize.value capacity )
    let put ( m: 'T ) ( mb: IWriteOnlyQueue<'T> ) : Async<unit> = mb.Put m
    let take ( mb: IReadOnlyQueue<'T> ) : Async<'T> = mb.Take ()
    let stream ( mb: IReadOnlyQueue<'T> ) = asyncSeq {
        let rec loop () = asyncSeq {
            let! v = take mb
            yield v
            yield! loop ()
        }
        
        yield! loop ()
    }
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
        |> Async.Start
        
        WriteOnlyQueueWrapper q :> IWriteOnlyQueue<'T>
        
    let ofBoundedMb ( m: BoundedMb<'T> ) = WriteOnlyQueueWrapper m :> IWriteOnlyQueue<'T>
    
module ReadOnlyQueue =
    let ofBoundedMb ( m: BoundedMb<'T> ) = ReadOnlyQueueWrapper m :> IReadOnlyQueue<'T>
    
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
    let tryFill ( m: 'T ) ( mb: IVar<'T> ) : Async<unit> = mb.Fill m |> Async.Ignore
    let read ( mb: IVar<'T> ) : Async<'T> = mb.Read ()
    let isFilled ( mb: IVar<_> ) : bool =
        async {
            let! s = mb.Value ()
            match s with
            | Some _ -> return true
            | None -> return false
        }
        |> Async.RunSynchronously