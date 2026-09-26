namespace JimiRoll;

/// <summary>
/// 极简双语工具：<c>T("中文", "English")</c>。
///
/// 语言由 <see cref="Logger.English"/> 决定（settings.json 的 <c>LogLanguage</c>，
/// 留空则跟随游戏语言）。默认是中文，所以中文是"主"、英文是"备"。
/// </summary>
internal static class Loc
{
    public static bool En => Logger.English;

    public static string T(string zh, string en) => Logger.English ? en : zh;

    /// <summary>数字 → 人类可读的速率（3.53 M/s 这种）。</summary>
    public static string Rate(double perSecond)
    {
        if (perSecond >= 1_000_000) return (perSecond / 1_000_000).ToString("0.##") + " M/s";
        if (perSecond >= 1_000) return (perSecond / 1_000).ToString("0.#") + " K/s";
        return perSecond.ToString("0") + "/s";
    }
}
