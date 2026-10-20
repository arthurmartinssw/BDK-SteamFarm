using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BdkHourFarm;

/// <summary>Uma janela em que o farm pode rodar. Days: 1 = segunda ... 7 = domingo. Start/End: "HH:mm" (End menor que Start = vira a meia-noite).</summary>
internal sealed record ScheduleWindow(List<int>? Days, string? Start, string? End);

internal sealed record ScheduleConfig(bool Enabled, string? TimeZone, List<ScheduleWindow>? Windows);

internal sealed record ScheduleView(bool Enabled, string TimeZone, List<ScheduleWindow> Windows, bool Active, long? NextChange);

/// <summary>Agenda de horários do farm (schedule.json). Sem janelas ou desligada = farma sempre.</summary>
internal sealed partial class ScheduleStore
{
    public const int MaxWindows = 14;
    public const string DefaultZone = "America/Sao_Paulo";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private ScheduleConfig _config = new(false, DefaultZone, new List<ScheduleWindow>());
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;

    public ScheduleStore(string path)
    {
        _path = path;

        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<ScheduleConfig>(File.ReadAllText(path), Json) is { } loaded
                && Validate(loaded, out var zone) is null)
            {
                _config = Normalize(loaded);
                _zone = zone!;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Não consegui ler a agenda ({ex.Message}). Usando sem agenda.");
        }

        TryZone(DefaultZone, out _zone);
    }

    public ScheduleView View(DateTimeOffset nowUtc)
    {
        ScheduleConfig config;
        TimeZoneInfo zone;
        lock (_gate)
        {
            config = _config;
            zone = _zone;
        }

        return new ScheduleView(
            config.Enabled,
            config.TimeZone ?? DefaultZone,
            config.Windows ?? new List<ScheduleWindow>(),
            IsActiveAt(config, zone, nowUtc),
            NextChange(config, zone, nowUtc));
    }

    public bool IsActive(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return IsActiveAt(_config, _zone, nowUtc);
        }
    }

    /// <summary>Próxima mudança (entrar/sair do horário), em segundos Unix; null se nunca muda.</summary>
    public long? NextChange(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return NextChange(_config, _zone, nowUtc);
        }
    }

    /// <returns>null se salvou; senão, a mensagem de erro.</returns>
    public string? Save(ScheduleConfig input)
    {
        var error = Validate(input, out var zone);
        if (error is not null) return error;

        var normalized = Normalize(input);

        lock (_gate)
        {
            AtomicFile.WriteText(_path, JsonSerializer.Serialize(normalized, Json));
            _config = normalized;
            _zone = zone!;
        }

        return null;
    }

    /// <summary>Texto curto ("seg 22:00") de um instante, no fuso da agenda.</summary>
    public static string Describe(long unixSeconds, ScheduleStore store)
    {
        TimeZoneInfo zone;
        lock (store._gate)
        {
            zone = store._zone;
        }

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone);
        return local.ToString("ddd HH:mm", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", "");
    }

    // ------------------------------------------------------------------ cálculo

    private static bool IsActiveAt(ScheduleConfig config, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (!config.Enabled || config.Windows is not { Count: > 0 } windows) return true;

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var today = IsoDay(local.DayOfWeek);
        var yesterday = today == 1 ? 7 : today - 1;
        var minute = local.Hour * 60 + local.Minute;

        foreach (var w in windows)
        {
            if (w.Days is null || w.Start is null || w.End is null) continue;

            var start = Minutes(w.Start);
            var end = Minutes(w.End);

            if (start == end)
            {
                if (w.Days.Contains(today)) return true; // dia inteiro
            }
            else if (start < end)
            {
                if (w.Days.Contains(today) && minute >= start && minute < end) return true;
            }
            else if ((w.Days.Contains(today) && minute >= start) || (w.Days.Contains(yesterday) && minute < end))
            {
                return true; // janela que vira a meia-noite
            }
        }

        return false;
    }

    private static long? NextChange(ScheduleConfig config, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (!config.Enabled || config.Windows is not { Count: > 0 }) return null;

        var current = IsActiveAt(config, zone, nowUtc);
        var t = nowUtc.AddTicks(-(nowUtc.Ticks % TimeSpan.TicksPerMinute));

        for (var i = 1; i <= 8 * 24 * 60; i++)
        {
            var candidate = t.AddMinutes(i);
            if (IsActiveAt(config, zone, candidate) != current)
                return candidate.ToUnixTimeSeconds();
        }

        return null;
    }

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

    private static int Minutes(string hhmm) =>
        int.Parse(hhmm[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(hhmm[3..], CultureInfo.InvariantCulture);

    // --------------------------------------------------------------- validação

    private static string? Validate(ScheduleConfig config, out TimeZoneInfo? zone)
    {
        zone = null;

        if (!TryZone(config.TimeZone ?? DefaultZone, out var found))
            return "Fuso horário desconhecido. Use um nome como America/Sao_Paulo.";
        zone = found;

        var windows = config.Windows ?? new List<ScheduleWindow>();

        if (windows.Count > MaxWindows)
            return $"No máximo {MaxWindows} horários.";

        if (config.Enabled && windows.Count == 0)
            return "Adicione pelo menos um horário, ou desligue a agenda.";

        foreach (var w in windows)
        {
            if (w.Days is not { Count: > 0 } days || days.Any(d => d is < 1 or > 7))
                return "Cada horário precisa de pelo menos um dia da semana.";

            if (w.Start is null || w.End is null || !TimeRegex().IsMatch(w.Start) || !TimeRegex().IsMatch(w.End))
                return "Use horas no formato HH:mm (ex.: 22:00).";
        }

        return null;
    }

    private static ScheduleConfig Normalize(ScheduleConfig c) => new(
        c.Enabled,
        c.TimeZone ?? DefaultZone,
        (c.Windows ?? new List<ScheduleWindow>())
            .Select(w => new ScheduleWindow(w.Days!.Distinct().OrderBy(d => d).ToList(), w.Start, w.End))
            .ToList());

    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeRegex();
}
