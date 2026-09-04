using System.Net.Http;
using System.Text.Json;

namespace ClassIsle.Services;

public enum DayType { Workday, Weekend, Holiday, MakeUpWorkday }

/// <summary>
/// 工作日 / 休息日 / 节假日 / 调休补班日检测。
/// 优先使用 timor.tech 节假日 API（缓存一天），失败时回退到周末判断。
/// </summary>
public static class HolidayService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static DayType? _cachedType;
    private static DateTime _cacheDate;

    public static async Task<DayType> GetDayTypeAsync(DateTime date)
    {
        if (_cachedType.HasValue && _cacheDate.Date == date.Date)
            return _cachedType.Value;

        var type = await QueryAsync(date);
        _cachedType = type;
        _cacheDate = date;
        return type;
    }

    private static async Task<DayType> QueryAsync(DateTime date)
    {
        try
        {
            // timor.tech: type 0=工作日 1=周末 2=节假日 3=调休补班
            var url = $"https://timor.tech/api/holiday/info/{date:yyyy-MM-dd}";
            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("type", out var t) && t.TryGetInt32(out var type))
            {
                return type switch
                {
                    0 => DayType.Workday,
                    1 => DayType.Weekend,
                    2 => DayType.Holiday,
                    3 => DayType.MakeUpWorkday,
                    _ => Fallback(date),
                };
            }
        }
        catch
        {
            // 网络不可用时静默回退
        }
        return Fallback(date);
    }

    private static DayType Fallback(DateTime date)
        => date.DayOfWeek switch
        {
            DayOfWeek.Saturday or DayOfWeek.Sunday => DayType.Weekend,
            _ => DayType.Workday,
        };
}
