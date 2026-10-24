using System;
using System.IO;
using Sim.Headless.Render;

namespace Sim.Headless.Tests.Render;

/// <summary>Fact, пропускаемый, если ffmpeg недоступен.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!FfmpegSink.IsAvailable())
        {
            Skip = "ffmpeg is not available";
        }
    }
}

public class FfmpegTests
{
    [FfmpegFact]
    public void Render_ToMp4_ProducesMp4File()
    {
        string path = Path.Combine(Path.GetTempPath(), "tc-render-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var e = new StringWriter();
            int code = RenderCommand.Run(
                new[] { "render", "--scenario", "demo-grid", "--seconds", "1", "--width", "128", "--height", "128", "--out", path },
                new StringWriter(),
                e);
            Assert.True(code == 0, e.ToString());
            byte[] head = new byte[12];
            using (FileStream f = File.OpenRead(path))
            {
                Assert.True(f.Length > 1000);
                Assert.Equal(12, f.Read(head, 0, 12));
            }

            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
