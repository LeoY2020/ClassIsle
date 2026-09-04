using System.IO;
using System.Text.Json;

namespace ClassIsle.Models;

/// <summary>课程条目：课程名 + 起止时间</summary>
public class CourseEntry
{
    public string Name { get; set; } = "未命名";
    public string StartTime { get; set; } = "08:00"; // HH:mm
    public string EndTime { get; set; } = "08:45";   // HH:mm
}

/// <summary>一天（周一~周日）的课表</summary>
public class DaySchedule
{
    public List<CourseEntry> Courses { get; set; } = new();
}

public class AppSettings
{
    // ------- 课表 -------
    /// <summary>索引 0 = 周一 ... 6 = 周日</summary>
    public List<DaySchedule> WeeklySchedule { get; set; } = CreateDefaultSchedule();

    public string LunchStart { get; set; } = "12:00";
    public string LunchEnd { get; set; } = "12:40";
    public string NapStart { get; set; } = "13:00";
    public string NapEnd { get; set; } = "14:00";

    /// <summary>预备铃提前分钟数，0 = 无预备铃</summary>
    public int PrepareBellMinutes { get; set; } = 2;

    /// <summary>非午休通知显示时长（秒）</summary>
    public int NotificationSeconds { get; set; } = 20;

    // ------- 灵动岛 -------
    public int IdleSeconds { get; set; } = 10;
    public int TopMargin { get; set; } = 16;

    // ------- 通用 -------
    public bool AutoStart { get; set; }
    public bool FirstRunCompleted { get; set; }

    /// <summary>组件显示开关</summary>
    public bool ShowWeather { get; set; } = true;
    public bool ShowCountdown { get; set; } = true;
    public bool ShowCurrentActivity { get; set; } = true;
    public bool ShowMoreActivities { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowCountdownDay { get; set; } = false;
    public bool ShowDate { get; set; } = false;

    // ------- 天气 -------
    public string CityName { get; set; } = "北京";
    public double Latitude { get; set; } = 39.9042;
    public double Longitude { get; set; } = 116.4074;

    // ------- 倒计日 -------
    public string CountdownDayTitle { get; set; } = "期末考试";
    public string CountdownDayDate { get; set; } = "2027-06-20";

    private static List<DaySchedule> CreateDefaultSchedule()
    {
        var list = new List<DaySchedule>();
        for (int i = 0; i < 7; i++)
        {
            var day = new DaySchedule();
            if (i < 5)
            {
                day.Courses.Add(new CourseEntry { Name = "语文", StartTime = "08:00", EndTime = "08:45" });
                day.Courses.Add(new CourseEntry { Name = "数学", StartTime = "08:55", EndTime = "09:40" });
                day.Courses.Add(new CourseEntry { Name = "英语", StartTime = "10:10", EndTime = "10:55" });
                day.Courses.Add(new CourseEntry { Name = "物理", StartTime = "11:05", EndTime = "11:50" });
                day.Courses.Add(new CourseEntry { Name = "生物", StartTime = "14:30", EndTime = "15:15" });
                day.Courses.Add(new CourseEntry { Name = "历史", StartTime = "15:25", EndTime = "16:10" });
                day.Courses.Add(new CourseEntry { Name = "体育", StartTime = "16:20", EndTime = "17:05" });
            }
            list.Add(day);
        }
        return list;
    }

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassIsle");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
