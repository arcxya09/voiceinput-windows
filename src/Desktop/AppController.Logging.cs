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
}
