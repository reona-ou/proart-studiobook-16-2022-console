using Windows.UI;

namespace AsusFanStudio;

// Material 3 风格的低饱和色板（tonal surface 取中性灰，三个档位用降饱和的 seed 色）
internal static class Palette
{
    public static readonly Color Quiet = Color.FromArgb(255, 122, 145, 165);
    public static readonly Color Balanced = Color.FromArgb(255, 132, 156, 138);
    public static readonly Color Turbo = Color.FromArgb(255, 169, 133, 130);
    public static readonly Color Neutral = Color.FromArgb(255, 150, 154, 160);

    // 滞回死区：用相邻档位色的淡化版，让色条读起来是连续的
    public static Color Soft(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Marker => Color.FromArgb(255, 70, 74, 82);
    public static Color Label => Color.FromArgb(225, 96, 100, 108);
    public static Color Tick => Color.FromArgb(150, 152, 156, 162);
    public static Color OnBlock => Color.FromArgb(245, 250, 250, 252);
}
