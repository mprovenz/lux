namespace Lux.Cli;

public static class ModInfo
{
    /// <summary>mod-info &lt;lri&gt;: module name → CameraID, gain, exposure and every vignetting record's relative_brightness.</summary>
    public static int Run(List<string> a)
    {
        var lri = Lux.Engine.Lri.LriFile.Load(a[0]);
        foreach (var kv in lri.Modules)
        {
            var m = kv.Value.Module; var rbs = new List<string>();
            foreach (var c in Lux.Engine.Imaging.Calibration.ForModule(lri.Header, m.Id)) rbs.Add(c.Vignetting is null ? "-" : (c.Vignetting.HasRelativeBrightness ? c.Vignetting.RelativeBrightness.ToString("R") : "unset") + (c.Geometry is null ? "" : "+geo") + (c.Color.Count > 0 ? "+col" : ""));
            float fb = float.NaN; try { fb = Lux.Engine.Pipeline.Isp.CapturedFrame.Load(lri, kv.Key).Info.FrameBlack; } catch (Exception) { }
            Console.WriteLine($"{kv.Key}: id {m.Id} ({(int)m.Id}) gain {m.SensorAnalogGain:R} exposure {m.SensorExposure} frameBlack {fb:R} vignetting rb per calib entry [{string.Join(", ", rbs)}]");
        }
        return 0;
    }
}
