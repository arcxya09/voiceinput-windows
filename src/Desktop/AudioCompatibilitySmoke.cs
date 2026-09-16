using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

internal static class AudioCompatibilitySmoke
{
    private static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    internal static async Task RunAsync(RuntimeLog log)
    {
        foreach(string scenario in new[]{"repeat","flag","timestamp-error","backwards","future","gap","tail-stall","device-failure","stop-failure","start-failure","long-stream"})
        {
            var device=new Packets(scenario);
            var completion=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int frames=0,callbacks=0;
            NativeWasapiCapture capture=null!;
            capture=new NativeWasapiCapture(device,(pointer,count,silent)=>
            {
                Require(pointer!=IntPtr.Zero&&count==448,"Native fixture PCM was discarded or incorrectly clipped: "+scenario);
                Require(Marshal.ReadByte(pointer)==42&&!silent,"Native fixture PCM changed");
                frames+=count;
                if(++callbacks==(scenario=="long-stream"?300:2))capture.RequestStop(); // Includes a stop during a packet lease.
            },e=>completion.TrySetResult(e),log);
            await using(capture)
            {
                Exception? startupError=null;
                try{capture.Start();}catch(Exception e){startupError=e;}
                var error=await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(device.OwnerError==null,device.OwnerError??"");
                Require(device.Disposed&&!device.Leased,"Capture thread did not release native ownership: "+scenario);
                if(scenario=="start-failure")
                {
                    Require(ReferenceEquals(startupError,device.Failure)&&error==null&&frames==0,"Startup HRESULT was replaced or notified twice");
                    continue;
                }
                Require(startupError==null,"Unexpected native startup failure: "+scenario);
                if(scenario is "device-failure" or "stop-failure")
                {
                    Require(ReferenceEquals(error,device.Failure)&&error!.HResult==unchecked((int)0x88890004),"Real device error was swallowed/replaced");
                    continue;
                }
                Require(error==null,"Recoverable metadata stopped native capture: "+scenario);
                int expectedPackets=scenario=="long-stream"?302:4;
                Require(frames==expectedPackets*448&&device.Reads==expectedPackets&&device.Releases==expectedPackets&&device.Stops==1,
                    "Stop-and-drain lost/duplicated packets or stopped twice: "+scenario);
                Require(capture.ReportedGapFrames==(scenario=="gap"?448:0),"Incorrect gap accounting: "+scenario);
            }
        }
    }

    private sealed class Packets(string scenario) : INativeCaptureClient
    {
        private readonly IntPtr pcm=Marshal.AllocHGlobal(448*8);
        private long clock;
        private int owner;
        public int Reads,Releases,Stops;
        public bool Leased,Disposed;
        public string? OwnerError;
        public readonly Exception Failure=new COMException("Fixture device unavailable",unchecked((int)0x88890004));
        public WaveFormat MixFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100,2);
        public int BufferSize => 448*4;
        public long DefaultDevicePeriod => 100_000;
        public void Initialize(WaveFormat format)
        {
            owner=Environment.CurrentManagedThreadId;
            clock=(long)(Stopwatch.GetTimestamp()*(double)AudioPacketTimeline.TicksPerSecond/Stopwatch.Frequency)-1_000_000;
            Marshal.Copy(Enumerable.Repeat((byte)42,448*8).ToArray(),0,pcm,448*8);
        }
        private void Owned()
        {
            if(owner!=Environment.CurrentManagedThreadId)OwnerError="Native service escaped its capture thread";
        }
        public void SetEventHandle(IntPtr handle)=>Owned();
        public void GetCaptureService()=>Owned();
        public int GetNextPacketSize()
        {
            Owned();
            if(scenario=="device-failure"&&Reads==1)throw Failure;
            int livePackets=scenario=="long-stream"?300:2;
            return Reads<(Stops==0?livePackets:livePackets+2)?448:0;
        }
        public IntPtr GetBuffer(out int frames,out AudioClientBufferFlags flags,out long position,out long timestamp)
        {
            Owned();Require(!Leased,"Overlapping native packet leases");Leased=true;
            int index=Reads++;frames=448;flags=index==0?AudioClientBufferFlags.DataDiscontinuity:0;
            position=index*448;timestamp=clock+index*102_000;
            if(index==1)
            {
                if(scenario=="repeat")position=0;
                if(scenario=="flag"||scenario=="stop-failure")flags|=AudioClientBufferFlags.DataDiscontinuity;
                if(scenario=="timestamp-error")flags|=AudioClientBufferFlags.TimestampError;
                if(scenario=="backwards")timestamp=clock-1;
                if(scenario=="future")timestamp=clock+100_000_000;
            }
            if(scenario=="gap"&&index>=1)position+=448;
            return pcm;
        }
        public void ReleaseBuffer(int frames){Owned();Require(Leased,"Unowned ReleaseBuffer");Leased=false;Releases++;}
        public void Start(){Owned();if(scenario=="start-failure")throw Failure;}
        public void Stop()
        {
            Owned();Require(!Leased,"Stop was called before ReleaseBuffer");Stops++;
            if(scenario=="stop-failure")throw Failure;
        }
        public void Dispose(){Owned();Disposed=true;Marshal.FreeHGlobal(pcm);}
    }
}
