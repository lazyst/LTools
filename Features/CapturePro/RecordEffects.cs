using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>录进画面的鼠标键（涟漪描边宽度按它区分：左键 3，其余 2）。</summary>
internal enum RecordMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// 录屏里的键鼠特效：光标高亮、点击涟漪、光标拖尾、按键浮层。
/// </summary>
/// <remarks>
/// <para>
/// 数据来源是**每帧轮询**（<c>GetAsyncKeyState</c> / <c>GetCursorPos</c>）而不是钩子回调：
/// 钩子在 <c>HookDispatcher</c> 专用线程上跑、回调里容不得任何阻塞或锁竞争，
/// 而录制本来就是一帧一帧地抓，顺带看一眼键鼠状态既便宜又不会碰到钩子线程。
/// 代价是短于一个帧间隔（30fps 时 33ms）的极快点击可能漏掉——与「画面本身也只有 30fps」是同一件事。
/// </para>
/// <para>
/// 几何 / 时间参数照上游 <c>snow-recording-effects</c>：涟漪 600ms、半径 7 → 40、左键描边 3 其余 2；
/// 光标高亮默认半径 41；拖尾 500ms / 最多 50 个点。这些纯函数在 <see cref="RecordEffectsMath"/>，
/// <c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal sealed class RecordEffects
{
    private readonly RecordOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Click> _clicks = new();
    private readonly List<Point2> _trail = new();
    private readonly List<KeyCap> _keys = new();
    private readonly HashSet<int> _down = new();

    private readonly Font _font = new("Segoe UI", 11f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);

    private Font? _keyFont;
    private int _keyFontSize;

    private long _lastSampleMs;
    private (int X, int Y) _cursor;
    private bool _left, _right, _middle;

    public RecordEffects(RecordOptions options) => _options = options;

    /// <summary>最近一次采样的光标位置（屏幕物理像素）。</summary>
    public (int X, int Y) Cursor => _cursor;

    /// <summary>采一帧输入状态：新按下的键 / 新按下的鼠标键各自入队，过期的丢掉。</summary>
    public void Sample(long nowMs)
    {
        _lastSampleMs = nowMs;

        if (Win32.GetCursorPos(out var cursor)) _cursor = (cursor.X, cursor.Y);

        bool left = KeyState.IsDown(VirtualKey.LeftButton);
        bool right = KeyState.IsDown(VirtualKey.RightButton);
        bool middle = KeyState.IsDown(VirtualKey.MiddleButton);

        if (left && !_left) AddClick(nowMs, RecordMouseButton.Left);
        if (right && !_right) AddClick(nowMs, RecordMouseButton.Right);
        if (middle && !_middle) AddClick(nowMs, RecordMouseButton.Middle);

        _left = left;
        _right = right;
        _middle = middle;

        // 拖尾没有开关：色卡选到透明就是不画（照 Snow Shot）；寿命来自 mouse_trail_duration_ms
        if (_options.HasLaser)
        {
            _trail.Add(new Point2(nowMs, _cursor.X, _cursor.Y));
            long oldest = nowMs - Math.Max(1, _options.TrailDurationMs);
            _trail.RemoveAll(p => p.AtMs < oldest);
            if (_trail.Count > RecordEffectsMath.LaserMaxPoints)
                _trail.RemoveRange(0, _trail.Count - RecordEffectsMath.LaserMaxPoints);
        }
        else
        {
            _trail.Clear();
        }

        if (_options.Keystroke) SampleKeys(nowMs);

        SampleWheel(nowMs);

        _clicks.RemoveAll(c => nowMs - c.AtMs > RecordEffectsMath.ClickDurationMs);
        _keys.RemoveAll(k => nowMs - k.AtMs > RecordEffectsMath.KeyHoldMs + RecordEffectsMath.KeyFadeMs);
    }

    private void AddClick(long nowMs, RecordMouseButton button)
    {
        _clicks.Add(new Click(nowMs, _cursor.X, _cursor.Y, button));

        // 「记录鼠标点击」= 按键浮层里多一个「左键 / 右键 / 中键」（上游把鼠标事件也送进同一个浮层）
        if (_options.Keystroke && _options.RecordClicks)
            PushKey(nowMs, LabelOf(button), withModifiers: _options.RecordControlKeys);
    }

    private static string LabelOf(RecordMouseButton button) => button switch
    {
        RecordMouseButton.Right => "Right",
        RecordMouseButton.Middle => "Middle",
        _ => "Left",
    };

    /// <summary>滚轮：开了「记录鼠标滚动」就进按键浮层（上游同款：滚动也算一次「按键」）。</summary>
    private void SampleWheel(long nowMs)
    {
        if (!_options.Keystroke || !_options.RecordScroll) return;

        while (RecordWheelFeed.TryTake(out int delta))
        {
            PushKey(nowMs, delta > 0 ? "Scroll up" : "Scroll down", withModifiers: false);
            if (_keys.Count > 4) _keys.RemoveRange(0, _keys.Count - 4);
        }
    }

    private void SampleKeys(long nowMs)
    {
        bool shortcutHeld = KeyState.IsDown(0x11) || KeyState.IsDown(0x12) || KeyState.IsDown(0x5B) || KeyState.IsDown(0x5C);

        foreach (var key in RecordEffectsMath.WatchedKeys)
        {
            bool down = KeyState.IsDown(key.Vk);
            if (down == _down.Contains(key.Vk)) continue;

            if (down)
            {
                _down.Add(key.Vk);

                // 修饰键本身不进浮层（它会挂在别的键前面，单按一下也不值得占屏）
                if (RecordKeyboardFilter.IsModifier(key.Vk)) continue;

                if (!RecordKeyboardFilter.Accept(_options, key.Vk, shortcutHeld)) continue;

                PushKey(nowMs, _options.RecordControlKeys
                    ? RecordEffectsMath.ComboLabel(_down, key.Label)
                    : key.Label, withModifiers: false);
            }
            else
            {
                _down.Remove(key.Vk);
            }
        }
    }

    private void PushKey(long nowMs, string label, bool withModifiers)
    {
        string text = label;
        if (withModifiers)
        {
            var held = new HashSet<int>();
            if (KeyState.IsDown(0x11) || KeyState.IsDown(0xA2)) held.Add(0x11);
            if (KeyState.IsDown(0x10) || KeyState.IsDown(0xA0)) held.Add(0x10);
            if (KeyState.IsDown(0x12) || KeyState.IsDown(0xA4)) held.Add(0x12);
            if (KeyState.IsDown(0x5B) || KeyState.IsDown(0x5C)) held.Add(0x5B);

            if (held.Count > 0) text = RecordEffectsMath.ComboLabel(held, label);
        }

        _keys.Add(new KeyCap(nowMs, text));
        if (_keys.Count > 4) _keys.RemoveRange(0, _keys.Count - 4);
    }

    /// <summary>
    /// 把特效画到当前帧上。<paramref name="area"/> 是这一帧实际抓的屏幕矩形，
    /// 屏幕坐标按它换算到输出像素（跟随鼠标换区域时也对得上）。
    /// <paramref name="elapsedMs"/> 是这一帧对应的时间（播放时间角标用它）。
    /// </summary>
    public void Render(Graphics graphics, int width, int height, Int32Rect area, int bottomInset = 0,
        long elapsedMs = 0)
    {
        long now = _lastSampleMs;

        // 光标最先画：高亮 / 涟漪 / 拖尾都叠在光标上面（照 Snow Shot 的图层顺序）
        if (_options.ShowCursor) DrawCursor(graphics, width, height, area);
        if (_options.MouseHighlight) DrawHighlight(graphics, width, height, area);
        if (_options.HasClickRipple) DrawClicks(graphics, width, height, area, now);
        if (_options.HasLaser) DrawTrail(graphics, width, height, area, now);
        if (_options.Keystroke) DrawKeys(graphics, width, height, now, bottomInset);
        if (_options.PostProcessing && !_options.PostProcessingProgressBar)
            DrawPlaybackTime(graphics, width, height, elapsedMs);
    }

    private void DrawCursor(Graphics graphics, int width, int height, Int32Rect area)
    {
        var (x, y) = RecordEffectsMath.Map(_cursor.X, _cursor.Y, area, width, height);
        double scale = width / (double)Math.Max(1, area.Width);

        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.None;
            RecordCursor.Draw(graphics, x, y, scale);
        }
        finally
        {
            graphics.Restore(saved);
        }
    }

    /// <summary>播放时间角标：右下角的深色胶囊（「后期处理 → 播放时间」）。</summary>
    private void DrawPlaybackTime(Graphics graphics, int width, int height, long elapsedMs)
    {
        string text = RecordPlan.FormatClock((int)Math.Max(0, elapsedMs));

        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float scale = Math.Clamp(width / 960f, 0.75f, 2f);
            var size = graphics.MeasureString(text, _font);
            float paddingX = 10 * scale;
            float paddingY = 5 * scale;
            float w = size.Width + paddingX * 2;
            float h = size.Height + paddingY * 2;
            var rect = new RectangleF(width - w - 16 * scale, height - h - 16 * scale, w, h);

            using var path = Rounded(rect, 6 * scale);
            using var fill = new SolidBrush(Color.FromArgb(190, 24, 24, 27));
            using var ink = new SolidBrush(Color.FromArgb(255, 255, 255, 255));

            graphics.FillPath(fill, path);
            graphics.DrawString(text, _font, ink, rect.X + paddingX, rect.Y + paddingY);
        }
        finally
        {
            graphics.Restore(saved);
        }
    }

    private void DrawHighlight(Graphics graphics, int width, int height, Int32Rect area)
    {
        var (x, y) = RecordEffectsMath.Map(_cursor.X, _cursor.Y, area, width, height);
        int radius = Math.Clamp(_options.MouseHighlightRadius * width / Math.Max(1, area.Width), 4, 512);

        var ink = InkOf(_options.MouseHighlightColor, System.Drawing.Color.FromArgb(255, 255, 0, 128));
        using var ring = new Pen(System.Drawing.Color.FromArgb(200, ink.R, ink.G, ink.B), 2.4f)
        {
            Alignment = PenAlignment.Center,
        };
        using var fill = new SolidBrush(System.Drawing.Color.FromArgb(28, ink.R, ink.G, ink.B));

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(ring, x - radius, y - radius, radius * 2, radius * 2);
    }

    private void DrawClicks(Graphics graphics, int width, int height, Int32Rect area, long now)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var clickInk = ClickInk();
        if (clickInk.A == 0) return;

        foreach (var click in _clicks)
        {
            var ripple = RecordEffectsMath.ClickRipple(now - click.AtMs,
                click.Button == RecordMouseButton.Left);
            if (ripple == null) continue;

            var (x, y) = RecordEffectsMath.Map(click.X, click.Y, area, width, height);
            float scale = width / (float)Math.Max(1, area.Width);
            int radius = Math.Max(2, (int)Math.Round(ripple.Value.Radius * scale));
            int alpha = ripple.Value.Alpha * clickInk.A / 255;
            if (alpha <= 0) continue;

            using var pen = new Pen(Color.FromArgb(alpha, clickInk.R, clickInk.G, clickInk.B),
                Math.Max(1f, ripple.Value.Thickness * scale));
            graphics.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
        }
    }

    /// <summary>激光笔拖尾的墨色（色卡写进 <see cref="RecordOptions.LaserColor"/>；alpha 0 = 不画）。</summary>
    private System.Drawing.Color TrailInk() => InkOf(_options.LaserColor, System.Drawing.Color.FromArgb(255, 64, 64));

    /// <summary>点击涟漪的墨色（同上）。</summary>
    private System.Drawing.Color ClickInk() => InkOf(_options.ClickColor, System.Drawing.Color.FromArgb(255, 59, 48));

    private static System.Drawing.Color InkOf(string? hex, System.Drawing.Color fallback)
    {
        string text = RecordOptions.NormalizeColor(hex, "");
        if (text.Length != 9) return fallback;

        return System.Drawing.Color.FromArgb(
            Convert.ToInt32(text.Substring(1, 2), 16),
            Convert.ToInt32(text.Substring(3, 2), 16),
            Convert.ToInt32(text.Substring(5, 2), 16),
            Convert.ToInt32(text.Substring(7, 2), 16));
    }

    private void DrawTrail(Graphics graphics, int width, int height, Int32Rect area, long now)
    {
        if (_trail.Count < 2) return;

        var ink = TrailInk();
        if (ink.A == 0) return;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 1; i < _trail.Count; i++)
        {
            var from = RecordEffectsMath.Map(_trail[i - 1].X, _trail[i - 1].Y, area, width, height);
            var to = RecordEffectsMath.Map(_trail[i].X, _trail[i].Y, area, width, height);

            int alpha = RecordEffectsMath.TrailAlpha(now - _trail[i].AtMs);
            if (alpha <= 0) continue;

            alpha = alpha * ink.A / 255;
            if (alpha <= 0) continue;

            using var pen = new Pen(Color.FromArgb(alpha, ink.R, ink.G, ink.B), 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(pen, from.X, from.Y, to.X, to.Y);
        }
    }

    private void DrawKeys(Graphics graphics, int width, int height, long now, int bottomInset)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float scale = Math.Clamp(width / 960f, 0.75f, 2f);
        float capHeight = Math.Clamp(_options.KeyboardSize * scale, 18f, 160f);
        float paddingX = capHeight * 0.28f;
        float paddingY = capHeight * 0.12f;
        float gap = capHeight * 0.12f;
        float radius = capHeight * 0.18f;
        float bottom = height - bottomInset - capHeight * 0.3f;

        var font = KeyFont((int)Math.Round(capHeight * 0.42f));
        var background = InkOf(_options.KeyboardBackgroundColor, Color.FromArgb(204, 0, 0, 0));
        var foreground = InkOf(_options.KeyboardForegroundColor, Color.FromArgb(255, 255, 255, 255));

        for (int i = _keys.Count - 1; i >= 0; i--)
        {
            var cap = _keys[i];
            long age = now - cap.AtMs;
            if (age < 0) continue;

            var state = RecordEffectsMath.KeyCapState(age);
            if (state == null) continue;

            float alpha = state.Value.Alpha / 255f;
            float rise = (float)((1 - state.Value.Slide) * 8 * scale);

            var size = graphics.MeasureString(cap.Label, font);
            float w = size.Width + paddingX * 2;
            float h = Math.Max(capHeight, size.Height + paddingY * 2);
            var rect = new RectangleF((width - w) / 2f, bottom - h - rise, w, h);

            using var path = Rounded(rect, radius);
            using var fill = new SolidBrush(Color.FromArgb((int)(background.A * alpha), background));
            using var border = new Pen(Color.FromArgb((int)(150 * alpha), 255, 255, 255), 1f);
            using var text = new SolidBrush(Color.FromArgb((int)(foreground.A * alpha), foreground));

            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            graphics.DrawString(cap.Label, font, text, rect.X + paddingX, rect.Y + paddingY);

            bottom -= h + gap;
            if (bottom < height * 0.55f) break;
        }
    }

    /// <summary>按键浮层的字体跟着 <c>keyboard_size</c> 走（缓存一份，别每帧 new）。</summary>
    private Font KeyFont(int pixelSize)
    {
        int size = Math.Clamp(pixelSize, 8, 96);
        if (_keyFont != null && _keyFontSize == size) return _keyFont;

        _keyFont?.Dispose();
        _keyFontSize = size;
        _keyFont = new Font("Segoe UI", size, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        return _keyFont;
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _font.Dispose();
        _keyFont?.Dispose();
    }

    private readonly record struct Click(long AtMs, int X, int Y, RecordMouseButton Button);

    private readonly record struct Point2(long AtMs, int X, int Y);

    private readonly record struct KeyCap(long AtMs, string Label);

    private static class VirtualKey
    {
        public const int LeftButton = 0x01;
        public const int RightButton = 0x02;
        public const int MiddleButton = 0x04;
    }

}

