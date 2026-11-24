using System.Text.Json;
namespace Kot.Windows;

// Same generated trajectories as loading-ui.js. Interpolate elapsed time, not frame count.
internal static class LoadingMotion
{
    internal sealed class Clip
    {
        public double Duration { get; set; }
        public string Group { get; set; } = "calm";
        public double[][] Frames { get; set; } = [];
    }
    sealed class Catalog
    {
        public int Fps { get; set; }
        public Dictionary<string,double> Weights { get; set; } = [];
        public Dictionary<string, Clip> Motions { get; set; } = [];
    }
    static readonly Catalog Data = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "ui", "loading-motion.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    internal static string[] Sequence()
    {
        var result = new List<string> { "quiet" }; var used = new HashSet<string>();
        for (int i = 0; i < 63; i++)
        {
            double r = Random.Shared.NextDouble();
            string group = "calm";
            foreach (var weight in Data.Weights) { group=weight.Key; if(r<weight.Value)break; r-=weight.Value; }
            var pool = Data.Motions.Where(p => p.Value.Group == group && !used.Contains(p.Key)).Select(p => p.Key).ToArray();
            if (pool.Length == 0) pool = ["quiet", "breathe", "peek"];
            string id = pool[Random.Shared.Next(pool.Length)]; result.Add(id);
            if (Data.Motions[id].Group is "joke" or "rare") used.Add(id);
        }
        return result.ToArray();
    }
    internal static double[] Sample(string[] sequence, double elapsed)
    {
        foreach (string id in sequence)
        {
            if (elapsed < Data.Motions[id].Duration) return Pose(id, elapsed);
            elapsed -= Data.Motions[id].Duration;
        }
        return Pose("breathe", elapsed % Data.Motions["breathe"].Duration);
    }
    internal static double[] Pose(string id, double elapsed)
    {
        var frames = Data.Motions[id].Frames;
        double p = Math.Clamp(elapsed * Data.Fps / 1000, 0, frames.Length - 1);
        int i = (int)p; var a = frames[i]; var b = frames[Math.Min(i + 1, frames.Length - 1)];
        return Enumerable.Range(0, 4).Select(j => a[j] + (b[j] - a[j]) * (p - i)).ToArray();
    }
}
