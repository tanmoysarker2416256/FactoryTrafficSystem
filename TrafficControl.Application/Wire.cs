using System.Text.RegularExpressions;
using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>Converts between C# enums and the SCREAMING_SNAKE_CASE strings used in the public API (the PDF's style).</summary>
public static class Wire
{
    private static readonly Regex Boundary = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    public static string Name<T>(T value) where T : struct, Enum
    {
        if (value is VehicleType vt && vt == VehicleType.Employee) return "EMPLOYEE_VEHICLE";   // the PDF's name
        return Boundary.Replace(value.ToString(), "_$1").ToUpperInvariant();
    }

    /// <summary>Case-insensitive, ignores '_' and '-'. So "VEHICLE_ARRIVED", "vehicle-arrived" and "VehicleArrived" all work.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = text.Trim().Replace("_", "").Replace("-", "").ToUpperInvariant();
        if (typeof(T) == typeof(VehicleType) && normalized == "EMPLOYEEVEHICLE")
        {
            value = (T)(object)VehicleType.Employee;
            return true;
        }
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (candidate.ToString().ToUpperInvariant() == normalized)
            {
                value = candidate;
                return true;
            }
        }
        return false;
    }
}
