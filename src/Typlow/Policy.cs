using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Typlow;

public enum InputMode { Unknown, English, Korean }
public record InputState(nint Layout, int Conversion, InputMode Mode);
public record AppRule(string Path, string Label, InputMode Mode);
public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool AddressEnglish { get; set; } = true;
    public bool ShowIndicator { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int IndicatorMilliseconds { get; set; } = 700;
    public List<AppRule> Rules { get; set; } = new();
    public Settings Copy() => new() { Enabled = Enabled, AddressEnglish = AddressEnglish,
        ShowIndicator = ShowIndicator, StartWithWindows = StartWithWindows, IndicatorMilliseconds = IndicatorMilliseconds, Rules = Rules.ToList() };
}

public sealed class SettingsStore
{
    public string FilePath { get; }
    public string? LoadWarning { get; private set; }
    private readonly string legacyPath;
    public SettingsStore(string? folder = null, string? legacyPath = null)
    {
        bool portable = folder == null;
        folder ??= System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        FilePath = System.IO.Path.Combine(folder, "Typlow.ini");
        this.legacyPath = legacyPath ?? (portable ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typlow", "settings.json") : System.IO.Path.Combine(folder, "settings.json"));
    }
    public Settings Load()
    {
        try
        {
            LoadWarning = null;
            bool migrate = !File.Exists(FilePath) && File.Exists(legacyPath);
            if (!File.Exists(FilePath) && !migrate) return new();
            var value = migrate ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(legacyPath)) ?? new() : Parse(File.ReadAllLines(FilePath));
            value.Rules ??= new();
            value.Rules = value.Rules.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path) &&
                (r.Mode == InputMode.English || r.Mode == InputMode.Korean)).ToList();
            value.IndicatorMilliseconds = Math.Clamp(value.IndicatorMilliseconds, 300, 2000);
            if (migrate)
                try { Save(value); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { LoadWarning = "기존 설정을 불러왔지만 EXE 폴더에 INI를 만들지 못했습니다. 폴더의 쓰기 권한을 확인해 주세요."; }
            return value;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException)
        { LoadWarning = "설정 파일을 읽지 못해 기본 설정으로 시작했습니다."; return new(); }
    }
    public void Save(Settings settings)
    {
        string content = Serialize(settings);
        if (File.Exists(FilePath) && File.ReadAllText(FilePath) == content) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        File.Move(temp, FilePath, true);
    }
    private static string Quote(string text) => JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static string Serialize(Settings value)
    {
        var lines = new List<string> { "; Typlow portable settings — UTF-8", "[Settings]", "Enabled=" + value.Enabled,
            "AddressEnglish=" + value.AddressEnglish, "ShowIndicator=" + value.ShowIndicator,
            "StartWithWindows=" + value.StartWithWindows, "IndicatorMilliseconds=" + value.IndicatorMilliseconds };
        for (int i = 0; i < value.Rules.Count; i++)
        {
            var rule = value.Rules[i];
            lines.Add(""); lines.Add("[App" + i + "]"); lines.Add("Path=" + Quote(rule.Path));
            lines.Add("Label=" + Quote(rule.Label)); lines.Add("Mode=" + rule.Mode);
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
    private static Settings Parse(string[] lines)
    {
        var value = new Settings(); string section = "";
        var apps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in lines)
        {
            string line = source.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int split = line.IndexOf('=');
            if (split <= 0 || section.Length == 0) throw new FormatException("Invalid INI entry.");
            string key = line[..split].Trim(), data = line[(split + 1)..].Trim();
            if (section.Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                switch (key.ToLowerInvariant())
                {
                    case "enabled": value.Enabled = bool.Parse(data); break;
                    case "addressenglish": value.AddressEnglish = bool.Parse(data); break;
                    case "showindicator": value.ShowIndicator = bool.Parse(data); break;
                    case "startwithwindows": value.StartWithWindows = bool.Parse(data); break;
                    case "indicatormilliseconds": value.IndicatorMilliseconds = int.Parse(data); break;
                }
            }
            else if (section.StartsWith("App", StringComparison.OrdinalIgnoreCase))
            {
                if (!apps.TryGetValue(section, out var app)) apps[section] = app = new(StringComparer.OrdinalIgnoreCase);
                app[key] = data;
            }
        }
        string Decode(string data) => data.StartsWith('"') ? JsonSerializer.Deserialize<string>(data) ?? "" : data;
        foreach (var app in apps.Values)
            if (app.TryGetValue("Path", out var path) && app.TryGetValue("Mode", out var mode) && Enum.TryParse<InputMode>(mode, true, out var parsed))
                value.Rules.Add(new(Decode(path), app.TryGetValue("Label", out var label) ? Decode(label) : System.IO.Path.GetFileNameWithoutExtension(Decode(path)), parsed));
        return value;
    }
}

