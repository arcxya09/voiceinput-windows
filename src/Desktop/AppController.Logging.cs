namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    // Correlation uses locally generated turn IDs only. Never log transcript,
    // provider messages, configuration, endpoint IDs, or diagnostic display text.
    internal static string? LogCorrelation(string? id)=>Guid.TryParse(id,out var parsed)?parsed.ToString():null;
    private void LogEvent(string eventName,Exception? error=null,string? turnId=null,params (string Key,object? Value)[] fields)
        =>Log.Write("Controller",eventName,LogCorrelation(turnId??captureAttempt?.Id),
            fields.ToDictionary(x=>x.Key,x=>x.Value),error);

    private void LogDiagnostic(string stage,Exception? error)
        =>LogEvent("Diagnostic",error,fields:[("Stage",stage),("State",state.ToString()),
            ("AudioAvailable",audio!=null),("OutputSampleRate",16000),("OutputChannels",1),("OutputBitsPerSample",16)]);

    internal static string LogDeliveryState(string? state)=>state switch
    {
        "Pending" or "Copying" or "Sending" or "Copied" or "Sent" or "Blocked" or "Unknown" or
        "Cancelled" or "Failed" or "StartFailed" or "Empty" or "NotRequested" or "PasteSent" or "CopyFailed" or "Partial" or "Dictated"=>state,
        _=>"Other"
    };
    internal Task LogTurnSummaryAsync(string turnId,string? delivery,int characters,bool manual,bool cancelled,
        long copyMs,long pasteMs,long releasedAt,int ordinaryInputs)=>OnActor(()=>
    {
        if(captureAttempt is not {} attempt||attempt.Id!=turnId)return;
        static long? Span(long begin,long end)=>begin>0&&end>=begin?end-begin:null;
        LogEvent("TurnSummary",turnId:turnId,fields:[
            ("State",LogDeliveryState(delivery)),("Cancelled",cancelled),("ManualDelivery",manual),
            ("CharacterCount",characters),("AudioDurationMs",attempt.SamplesSent/16.0),
            ("SamplesSent",attempt.SamplesSent),("MetadataAnomalies",attempt.MetadataAnomalies),("ReportedGapFrames",attempt.ReportedGapFrames),
            ("FirstAudioMs",Span(attempt.PressedAt,attempt.FirstAudioAt)),("RecognitionReadyMs",Span(attempt.PressedAt,attempt.AsrReadyAt)),
            ("LocalStopMs",Span(attempt.StopRequestedAt,attempt.AudioStoppedAt)),
            ("RecognitionStopMs",Span(attempt.StopRequestedAt,attempt.AsrStoppedAt)),
            ("PolishMs",Span(attempt.PolishStartedAt,attempt.PolishFinishedAt)),("PolishOutcome",attempt.PolishOutcome),
            ("CopyMs",copyMs<0?(long?)null:copyMs),("PasteMs",pasteMs<0?(long?)null:pasteMs),
            ("ReleaseToDeliveryMs",Span(releasedAt,Environment.TickCount64)),
            ("TotalMs",Math.Max(0,Environment.TickCount64-attempt.PressedAt)),("OrdinaryInputCount",ordinaryInputs)]);
    });
}
