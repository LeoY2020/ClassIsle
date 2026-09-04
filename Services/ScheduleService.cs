using ClassIsle.Models;

namespace ClassIsle.Services;

public enum ScheduleEventType { PrepareBell, ClassStart, ClassEnd, Lunch, NapStart, NapEnd }

public record ScheduleEvent(ScheduleEventType Type, DateTime Time, string? CourseName = null);

/// <summary>
/// 课表调度引擎：每秒轮询，根据当前时间与课表/午饭/午休设置产生事件。
/// </summary>
public class ScheduleService
{
    private readonly AppSettings _settings;
    private readonly Action<ScheduleEvent> _onEvent;
    private DateTime _lastTick = DateTime.MinValue;
    private readonly HashSet<string> _firedToday = new();
    private DateTime _currentDate = DateTime.Today;
    private DateTime _lastEventTime = DateTime.MinValue;

    public DayType TodayType { get; private set; } = DayType.Workday;

    public ScheduleService(AppSettings settings, Action<ScheduleEvent> onEvent)
    {
        _settings = settings;
        _onEvent = onEvent;
    }

    public void Start()
    {
        _ = RefreshDayTypeAsync();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync())
            {
                try { Tick(); } catch { }
            }
        });
    }

    public async Task RefreshDayTypeAsync()
    {
        TodayType = await HolidayService.GetDayTypeAsync(DateTime.Today);
        // 日期变化时重置当日已触发事件
        if (_currentDate != DateTime.Today)
        {
            _currentDate = DateTime.Today;
            _firedToday.Clear();
        }
    }

    /// <summary>获取某时间点生效的课程（返回 null 表示空闲/课间）</summary>
    public CourseEntry? GetCurrentCourse(TimeSpan now)
    {
        var day = _settings.WeeklySchedule[((int)DateTime.Today.DayOfWeek + 6) % 7];
        CourseEntry? best = null;
        foreach (var c in day.Courses)
        {
            var s = ParseTime(c.StartTime);
            var e = ParseTime(c.EndTime);
            if (now >= s && now < e)
            {
                best = c;
                break;
            }
        }
        return best;
    }

    /// <summary>获取下一个课程（now 之后开始的第一个）</summary>
    public CourseEntry? GetNextCourse(TimeSpan now)
    {
        var day = _settings.WeeklySchedule[((int)DateTime.Today.DayOfWeek + 6) % 7];
        CourseEntry? best = null;
        var bestStart = TimeSpan.MaxValue;
        foreach (var c in day.Courses)
        {
            var s = ParseTime(c.StartTime);
            if (s > now && s < bestStart)
            {
                best = c;
                bestStart = s;
            }
        }
        return best;
    }

    public List<CourseEntry> GetUpcomingCourses(TimeSpan now, int count)
    {
        var day = _settings.WeeklySchedule[((int)DateTime.Today.DayOfWeek + 6) % 7];
        return day.Courses
            .Where(c => ParseTime(c.StartTime) > now)
            .OrderBy(c => ParseTime(c.StartTime))
            .Take(count)
            .ToList();
    }

    /// <summary>当前活动（含午饭/午休优先）</summary>
    public (string Name, bool IsBreak)? GetCurrentActivity(TimeSpan now)
    {
        // 午休 / 午饭优先于课程
        var napS = ParseTime(_settings.NapStart);
        var napE = ParseTime(_settings.NapEnd);
        if (now >= napS && now < napE) return ("午休", true);
        var lunS = ParseTime(_settings.LunchStart);
        var lunE = ParseTime(_settings.LunchEnd);
        if (now >= lunS && now < lunE) return ("午饭时间", true);

        var course = GetCurrentCourse(now);
        if (course != null) return (course.Name, false);
        return ("课间", true);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var today = now.Date;

        if (today != _currentDate)
        {
            _currentDate = today;
            _firedToday.Clear();
            _ = RefreshDayTypeAsync();
        }

        // 休息日 / 节假日：完全静默
        if (TodayType is DayType.Weekend or DayType.Holiday)
            return;

        var nowT = now.TimeOfDay;
        var day = _settings.WeeklySchedule[((int)now.DayOfWeek + 6) % 7];

        foreach (var c in day.Courses)
        {
            var s = ParseTime(c.StartTime);
            var e = ParseTime(c.EndTime);

            // 预备铃（提前 N 分钟，0 = 不触发）
            if (_settings.PrepareBellMinutes > 0)
            {
                var prep = s - TimeSpan.FromMinutes(_settings.PrepareBellMinutes);
                if (InWindow(nowT, prep, 2) && Mark(c.Name, "prep", today))
                    Fire(new ScheduleEvent(ScheduleEventType.PrepareBell, now, c.Name));
            }

            if (InWindow(nowT, s, 2) && Mark(c.Name, "start", today))
                Fire(new ScheduleEvent(ScheduleEventType.ClassStart, now, c.Name));

            if (InWindow(nowT, e, 2) && Mark(c.Name, "end", today))
                Fire(new ScheduleEvent(ScheduleEventType.ClassEnd, now, c.Name));
        }

        var lunchS = ParseTime(_settings.LunchStart);
        var lunchE = ParseTime(_settings.LunchEnd);
        var napS = ParseTime(_settings.NapStart);
        var napE = ParseTime(_settings.NapEnd);

        if (InWindow(nowT, lunchS, 2) && Mark("lunch", "start", today))
            Fire(new ScheduleEvent(ScheduleEventType.Lunch, now));

        if (InWindow(nowT, napS, 2) && Mark("nap", "start", today))
            Fire(new ScheduleEvent(ScheduleEventType.NapStart, now));
        if (InWindow(nowT, napE, 2) && Mark("nap", "end", today))
            Fire(new ScheduleEvent(ScheduleEventType.NapEnd, now));
    }

    /// <summary>通知冷却：2 秒内不重复触发</summary>
    private void Fire(ScheduleEvent evt)
    {
        if ((evt.Time - _lastEventTime).TotalSeconds < 2)
            return;
        _lastEventTime = evt.Time;
        _onEvent(evt);
    }

    private static bool InWindow(TimeSpan t, TimeSpan target, int seconds)
        => t >= target && (t - target).TotalSeconds <= seconds;

    private bool Mark(string key, string kind, DateTime date)
    {
        var k = $"{date:yyyyMMdd}|{key}|{kind}";
        return _firedToday.Add(k);
    }

    public static TimeSpan ParseTime(string s)
    {
        if (TimeSpan.TryParse(s, out var ts)) return ts;
        if (DateTime.TryParse(s, out var dt)) return dt.TimeOfDay;
        return TimeSpan.Zero;
    }
}
