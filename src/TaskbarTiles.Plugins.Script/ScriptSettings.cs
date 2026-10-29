using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>One entry of settings.hooks. Every is set for "interval" only; Timeout overrides ScriptSettings.DefaultTimeout.</summary>
public sealed record HookSpec(string Run, TimeSpan? Every = null, TimeSpan? Timeout = null);

/// <summary>
/// The script connector's settings, from tile.json: { "hooks": { "interval": { "every": "30s", "run": "x.ps1" }, ... } }.
/// "every" may also be "$name": the duration in the top-level setting of that name, so a Settings… field can edit it.
/// </summary>
public sealed class ScriptSettings
{
    /// <summary>Longest a hook script may run unless its "timeout" says otherwise. Click, action and popup hooks are also bounded by the host's 10 s.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bounds for "every" and "timeout": long enough to be sane, short enough for Task.Delay and CancelAfter.</summary>
    public static readonly TimeSpan MinEvery = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    const string ActionPrefix = "action:";
    const string HostPrefix = "host:";
    static readonly HashSet<string> PlainHooks = new(StringComparer.Ordinal) { "interval", "watch", "click", "flyout", "startup", "resume" };

    /// <summary>The settings a hook object may have, in the order error messages list them.</summary>
    static readonly string[] IntervalKeys = ["run", "every", "timeout"];
    static readonly string[] HookKeys = ["run", "timeout"];

    ScriptSettings(IReadOnlyDictionary<string, HookSpec> hooks, IReadOnlyList<string> errors)
    {
        Hooks = hooks;
        Errors = errors;
    }

    public IReadOnlyDictionary<string, HookSpec> Hooks { get; }

    /// <summary>Problems found while parsing. The connector refuses to run anything while there are any.</summary>
    public IReadOnlyList<string> Errors { get; }

    public HookSpec? Hook(string name) => Hooks.GetValueOrDefault(name);

    public HookSpec? Action(string actionId) => Hooks.GetValueOrDefault(ActionPrefix + actionId);

    public static ScriptSettings Parse(JsonElement settings)
    {
        var hooks = new Dictionary<string, HookSpec>(StringComparer.Ordinal);
        var errors = new List<string>();

        if (settings.ValueKind != JsonValueKind.Object
            || !settings.TryGetProperty("hooks", out var hooksEl)
            || hooksEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add("settings.hooks must be an object, e.g. { \"interval\": { \"every\": \"30s\", \"run\": \"status.ps1\" } }.");
            return new(hooks, errors);
        }

        foreach (var hook in hooksEl.EnumerateObject())
        {
            var name = hook.Name;
            var isAction = name.StartsWith(ActionPrefix, StringComparison.Ordinal);
            if (!isAction && !PlainHooks.Contains(name))
            {
                errors.Add($"Unknown hook \"{name}\". Use interval, watch, click, flyout, startup, resume or action:<id>.");
                continue;
            }
            if (isAction && name.Length == ActionPrefix.Length)
            {
                errors.Add("Hook \"action:\" needs an id, e.g. \"action:rerun\".");
                continue;
            }
            if (isAction && name.StartsWith(ActionPrefix + HostPrefix, StringComparison.Ordinal))
            {
                errors.Add($"Hook \"{name}\" uses a reserved id; ids starting with \"host:\" are reserved for the host.");
                continue;
            }
            if (ReadHook(name, hook.Value, settings, errors) is { } spec) hooks[name] = spec;
        }

        if (hooks.Count == 0 && errors.Count == 0) errors.Add("settings.hooks is empty; add at least one hook.");
        return new(hooks, errors);
    }

    static HookSpec? ReadHook(string name, JsonElement el, JsonElement settings, List<string> errors)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            if (name == "interval")
            {
                errors.Add("Hook \"interval\" needs { \"every\": \"30s\", \"run\": \"...\" }.");
                return null;
            }
            var text = el.GetString();
            return string.IsNullOrWhiteSpace(text) ? MissingRun(name, errors) : new HookSpec(text.Trim());
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Hook \"{name}\" must be an object with \"run\".");
            return null;
        }

        // A typo such as "timout" would otherwise be ignored silently and the default used.
        var allowed = name == "interval" ? IntervalKeys : HookKeys;
        var unknown = el.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !allowed.Contains(n, StringComparer.Ordinal));
        if (unknown is not null)
        {
            errors.Add($"Hook \"{name}\" has an unknown setting \"{unknown}\"; use {string.Join(", ", allowed)}.");
            return null;
        }

        if (!el.TryGetProperty("run", out var run) || run.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(run.GetString()))
            return MissingRun(name, errors);

        TimeSpan? every = null;
        if (name == "interval")
        {
            if (el.TryGetProperty("every", out var named) && named.ValueKind == JsonValueKind.String
                && named.GetString() is ['$', .. var key])
            {
                if (key.Length == 0 || !settings.TryGetProperty(key, out var value) || !TryDuration(value, MinEvery, out var fromSetting))
                {
                    errors.Add($"Hook \"interval\" takes \"every\" from setting \"{key}\", which must be a duration from 1s to 24h, e.g. \"30s\".");
                    return null;
                }
                every = fromSetting;
            }
            else if (!el.TryGetProperty("every", out var e) || !TryDuration(e, MinEvery, out var period))
            {
                errors.Add("Hook \"interval\" needs \"every\" as a duration from 1s to 24h, e.g. \"30s\".");
                return null;
            }
            else every = period;
        }

        TimeSpan? timeout = null;
        if (el.TryGetProperty("timeout", out var t))
        {
            if (!TryDuration(t, TimeSpan.Zero, out var limit))
            {
                errors.Add($"Hook \"{name}\" has an invalid \"timeout\"; use a duration above 0 up to 24h, e.g. \"20s\".");
                return null;
            }
            timeout = limit;
        }

        return new HookSpec(run.GetString()!.Trim(), every, timeout);
    }

    /// <summary>A duration string above (or, for a positive minimum, at least) min and at most 24 h.</summary>
    static bool TryDuration(JsonElement el, TimeSpan min, out TimeSpan value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.String || !Duration.TryParse(el.GetString(), out value)) return false;
        return (min > TimeSpan.Zero ? value >= min : value > min) && value <= MaxDuration;
    }

    static HookSpec? MissingRun(string name, List<string> errors)
    {
        errors.Add($"Hook \"{name}\" needs a \"run\" script path or command line.");
        return null;
    }
}
