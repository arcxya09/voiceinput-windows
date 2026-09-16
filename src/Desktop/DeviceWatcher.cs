using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using RealtimeTranscription.Infrastructure;
namespace RealtimeTranscription.Desktop;
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator enumerator=new();
    private readonly Action<string,bool> changed;
    private readonly RuntimeLog? log;
    public DeviceWatcher(Action<string,bool> changed,RuntimeLog? log=null){this.changed=changed;this.log=log;enumerator.RegisterEndpointNotificationCallback(this);AudioLog.Write(log,"DeviceWatcherRegistered");}
    public void OnDeviceStateChanged(string id,DeviceState state){AudioLog.Write(log,"DeviceStateChanged",null,null,("endpoint",AudioLog.DeviceKey(id)),("state",state));if(state!=DeviceState.Active)changed(id,false);}
    public void OnDeviceAdded(string id)=>AudioLog.Write(log,"DeviceAdded",null,null,("endpoint",AudioLog.DeviceKey(id)));
    public void OnDeviceRemoved(string id){AudioLog.Write(log,"DeviceRemoved",null,null,("endpoint",AudioLog.DeviceKey(id)));changed(id,false);}
    public void OnDefaultDeviceChanged(DataFlow flow,Role role,string id){AudioLog.Write(log,"DefaultDeviceChanged",null,null,("endpoint",AudioLog.DeviceKey(id)),("flow",flow),("role",role));if(flow==DataFlow.Capture&&role==Role.Communications)changed(id,true);}
    public void OnPropertyValueChanged(string id,PropertyKey key){}
    public void Dispose(){enumerator.UnregisterEndpointNotificationCallback(this);enumerator.Dispose();}
}
