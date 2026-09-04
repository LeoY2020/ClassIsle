using ClassIsle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassIsle;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onSave;
    private int _dayIndex;

    public SettingsWindow(AppSettings settings, Action<AppSettings> onSave)
    {
        _settings = settings;
        _onSave = onSave;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 760));
        LoadSettings();
    }

    private void LoadSettings()
    {
        DayPicker.SelectedIndex = 0;
        LoadDay(0);

        LunchStartBox.Text = _settings.LunchStart;
        LunchEndBox.Text = _settings.LunchEnd;
        NapStartBox.Text = _settings.NapStart;
        NapEndBox.Text = _settings.NapEnd;
        PrepareBellBox.Value = _settings.PrepareBellMinutes;
        NotifySecondsBox.Value = _settings.NotificationSeconds;
        IdleSecondsBox.Value = _settings.IdleSeconds;
        TopMarginBox.Value = _settings.TopMargin;
        AutoStartBox.IsChecked = _settings.AutoStart;
        ShowDateBox.IsChecked = _settings.ShowDate;
        ShowCountdownBox.IsChecked = _settings.ShowCountdown;
        ShowCurrentBox.IsChecked = _settings.ShowCurrentActivity;
        ShowMoreBox.IsChecked = _settings.ShowMoreActivities;
        ShowWeatherBox.IsChecked = _settings.ShowWeather;
        ShowCountdownDayBox.IsChecked = _settings.ShowCountdownDay;
        ShowClockBox.IsChecked = _settings.ShowClock;
        CityBox.Text = _settings.CityName;
        LatitudeBox.Text = _settings.Latitude.ToString();
        LongitudeBox.Text = _settings.Longitude.ToString();
        CountdownTitleBox.Text = _settings.CountdownDayTitle;
        CountdownDateBox.Text = _settings.CountdownDayDate;
    }

    private void OnDayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DayPicker.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var idx))
        {
            _dayIndex = idx;
            LoadDay(idx);
        }
    }

    private void LoadDay(int idx)
    {
        CourseList.Items.Clear();
        foreach (var c in _settings.WeeklySchedule[idx].Courses)
            CourseList.Items.Add(c);
    }

    private void OnAddCourse(object sender, RoutedEventArgs e)
    {
        var entry = new CourseEntry();
        _settings.WeeklySchedule[_dayIndex].Courses.Add(entry);
        CourseList.Items.Add(entry);
    }

    private void OnRemoveCourse(object sender, RoutedEventArgs e)
    {
        if (CourseList.SelectedItem is CourseEntry entry)
        {
            _settings.WeeklySchedule[_dayIndex].Courses.Remove(entry);
            CourseList.Items.Remove(entry);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.LunchStart = LunchStartBox.Text;
        _settings.LunchEnd = LunchEndBox.Text;
        _settings.NapStart = NapStartBox.Text;
        _settings.NapEnd = NapEndBox.Text;
        _settings.PrepareBellMinutes = (int)PrepareBellBox.Value;
        _settings.NotificationSeconds = (int)NotifySecondsBox.Value;
        _settings.IdleSeconds = (int)IdleSecondsBox.Value;
        _settings.TopMargin = (int)TopMarginBox.Value;
        _settings.AutoStart = AutoStartBox.IsChecked == true;
        _settings.ShowDate = ShowDateBox.IsChecked == true;
        _settings.ShowCountdown = ShowCountdownBox.IsChecked == true;
        _settings.ShowCurrentActivity = ShowCurrentBox.IsChecked == true;
        _settings.ShowMoreActivities = ShowMoreBox.IsChecked == true;
        _settings.ShowWeather = ShowWeatherBox.IsChecked == true;
        _settings.ShowCountdownDay = ShowCountdownDayBox.IsChecked == true;
        _settings.ShowClock = ShowClockBox.IsChecked == true;
        _settings.CityName = CityBox.Text;
        if (double.TryParse(LatitudeBox.Text, out var lat)) _settings.Latitude = lat;
        if (double.TryParse(LongitudeBox.Text, out var lon)) _settings.Longitude = lon;
        _settings.CountdownDayTitle = CountdownTitleBox.Text;
        _settings.CountdownDayDate = CountdownDateBox.Text;

        _settings.Save();
        _onSave(_settings);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
