using System.Buffers.Binary;

namespace RealtimeTranscription.Core;

/// <summary>Energy estimates in dBFS, not calibrated SPL or a semantic speech detector.</summary>
public sealed record AudioEnvironment(int Frames, double NoiseDb, double ActiveDb, double ContrastDb,
    double ClippedFraction, string Kind)
{
    public static AudioEnvironment Empty { get; } = new(0, -96, -96, 0, 0, "Insufficient");
    public bool Usable => Kind is "QuietSpeech" or "Noisy" or "Clean";
    public int DurationMs => Frames * 20;
    public float ActivityThreshold => (float)Math.Clamp(Math.Pow(10, (NoiseDb + 8) / 20), .0005, .03);
}

public sealed record AsrTuning(double? SpeechNoiseThreshold = null, bool MultiThreshold = false,
    string Source = "Default", string Kind = "Insufficient");

/// <summary>Observes unchanged 16 kHz mono PCM16 in fixed 20 ms windows, capped at the latest 8 seconds.</summary>
public sealed class AudioEnvironmentAnalyzer
{
    private readonly object sync = new();
    private readonly double[] levels = new double[400];
    private readonly int[] clipping = new int[400];
    private int count, next, samples, clipped;
    private double sum, squares;
    private AudioEnvironment snapshot = AudioEnvironment.Empty;
    public AudioEnvironment Snapshot { get { lock(sync) return snapshot; } }

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if(pcm.Length % 2 != 0) throw new ArgumentException("PCM16 must be sample-aligned.", nameof(pcm));
        lock(sync)
        {
            for(int i=0;i<pcm.Length;i+=2)
            {
                double sample=BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i,2))/32768.0;
                sum+=sample;squares+=sample*sample;if(Math.Abs(sample)>=.98)clipped++;
                if(++samples<320)continue;
                // Remove DC offset from the energy estimate only; never alter uploaded PCM.
                double power=Math.Max(0,squares/320-Math.Pow(sum/320,2));
                levels[next]=Math.Max(-96,10*Math.Log10(Math.Max(power,1e-12)));
                clipping[next]=clipped;next=(next+1)%levels.Length;count=Math.Min(count+1,levels.Length);
                samples=clipped=0;sum=squares=0;
            }
            snapshot=Estimate();
        }
    }

    private AudioEnvironment Estimate()
    {
        if(count==0)return AudioEnvironment.Empty;
        var sorted=levels.AsSpan(0,count).ToArray();Array.Sort(sorted);
        double noise=sorted[(count-1)/5],active=sorted[(count-1)*9/10],contrast=active-noise;
        double clip=clipping.Take(count).Sum()/(count*320.0);
        // Require sustained modulation: a keyboard click alone must not select a speech profile.
        int run=0,longest=0;
        int oldest=count==levels.Length?next:0;
        for(int i=0;i<count;i++)
        {
            double db=levels[(oldest+i)%levels.Length];
            run=db>=noise+8 && db>-66?run+1:0;longest=Math.Max(longest,run);
        }
        string kind=count<30?"Insufficient":clip>.01?"Clipped":active<=-66?"Silence":
            contrast<10||longest<6?"Uncertain":active<-35?"QuietSpeech":noise>-42?"Noisy":"Clean";
        return new(count,noise,active,contrast,clip,kind);
    }
}

/// <summary>One recent profile, scoped to the actual endpoint. No audio or device profile is persisted.</summary>
public sealed class AudioEnvironmentMemory
{
    private readonly object sync = new();
    private string endpoint="";
    private long recordedAt;
    private AudioEnvironment? previous;
    public void Remember(string device,AudioEnvironment value,long now)
    { lock(sync) { endpoint=device;recordedAt=now;previous=value.Usable?value:null; } }
    public AsrTuning Select(bool enabled,string device,AudioEnvironment current,long now)
    {
        lock(sync)
        {
        if(!enabled)return new(Source:"Disabled",Kind:current.Kind);
        string source="Current";
        if(current.DurationMs<600 && current.ClippedFraction<=.01 && previous!=null && device.Length>0 && endpoint==device && now>=recordedAt && now-recordedAt<=120000)
        {current=previous;source="Recent";}
        if(!current.Usable)return new(Kind:current.Kind);
        return current.Kind switch
        {
            "QuietSpeech"=>new(-.1,false,source,current.Kind),
            "Noisy"=>new(.1,true,source,current.Kind),
            _=>new(null,false,source,current.Kind)
        };
        }
    }
}
