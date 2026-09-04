using Windows.UI;

namespace ClassIsle.Services;

/// <summary>课程主题色映射（规格表）</summary>
public static class CourseThemes
{
    public static readonly Color Default = Color.FromArgb(255, 75, 170, 255);

    public static Color Get(string? courseName)
    {
        var n = (courseName ?? "").Trim();
        return n switch
        {
            "语文" => Color.FromArgb(255, 255, 151, 135),
            "数学" => Color.FromArgb(255, 105, 84, 255),
            "英语" => Color.FromArgb(255, 236, 135, 255),
            "生物" => Color.FromArgb(255, 68, 200, 94),
            "地理" => Color.FromArgb(255, 80, 214, 200),
            "政治" => Color.FromArgb(255, 255, 110, 110),
            "历史" => Color.FromArgb(255, 180, 130, 85),
            "物理" => Color.FromArgb(255, 130, 85, 180),
            "化学" => Color.FromArgb(255, 84, 135, 190),
            "体育" => Color.FromArgb(255, 255, 151, 135),
            "美术" => Color.FromArgb(255, 0, 186, 255),
            "音乐" => Color.FromArgb(255, 255, 101, 158),
            "自习" => Color.FromArgb(255, 115, 255, 150),
            "课间" => Color.FromArgb(255, 135, 255, 191),
            _ => Default,
        };
    }
}