public record Observation(nint Window, string Path, bool Address, InputState State);
public record SwitchRequest(InputMode Mode, InputState? Restore, string Reason);

// One display opportunity per focused input. Polling the same input never replenishes it.
public sealed class IndicatorVisit
{
    private nint activeWindow;
    private string activePath = "", activeFocus = "";
    private bool pending;
    public bool Pending => pending;
    public void Enter(nint window, string path, string focus, bool isInput)
    {
        bool changed = activeWindow != window || activeFocus != focus ||
            !string.Equals(activePath, path, StringComparison.OrdinalIgnoreCase);
        activeWindow = window; activePath = path; activeFocus = focus;
        if (window == 0 || !isInput || focus.Length == 0) pending = false;
        else if (changed) pending = true;
    }
    public bool TryTake(Settings settings, string path, bool isInput, InputMode mode, bool caretAvailable)
    {
        if (!settings.Enabled || !settings.ShowIndicator || !settings.Rules.Exists(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
        { pending = false; return false; }
        if (!pending || !isInput || mode == InputMode.Unknown || !caretAvailable) return false;
        pending = false; return true;
    }
}

// Only focus transitions produce commands. Repeated observations are read-only.
public sealed class FocusPolicy
{
    public static bool ShouldShowIndicator(Settings settings, string path, bool isInput, bool focusEntered, InputMode mode)
        => settings.Enabled && settings.ShowIndicator && isInput && focusEntered && mode != InputMode.Unknown &&
           settings.Rules.Exists(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
    private nint active;
    private readonly Dictionary<nint, InputState> content = new();
    private readonly Dictionary<nint, InputState> addressSessions = new();
    public void Reset() { active = 0; content.Clear(); addressSessions.Clear(); }
    public void Forget(nint window) { content.Remove(window); addressSessions.Remove(window); }
    public SwitchRequest? Observe(Observation observation, Settings settings)
    {
        bool enteredWindow = active != observation.Window;
        active = observation.Window;
        if (!settings.Enabled || observation.Window == 0) return null;
        if (settings.AddressEnglish && observation.Address)
        {
            if (!addressSessions.ContainsKey(observation.Window))
            {
                InputState before = content.GetValueOrDefault(observation.Window) ?? observation.State;
                addressSessions[observation.Window] = before;
                return new(InputMode.English, null, "주소창 진입 · 영어");
            }
            // Returning to an address bar is another focus entry, but must not replace its snapshot.
            return enteredWindow ? new(InputMode.English, null, "주소창 복귀 · 영어") : null;
        }
        if (addressSessions.Remove(observation.Window, out var saved))
        {
            content[observation.Window] = saved;
            return saved.Mode == InputMode.Unknown ? null : new(saved.Mode, saved, "주소창 이탈 · 이전 상태 복원");
        }
        if (observation.State.Mode != InputMode.Unknown) content[observation.Window] = observation.State;
        if (!enteredWindow) return null;
        AppRule? rule = settings.Rules.FirstOrDefault(r => string.Equals(r.Path, observation.Path, StringComparison.OrdinalIgnoreCase));
        return rule is null ? null : new(rule.Mode, null, "앱 포커스 · " + rule.Label);
    }
    public InputState? TakeActiveRestore() => addressSessions.Remove(active, out var saved) && saved.Mode != InputMode.Unknown ? saved : null;
    public void Confirm(nint window, InputState state, bool address)
    { if (!address && state.Mode != InputMode.Unknown) content[window] = state; }
}
