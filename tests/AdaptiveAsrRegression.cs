using System.Buffers.Binary;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

internal static class AdaptiveAsrRegression
{
    private static void Check(bool ok,string reason) { if(!ok)throw new Exception(reason); }
    internal static byte[] Frame(double db)
    {
        var pcm=new byte[640];double amplitude=Math.Pow(10,db/20)*Math.Sqrt(2);
        for(int i=0;i<320;i++)BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i*2,2),(short)Math.Round(Math.Clamp(amplitude*Math.Sin(i*2*Math.PI/40),-1, .9999)*32767));
        return pcm;
    }
    internal static byte[] Recording(double noise,double active) => Enumerable.Range(0,50).SelectMany(i=>Frame(i<20?noise:active)).ToArray();
    private static AudioEnvironment Analyze(byte[] pcm)
    {var a=new AudioEnvironmentAnalyzer();a.Add(pcm);return a.Snapshot;}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("自适应 ASR：轻声、清晰语音和有噪声语音选择小幅阈值",()=>
        {
            foreach(var (noise,active,kind,threshold,multi) in new[]{(-75d,-48d,"QuietSpeech",(double?)-.1,false),(-60d,-20d,"Clean",(double?)null,false),(-35d,-18d,"Noisy",(double?).1,true)})
            {
                var pcm=Recording(noise,active);var original=pcm.ToArray();var value=Analyze(pcm);
                var tuning=new AudioEnvironmentMemory().Select(true,"mic",value,1000);
                Check(value.Kind==kind,$"{kind}: {value}");
                Check(tuning.SpeechNoiseThreshold==threshold && tuning.MultiThreshold==multi,"错误的灵敏度方向或断句策略");
                Check(pcm.SequenceEqual(original),"分析改写了音频");
                Check(Math.Abs(value.NoiseDb-noise)<1 && Math.Abs(value.ActiveDb-active)<1,"dBFS 估计偏差");
            }
            return Task.CompletedTask;
        });
        await test("自适应 ASR：静音、持续噪声、点击、DC 和削波不强行调整",()=>
        {
            var click=Enumerable.Range(0,50).SelectMany(i=>Frame(i is 24 or 25?-10:-60)).ToArray();
            var dc=new byte[32000];for(int i=0;i<dc.Length;i+=2)BinaryPrimitives.WriteInt16LittleEndian(dc.AsSpan(i,2),12000);
            var clipped=new byte[32000];for(int i=0;i<clipped.Length;i+=2)BinaryPrimitives.WriteInt16LittleEndian(clipped.AsSpan(i,2),i%4==0?short.MaxValue:short.MinValue);
            foreach(var pcm in new[]{new byte[32000],Recording(-25,-25),click,dc,clipped,Frame(-15)})
            {var value=Analyze(pcm);Check(!value.Usable,$"误判语音: {value}");Check(new AudioEnvironmentMemory().Select(true,"mic",value,0).SpeechNoiseThreshold==null,"不确定音频发送了阈值");}
            return Task.CompletedTask;
        });
        await test("自适应 ASR：不同包长统计一致，滑动窗口有界",()=>
        {
            var pcm=Recording(-35,-18);var a=new AudioEnvironmentAnalyzer();
            for(int i=0;i<pcm.Length;i+=14)a.Add(pcm.AsSpan(i,Math.Min(14,pcm.Length-i)));
            Check(a.Snapshot==Analyze(pcm),"设备包长改变估计");
            for(int i=0;i<450;i++)a.Add(Frame(-25));
            Check(a.Snapshot.Frames==400 && a.Snapshot.Kind=="Uncertain","旧语音或无限增长窗口污染统计");
            return Task.CompletedTask;
        });
        await test("自适应 ASR：近期统计按设备和有效期隔离，当前环境优先",()=>
        {
            var memory=new AudioEnvironmentMemory();var quiet=Analyze(Recording(-75,-48));var noisy=Analyze(Recording(-35,-18));
            memory.Remember("A",quiet,1000);
            Check(memory.Select(true,"A",AudioEnvironment.Empty,1100).Source=="Recent","未复用同一设备近期统计");
            foreach(var (device,now) in new[]{("B",1100L),("A",121001L),("A",999L),("",1100L)})
                Check(memory.Select(true,device,AudioEnvironment.Empty,now).SpeechNoiseThreshold==null,"复用了不适用的统计");
            Check(memory.Select(true,"A",noisy,1100).SpeechNoiseThreshold==.1,"当前环境未覆盖旧统计");
            Check(memory.Select(true,"A",Analyze(new byte[32000]),1100).SpeechNoiseThreshold==null,"当前静音仍套用旧统计");
            Check(memory.Select(false,"A",noisy,1100).Source=="Disabled","开关失效");
            memory.Remember("A",AudioEnvironment.Empty,1200);
            Check(memory.Select(true,"A",AudioEnvironment.Empty,1300).Source=="Default","不可靠轮次未清除旧统计");
            return Task.CompletedTask;
        });
        await test("自适应 ASR：请求只带官方支持的启动参数，保留用户断句停顿",()=>
        {
            var settings=new AppSettings{SilenceMs=3100};
            using var doc=JsonDocument.Parse(BailianProtocol.Start("test",settings,[],new(.1,true,"Current","Noisy")));
            var p=doc.RootElement.GetProperty("payload").GetProperty("parameters");
            Check(p.GetProperty("speech_noise_threshold").GetDouble()==.1 && p.GetProperty("multi_threshold_mode_enabled").GetBoolean(),"启动参数遗漏");
            Check(!p.GetProperty("semantic_punctuation_enabled").GetBoolean() && p.GetProperty("max_sentence_silence").GetInt32()==3100,"修改用户断句设置");
            using var disabled=JsonDocument.Parse(BailianProtocol.Start("test",settings with{AdaptiveAsrEnabled=false},[],new(.1,true)));
            var original=disabled.RootElement.GetProperty("payload").GetProperty("parameters");
            Check(!original.TryGetProperty("speech_noise_threshold",out _) && !original.GetProperty("multi_threshold_mode_enabled").GetBoolean(),"关闭后仍发送自适应参数");
            foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1.1,1.1})
            {bool rejected=false;try{BailianProtocol.Start("test",settings,[],new(invalid));}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"无效阈值被发送");}
            return Task.CompletedTask;
        });
    }
}
