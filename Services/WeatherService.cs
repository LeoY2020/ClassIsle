using System.Net.Http;
using System.Text.Json;

namespace ClassIsle.Services;

public record WeatherInfo(string IconGlyph, string Description, double TemperatureC);

/// <summary>天气服务：Open-Meteo 免费无 Key API</summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<WeatherInfo?> GetAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,weather_code&timezone=auto";
            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var cur = doc.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var code = cur.GetProperty("weather_code").GetInt32();
            var (glyph, desc) = code switch
            {
                0 => ("\uE706", "晴"),
                1 or 2 => ("\uE706", "多云"),
                3 => ("\uE703", "阴"),
                45 or 48 => ("\uE703", "雾"),
                51 or 53 or 55 or 56 or 57 => ("\uE709", "毛毛雨"),
                61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("\uE709", "雨"),
                71 or 73 or 75 or 77 or 85 or 86 => ("\uE70A", "雪"),
                95 or 96 or 99 => ("\uE70A", "雷雨"),
                _ => ("\uE703", "未知"),
            };
            return new WeatherInfo(glyph, desc, Math.Round(temp));
        }
        catch
        {
            return null;
        }
    }
}
