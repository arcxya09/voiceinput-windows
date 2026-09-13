using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace RealtimeTranscription.Desktop;
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator enumerator=new();
    private readonly Action<string,bool> changed;
    public DeviceWatcher(Action<string,bool> changed){this.changed=changed;enumerator.RegisterEndpointNotificationCallback(this);}
    public void OnDeviceStateChanged(string id,DeviceState state){if(state!=DeviceState.Active)changed(id,false);}
    public void OnDeviceAdded(string id){}
    public void OnDeviceRemoved(string id)=>changed(id,false);
    public void OnDefaultDeviceChanged(DataFlow flow,Role role,string id){if(flow==DataFlow.Capture&&role==Role.Communications)changed(id,true);}
    public void OnPropertyValueChanged(string id,PropertyKey key){}
    public void Dispose(){enumerator.UnregisterEndpointNotificationCallback(this);enumerator.Dispose();}
}
