using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;

namespace statsCollector.Infrastructure;

public static class GameEventExtensions
{
    public static bool TryGetField<T>(this GameEvent @event, string key, out T value)
    {
        try
        {
            value = @event.Get<T>(key);
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    public static T GetFieldOrDefault<T>(this GameEvent @event, string key, T defaultValue = default!)
    {
        return @event.TryGetField<T>(key, out var value) ? value : defaultValue;
    }

    public static CCSPlayerController? GetPlayerOrDefault(this GameEvent @event, string key)
    {
        return @event.GetFieldOrDefault<CCSPlayerController?>(key);
    }

    public static string? GetStringValue(this GameEvent @event, string key, string? defaultValue = null)
    {
        return @event.TryGetField<string>(key, out var value) ? value : defaultValue;
    }

    public static int GetIntValue(this GameEvent @event, string key, int defaultValue = 0)
    {
        return @event.GetFieldOrDefault<int>(key, defaultValue);
    }

    public static float GetFloatValue(this GameEvent @event, string key, float defaultValue = 0)
    {
        return @event.GetFieldOrDefault<float>(key, defaultValue);
    }

    public static double GetDoubleValue(this GameEvent @event, string key, double defaultValue = 0)
    {
        return @event.GetFieldOrDefault<double>(key, defaultValue);
    }

    public static bool GetBoolValue(this GameEvent @event, string key, bool defaultValue = false)
    {
        if (@event.TryGetField<bool>(key, out var value))
        {
            return value;
        }

        if (@event.TryGetField<int>(key, out var intValue))
        {
            return intValue != 0;
        }

        return defaultValue;
    }
}
