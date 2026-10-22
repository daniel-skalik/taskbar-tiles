using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>One entry of settings.hooks. Every is set for "interval" only; Timeout overrides ScriptSettings.DefaultTimeout.</summary>
public sealed record HookSpec(string Run, TimeSpan? Every = null, TimeSpan? Timeout = null);

/// <summary>The script connector's settings: { "hooks": { "interval": { "every": "30s", "run": "scripts/x.ps1" }, ... } }.</summary>
public sealed class ScriptSettings
{
    /// <summary>Longest a hook script may run unless its "timeout" says otherwise. Click, action and popup hooks are also bounded by the host's 10 s.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    const string ActionPrefix = "action:";
    static readonly HashSet<string> PlainHooks = new(StringComparer.Ordinal) { "interval", "watch", "click", "flyout", "startup", "resume" };

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
            errors.Add("settings.hooks must be an object, e.g. { \"interval\": { \"every\": \"30s\", \"run\": \"scripts/status.ps1\" } }.");
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
            if (ReadHook(name, hook.Value, errors) is { } spec) hooks[name] = spec;
        }

        if (hooks.Count == 0 && errors.Count == 0) errors.Add("settings.hooks is empty; add at least one hook.");
        return new(hooks, errors);
    }

    static HookSpec? ReadHook(string name, JsonElement el, List<string> errors)
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

        if (!el.TryGetProperty("run", out var run) || run.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(run.GetString()))
            return MissingRun(name, errors);

        TimeSpan? every = null;
        if (name == "interval")
        {
            if (!el.TryGetProperty("every", out var e) || !TryPositiveDuration(e, out var period))
            {
                errors.Add("Hook \"interval\" needs \"every\" as a duration, e.g. \"30s\".");
                return null;
            }
            every = period;
        }

        TimeSpan? timeout = null;
        if (el.TryGetProperty("timeout", out var t))
        {
            if (!TryPositiveDuration(t, out var limit))
            {
                errors.Add($"Hook \"{name}\" has an invalid \"timeout\"; use e.g. \"20s\".");
                return null;
            }
            timeout = limit;
        }

        return new HookSpec(run.GetString()!.Trim(), every, timeout);
    }

    static bool TryPositiveDuration(JsonElement el, out TimeSpan value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.String && Duration.TryParse(el.GetString(), out value) && value > TimeSpan.Zero;
    }

    static HookSpec? MissingRun(string name, List<string> errors)
    {
        errors.Add($"Hook \"{name}\" needs a \"run\" script path or command line.");
        return null;
    }
}
