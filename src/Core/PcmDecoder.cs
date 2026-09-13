using System.Buffers.Binary;

namespace RealtimeTranscription.Core;

public enum PcmEncoding { Integer, Float }

/// <summary>Immutable description of one interleaved WASAPI sample frame.</summary>
public sealed record PcmInputFormat(int SampleRate,int Channels,int BitsPerSample,int BlockAlign,PcmEncoding Encoding)
{
    public void Validate()
    {
        if(SampleRate is <8000 or >384000||Channels is <1 or >64)
            throw new NotSupportedException("麦克风采样率或声道数不受支持。");
        bool supported=Encoding switch{PcmEncoding.Integer=>BitsPerSample is 8 or 16 or 24 or 32,PcmEncoding.Float=>BitsPerSample is 32 or 64,_=>false};
        if(!supported||BlockAlign!=Channels*(BitsPerSample/8))
            throw new NotSupportedException("麦克风采样编码或样本对齐格式不受支持。");
    }
    public override string ToString()=>$"{SampleRate} Hz / {Channels} ch / {BitsPerSample} bit {Encoding} / align {BlockAlign}";
}

public sealed class PcmDecoder
{
    public PcmInputFormat Format { get; }
    public PcmDecoder(PcmInputFormat format){format.Validate();Format=format;}
    public float[] Decode(ReadOnlySpan<byte> bytes,out float rms)
    {
        if(bytes.Length%Format.BlockAlign!=0)throw new ArgumentException("音频块没有按样本对齐。");
        var mono=new float[bytes.Length/Format.BlockAlign];double energy=0;
        int stride=Format.BitsPerSample/8;
        for(int i=0;i<mono.Length;i++)
        {
            double sum=0;
            for(int ch=0;ch<Format.Channels;ch++)
            {
                var data=bytes.Slice(i*Format.BlockAlign+ch*stride,stride);
                double sample=Format.Encoding==PcmEncoding.Float
                    ? Format.BitsPerSample==32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data))
                    : Format.BitsPerSample switch
                    {
                        8=>(data[0]-128)/128.0,
                        16=>BinaryPrimitives.ReadInt16LittleEndian(data)/32768.0,
                        24=>(((data[0]|data[1]<<8|data[2]<<16)<<8)>>8)/8388608.0,
                        32=>BinaryPrimitives.ReadInt32LittleEndian(data)/2147483648.0,
                        _=>throw new NotSupportedException()
                    };
                sum+=double.IsFinite(sample)?Math.Clamp(sample,-1,1):0;
            }
            mono[i]=(float)(sum/Format.Channels);energy+=mono[i]*mono[i];
        }
        rms=mono.Length==0?0:(float)Math.Sqrt(energy/mono.Length);
        return mono;
    }
}