/// <summary>特效的几何与时间（纯函数，<c>--selftest</c> 直接断言）。</summary>
internal static class RecordEffectsMath
{
    /// <summary>一次点击涟漪的总时长（上游 <c>CLICK_ANIMATION_MS</c>）。</summary>
    public const int ClickDurationMs = 600;

    /// <summary>涟漪扩散后的半径（上游 7 → 40）。</summary>
    public const int ClickStartRadius = 7;
    public const int ClickEndRadius = 40;

    /// <summary>拖尾寿命与点上限（上游 <c>DEFAULT_LIFETIME_MS</c> / <c>MAX_POINTS</c>）。</summary>
    public const int LaserLifeMs = 500;
    public const int LaserMaxPoints = 50;

    /// <summary>按键浮层：停留 / 淡出 / 上浮（上游 <c>HOLD_MS</c> / <c>FADE_MS</c> / <c>MOVE_MS</c>）。</summary>
    public const int KeyHoldMs = 1200;
    public const int KeyFadeMs = 400;
    public const int KeyMoveMs = 180;

    private static readonly KeyWatch[] Watch =
    {
        new(0x10, "Shift"), new(0x11, "Ctrl"), new(0x12, "Alt"), new(0x5B, "Win"),
        new(0x41, "A"), new(0x42, "B"), new(0x43, "C"), new(0x44, "D"), new(0x45, "E"),
        new(0x46, "F"), new(0x47, "G"), new(0x48, "H"), new(0x49, "I"), new(0x4A, "J"),
        new(0x4B, "K"), new(0x4C, "L"), new(0x4D, "M"), new(0x4E, "N"), new(0x4F, "O"),
        new(0x50, "P"), new(0x51, "Q"), new(0x52, "R"), new(0x53, "S"), new(0x54, "T"),
        new(0x55, "U"), new(0x56, "V"), new(0x57, "W"), new(0x58, "X"), new(0x59, "Y"),
        new(0x5A, "Z"),
        new(0x30, "0"), new(0x31, "1"), new(0x32, "2"), new(0x33, "3"), new(0x34, "4"),
        new(0x35, "5"), new(0x36, "6"), new(0x37, "7"), new(0x38, "8"), new(0x39, "9"),
        new(0x70, "F1"), new(0x71, "F2"), new(0x72, "F3"), new(0x73, "F4"), new(0x74, "F5"),
        new(0x75, "F6"), new(0x76, "F7"), new(0x77, "F8"), new(0x78, "F9"), new(0x79, "F10"),
        new(0x7A, "F11"), new(0x7B, "F12"),
        new(0x20, "Space"), new(0x0D, "Enter"), new(0x09, "Tab"), new(0x1B, "Esc"),
        new(0x08, "Backspace"), new(0x2E, "Delete"), new(0x2D, "Insert"),
        new(0x25, "←"), new(0x26, "↑"), new(0x27, "→"), new(0x28, "↓"),
        new(0x24, "Home"), new(0x23, "End"), new(0x21, "PageUp"), new(0x22, "PageDown"),
        new(0x2C, "PrtSc"), new(0x14, "CapsLock"), new(0x90, "NumLock"),
        new(0xBA, ";"), new(0xBB, "="), new(0xBC, ","), new(0xBD, "-"), new(0xBE, "."),
        new(0xBF, "/"), new(0xC0, "`"), new(0xDB, "["), new(0xDC, "\\"), new(0xDD, "]"),
        new(0xDE, "'"),
    };

