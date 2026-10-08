using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace UsenetBackup.Recovery;

/// <summary>
/// Windows 95 Setup aesthetic for the USB/WinPE recovery environment:
/// teal desktop, navy gradient title bar, classic 3D gray dialog with a
/// step list on the left, MS Sans Serif. Comfort, not trickery — the
/// window title and branding stay "FileKeep".
/// </summary>
internal static class Win95Theme
{
    public static readonly Color Teal = Color.FromArgb(0, 128, 128);
    public static readonly Color Navy = Color.FromArgb(0, 0, 128);
    public static readonly Color TitleEnd = Color.FromArgb(16, 132, 208);
    public static readonly Color Face = Color.FromArgb(192, 192, 192);
    public static readonly Color SidebarText = Color.White;
    public static readonly Color SidebarDim = Color.FromArgb(150, 150, 190);

    public static Font UiFont => new("MS Sans Serif", 8.25f);
    public static Font TitleFont => new("MS Sans Serif", 8.25f, FontStyle.Bold);

    /// <summary>
    /// True when running inside Windows PE (MiniNT registry key present).
    /// The recovery wizard auto-selects the Win95 theme there.
    /// </summary>
    public static bool IsWinPE()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Paints the classic navy-to-blue gradient title bar with white bold text.
    /// </summary>
    public static void PaintTitleBar(Graphics g, Rectangle bounds, string text)
    {
        using var brush = new LinearGradientBrush(
            bounds, Navy, TitleEnd, LinearGradientMode.Horizontal);
        g.FillRectangle(brush, bounds);
        TextRenderer.DrawText(g, text, TitleFont,
            new Rectangle(bounds.X + 8, bounds.Y, bounds.Width - 40, bounds.Height),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    /// <summary>
    /// Bounds of the classic "x" close button inside a title bar of the given size.
    /// </summary>
    public static Rectangle CloseButtonBounds(Rectangle titleBounds) =>
        new(titleBounds.Right - 24, titleBounds.Y + 4, 18, 18);

    public static void PaintCloseButton(Graphics g, Rectangle bounds)
    {
        ControlPaint.DrawButton(g, bounds, ButtonState.Normal);
        TextRenderer.DrawText(g, "x", UiFont, bounds, Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
