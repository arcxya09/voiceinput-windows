using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

// Exercises the production WebSocket send/receive loops without an account or network.
sealed class ScriptedSocket : WebSocket
{
    private readonly Channel<byte[]> incoming=Channel.CreateUnbounded<byte[]>();
    private WebSocketState state=WebSocketState.Open;
    private string taskId="";
    public string? RejectCode;
    public string FinalText="完整结果。";
    public bool IncompleteTail;
    public readonly ConcurrentQueue<byte[]> Pcm=[];
    public readonly ConcurrentQueue<string> Actions=[];
    public readonly TaskCompletionSource FirstPcm=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override WebSocketCloseStatus? CloseStatus=>null;
    public override string? CloseStatusDescription=>null;
    public override string? SubProtocol=>null;
    public override WebSocketState State=>state;
    public override void Abort(){state=WebSocketState.Aborted;incoming.Writer.TryComplete();}
    public override void Dispose()=>Abort();
    public override Task CloseAsync(WebSocketCloseStatus status,string? description,CancellationToken token)=>CloseOutputAsync(status,description,token);
    public override Task CloseOutputAsync(WebSocketCloseStatus status,string? description,CancellationToken token){state=WebSocketState.Closed;return Task.CompletedTask;}
    private void Emit(string name,object payload,string? code=null)
        =>incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new{header=new{task_id=taskId,@event=name,error_code=code,error_message="SECRET_REQUEST_DATA"},payload}));
    public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool end,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(type==WebSocketMessageType.Binary)
        {
            Pcm.Enqueue(buffer.ToArray());
            if(Pcm.Count==1)
            {
                Emit("result-generated",new{output=new{sentence=new{sentence_id=1,text="",sentence_end=false,begin_time=0,end_time=(long?)null}},usage=(object?)null});
                Emit("result-generated",new{output=new{sentence=new{sentence_id=0,heartbeat=true}},usage=(object?)null});
                FirstPcm.TrySetResult();
            }
        }
        else
        {
            using var doc=JsonDocument.Parse(buffer.AsMemory());var h=doc.RootElement.GetProperty("header");
            taskId=h.GetProperty("task_id").GetString()!;string action=h.GetProperty("action").GetString()!;Actions.Enqueue(action);
            if(action=="run-task")Emit(RejectCode==null?"task-started":"task-failed",new{},RejectCode);
            if(action=="finish-task")
            {
                Emit("result-generated",new{output=new{sentence=new{sentence_id=1,text=FinalText,sentence_end=true,begin_time=0,end_time=850}},usage=new{duration=1}});
                if(IncompleteTail)Emit("result-generated",new{output=new{sentence=new{sentence_id=2,text="尚未确认的尾句",sentence_end=false,begin_time=900,end_time=(long?)null}},usage=(object?)null});
                Emit("task-finished",new{usage=(object?)null});
            }
        }
        return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token)
    {
        var next=await incoming.Reader.ReadAsync(token);
        if(next.Length>buffer.Count)throw new InvalidOperationException("Test response too large");
        next.CopyTo(buffer.AsSpan());return new(next.Length,WebSocketMessageType.Text,true);
    }
}