    /// <summary>要轮询的键（按顺序决定浮层里同帧出现时的先后）。</summary>
    public static IReadOnlyList<KeyWatch> WatchedKeys => Watch;

    /// <summary>
    /// 涟漪在 <paramref name="ageMs"/> 时的半径 / 透明度 / 描边宽度；
    /// 超出生命周期（或时间倒流）返回 null。
    /// </summary>
    public static (int Radius, int Alpha, int Thickness)? ClickRipple(long ageMs, bool primary)
    {
        if (ageMs < 0 || ageMs > ClickDurationMs) return null;

        double progress = ageMs / (double)ClickDurationMs;
        int radius = ClickStartRadius + (int)Math.Round(progress * (ClickEndRadius - ClickStartRadius));
        int alpha = (int)Math.Round(255 * (1 - progress));
        return (radius, Math.Clamp(alpha, 0, 255), primary ? 3 : 2);
    }

    /// <summary>拖尾点的透明度：500ms 线性淡出。</summary>
    public static int TrailAlpha(long ageMs)
    {
        if (ageMs < 0 || ageMs > LaserLifeMs) return 0;
        double left = 1 - ageMs / (double)LaserLifeMs;
        return Math.Clamp((int)Math.Round(220 * left), 0, 255);
    }

    /// <summary>按键浮层在某一刻的透明度 / 上浮进度（0 = 还在原地，1 = 浮到顶）。</summary>
    public static (int Alpha, double Slide)? KeyCapState(long ageMs)
    {
        if (ageMs < 0) return null;

        if (ageMs <= KeyHoldMs)
        {
            double slide = ageMs >= KeyMoveMs ? 1 : ageMs / (double)KeyMoveMs;
            return (255, slide);
        }

        long fade = ageMs - KeyHoldMs;
        if (fade > KeyFadeMs) return null;

        return ((int)Math.Round(255 * (1 - fade / (double)KeyFadeMs)), 1);
    }

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B;

    /// <summary>按当前按下的修饰键拼出 <c>Ctrl+Shift+A</c> 这样的名字。</summary>
    public static string ComboLabel(IReadOnlySet<int> down, string key)
    {
        var parts = new List<string>(3);
        if (down.Contains(0x11)) parts.Add("Ctrl");
        if (down.Contains(0x10)) parts.Add("Shift");
        if (down.Contains(0x12)) parts.Add("Alt");
        if (down.Contains(0x5B)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>屏幕坐标 → 输出像素（跟随鼠标换区域时也对得上）。</summary>
    public static (int X, int Y) Map(int x, int y, Int32Rect area, int width, int height)
    {
        int outX = MapAxis(x - area.X, area.Width, width);
        int outY = MapAxis(y - area.Y, area.Height, height);
        return (outX, outY);
    }

    /// <summary>单轴映射：整数四舍五入，不用浮点累加以免长录漂移。</summary>
    public static int MapAxis(int value, int sourceExtent, int outputExtent)
    {
        if (sourceExtent <= 0) return value;
        long scaled = (long)value * outputExtent + sourceExtent / 2;
        return (int)(scaled / sourceExtent);
    }

    internal readonly record struct KeyWatch(int Vk, string Label);
}
