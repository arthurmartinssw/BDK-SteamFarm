namespace BdkHourFarm;

internal static class Privacy
{
    /// <summary>"starlzcodm" vira "st******": o log não entrega o nome da conta.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "?";
        if (value.Length <= 2) return new string('*', value.Length);
        return value[..2] + new string('*', Math.Min(value.Length - 2, 6));
    }
}
