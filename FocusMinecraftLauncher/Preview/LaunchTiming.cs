using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

internal sealed record LaunchPhaseTiming(string Name, TimeSpan Elapsed);
internal sealed record LaunchTimingSample(DateTimeOffset StartedAt, TimeSpan Elapsed, IReadOnlyList<LaunchPhaseTiming> Phases);

internal sealed class LaunchTiming
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<LaunchPhaseTiming> _phases = [];
    private string _phase = "账号验证";
    private TimeSpan _phaseStarted;

    public void Enter(string phase)
    {
        var elapsed = _clock.Elapsed;
        _phases.Add(new(_phase, elapsed - _phaseStarted));
        _phase = phase;
        _phaseStarted = elapsed;
    }

    public LaunchTimingSample Finish()
    {
        _clock.Stop();
        _phases.Add(new(_phase, _clock.Elapsed - _phaseStarted));
        return new(_startedAt, _clock.Elapsed, _phases.ToArray());
    }
}

public sealed partial class PreviewSession
{
    private const string LaunchTimingKey = "launchTimings";
    private const int LaunchTimingLimit = 5;
    internal event Action? LaunchTimingsChanged;

    internal IReadOnlyList<LaunchTimingSample> GetLaunchTimings(PreviewInstance instance)
    {
        if (_configuration.ReadLauncher()[LaunchTimingKey] is not JsonObject groups
            || groups[InstanceStore.Key(instance.Root, instance.Id)] is not JsonArray samples)
            return [];

        try
        {
            return samples.Deserialize<LaunchTimingSample[]>()?
                .Where(sample => sample?.Phases != null).Take(LaunchTimingLimit).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private void SaveLaunchTiming(PreviewInstance instance, LaunchTimingSample sample)
    {
        var key = InstanceStore.Key(instance.Root, instance.Id);
        _configuration.ChangeLauncher(document =>
        {
            var groups = LauncherConfiguration.Object(document, LaunchTimingKey);
            if (groups[key] is not JsonArray samples) groups[key] = samples = new();
            samples.Insert(0, JsonSerializer.SerializeToNode(sample));
            while (samples.Count > LaunchTimingLimit) samples.RemoveAt(samples.Count - 1);
        });
        LaunchTimingsChanged?.Invoke();
    }
}
