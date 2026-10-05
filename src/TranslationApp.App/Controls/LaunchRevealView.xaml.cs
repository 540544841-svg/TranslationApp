using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TranslationApp.Theming;

namespace TranslationApp.Controls;

/// <summary>
/// 首启「启印」——把设计稿 inkseal-motion-v2.html 的迎宾落印原样搬进产品，
/// 作为盖在主窗体上的一层（不再单开小窗）。时间轴照抄设计稿的 await 序列，
/// 改常量就是改节奏，不引入第二条动效通道。
///
/// 用法：<see cref="Play"/> 起播，播完（或用户跳过）触发 <see cref="Finished"/>，
/// 宿主据此把这层收起来。系统关掉「动画效果」时 <see cref="CanPlay"/> 为 false。
/// </summary>
public partial class LaunchRevealView : UserControl
{
    // ---- 缓动曲线：与设计稿的 CSS cubic-bezier 数值一一对应 ----
    private static readonly KeySpline EaseStandard = new(0.2, 0, 0, 1);
    private static readonly KeySpline EaseDec = new(0.05, 0.7, 0.1, 1);
    private static readonly KeySpline EaseAcc = new(0.3, 0, 0.8, 0.15);
    private static readonly KeySpline EaseStamp = new(0.34, 1.45, 0.5, 1);
    private static readonly KeySpline EaseRise = new(0.45, 0.05, 0.25, 1);
    private static readonly KeySpline EaseType = new(0.25, 0.6, 0.35, 1);

    // ---- 印面三态：设计稿 TRAVEL / LIFT / LAND / SQUASH ----
    // 印面从上方飞入，而台面只是排版标尺（不裁切），所以飞行高度必须保证最高点仍在窗口里：
    // 设计稿的 -96/-106 + scale(2) 是相对 94svh 的整页主视觉定的，直接搬到 1140×780 的窗口上
    // 会顶出上沿、被窗口切掉一截，这里按窗口高度把飞行收了一档。
    private const double TravelY = -70;
    private const double TravelScale = 1.6;
    private const double TravelAngle = -12;
    private const double LiftY = -80;
    private const double LiftScale = 1.6;
    private const double SquashScaleX = 1.28;
    private const double SquashScaleY = 0.70;

    private const int TypeStepMs = 64;
    private const int TypeTailMs = 140;
    private const int TailAfterMs = 740;
    private const int HoldBeforeFadeMs = 700;
    private const int FadeOutMs = 360;

    private readonly List<Border> _rings = [];
    private readonly List<Ellipse> _drops = [];
    private readonly List<TextBlock> _enChars = [];
    private readonly TranslateTransform _signMove = new(0, -60);

    private double _enWidth = 120;
    private int _token;
    private bool _started;
    private bool _finished;

    public LaunchRevealView()
    {
        InitializeComponent();
        ApplyThemePalette();
        BuildTypeLetters();
        BuildRingsAndDrops();

        Sign.RenderTransform = _signMove;
        SetSeal(TravelY, TravelScale, TravelAngle, TravelScale);
        CjkYi.RenderTransform = new TranslateTransform(0, 9);
        CjkYin.RenderTransform = new TranslateTransform(0, 9);
        Creed.RenderTransform = new TranslateTransform(0, 6);
        Sub.RenderTransform = new TranslateTransform(0, 6);
    }

    /// <summary>系统里关掉了「动画效果」时不演这一段，避免给用户强塞动效。</summary>
    public static bool CanPlay => SystemParameters.ClientAreaAnimation;

    /// <summary>整层淡出结束（正常播完或被跳过都走这里），宿主据此收层。</summary>
    public event EventHandler? Finished;

    /// <summary>纸底与墨底各一套：接触阴影的浓度、纸缝切痕与漏光的色温都不同。</summary>
    private void ApplyThemePalette()
    {
        var dark = ThemeManager.IsDarkEffective;

        if (dark)
        {
            // 墨底上黑影本来就难显，把中心压到纯黑、放大覆盖面积，让印落进一个暗坑里。
            SealShadow.Width = 150;
            SealShadow.Height = 24;
            SealShadow.Margin = new Thickness(0, 0, 0, -5);
            ShadowA.Color = Color.FromArgb(0xFA, 0, 0, 0);
            ShadowB.Color = Color.FromArgb(0xC7, 0, 0, 0);
            ShadowC.Color = Color.FromArgb(0x00, 0, 0, 0);
        }
        else
        {
            SealShadow.Width = 112;
            SealShadow.Height = 16;
            SealShadow.Margin = new Thickness(0, 0, 0, -2);
            ShadowA.Color = Color.FromArgb(0x4D, 0x18, 0x12, 0x0E);
            ShadowB.Color = Color.FromArgb(0x21, 0x18, 0x12, 0x0E);
            ShadowC.Color = Color.FromArgb(0x00, 0x18, 0x12, 0x0E);
        }

        var glow = dark ? Color.FromRgb(0x39, 0x42, 0x4E) : Colors.White;
        var glowCore = dark ? Color.FromRgb(0x8A, 0x99, 0xAB) : Color.FromRgb(0xFF, 0xFD, 0xF8);
        var cutEdge = dark ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0x38, 0x7A, 0x4A, 0x3A);

        Bleed.Fill = new SolidColorBrush(glow);

        CutEdgeA.Color = Color.FromArgb(0, cutEdge.R, cutEdge.G, cutEdge.B);
        CutEdgeB.Color = cutEdge;
        CutEdgeC.Color = cutEdge;
        CutEdgeD.Color = Color.FromArgb(0, cutEdge.R, cutEdge.G, cutEdge.B);

        CutCoreA.Color = Color.FromArgb(0, glowCore.R, glowCore.G, glowCore.B);
        CutCoreB.Color = glowCore;
        CutCoreC.Color = glowCore;
        CutCoreD.Color = Color.FromArgb(0, glowCore.R, glowCore.G, glowCore.B);

        CutGlowA.Color = Color.FromArgb(0xC7, glowCore.R, glowCore.G, glowCore.B);
        CutGlowB.Color = Color.FromArgb(0, glowCore.R, glowCore.G, glowCore.B);
    }

    /// <summary>INKSEAL 拆成逐字，字距靠右外边距补（WPF 没有 letter-spacing）。</summary>
    private void BuildTypeLetters()
    {
        const string word = "INKSEAL";
        for (var i = 0; i < word.Length; i++)
        {
            var glyph = new TextBlock
            {
                Text = word[i].ToString(),
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Brush.TextTertiary"),
                Opacity = 0,
                Margin = new Thickness(0, 0, i == word.Length - 1 ? 0 : 4, 0),
                RenderTransform = new TranslateTransform(0, 3),
            };
            _enChars.Add(glyph);
            EnChars.Children.Add(glyph);
        }

        EnChars.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _enWidth = Math.Ceiling(EnChars.DesiredSize.Width);
        EnBox.Width = 0;
    }

    private void BuildRingsAndDrops()
    {
        var vermilion = (Brush)FindResource("Brush.Primary");
        for (var i = 0; i < 3; i++)
        {
            var ring = new Border
            {
                Width = 136,
                Height = 136,
                CornerRadius = new CornerRadius(27),
                BorderThickness = new Thickness(1.6),
                BorderBrush = vermilion,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.62, 0.62),
                IsHitTestVisible = false,
            };
            _rings.Add(ring);
            SealBox.Children.Add(ring);
        }

        for (var i = 0; i < 6; i++)
        {
            var drop = new Ellipse
            {
                Width = 4.5,
                Height = 4.5,
                Fill = vermilion,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform(),
                IsHitTestVisible = false,
            };
            _drops.Add(drop);
            SealBox.Children.Add(drop);
        }
    }

    /// <summary>离屏出图用：不播动画，直接把末帧状态摆好。</summary>
    public void ShowFinalFrame()
    {
        _started = true;
        _finished = true;
        _token++;
        Root.Opacity = 1;
        SetSeal(0, 1, 0, 1);
        Seal.Opacity = 1;
        SealShadow.Opacity = 0.92;
        SetShadowScale(1);
        _signMove.Y = 0;
        CjkYi.Opacity = 1;
        CjkYin.Opacity = 1;
        ((TranslateTransform)CjkYi.RenderTransform).Y = 0;
        ((TranslateTransform)CjkYin.RenderTransform).Y = 0;
        EnBox.Width = _enWidth;
        foreach (var glyph in _enChars)
        {
            glyph.Opacity = 1;
            ((TranslateTransform)glyph.RenderTransform).Y = 0;
        }

        ((TranslateTransform)Creed.RenderTransform).Y = 0;
        ((TranslateTransform)Sub.RenderTransform).Y = 0;
        Creed.Opacity = 1;
        Sub.Opacity = 1;
        Bleed.Opacity = 1;
        BleedScale.ScaleY = 1;
        Cut.Opacity = 1;
        CutScale.ScaleX = 1;
    }

    /// <summary>
    /// 离屏出图用：把印面摆在整段动画里最高的那一瞬（抬到 LIFT、即将砸下），核对它有没有被
    /// 窗口上沿切掉——这是最容易出界的姿态。其余元素按末帧摆，方便一眼比对，不是真实帧。
    /// </summary>
    public void ShowFlightPose()
    {
        _started = true;
        _finished = true;
        _token++;
        Root.Opacity = 1;
        SetSeal(LiftY, LiftScale, 0, LiftScale);
        _signMove.Y = 0;
        Seal.Opacity = 1;
        SealShadow.Opacity = 0.35;
        SetShadowScale(1.4);
        CjkYi.Opacity = 1;
        CjkYin.Opacity = 1;
        ((TranslateTransform)CjkYi.RenderTransform).Y = 0;
        ((TranslateTransform)CjkYin.RenderTransform).Y = 0;
        EnBox.Width = _enWidth;
        foreach (var glyph in _enChars)
        {
            glyph.Opacity = 1;
            ((TranslateTransform)glyph.RenderTransform).Y = 0;
        }

        ((TranslateTransform)Creed.RenderTransform).Y = 0;
        ((TranslateTransform)Sub.RenderTransform).Y = 0;
        Creed.Opacity = 1;
        Sub.Opacity = 1;
    }

    /// <summary>
    /// 只铺不播：把整层置为不透明，让窗口的第一帧就是启印，而不是先闪一下工作台。
    /// 宿主必须在 Show() 之前调用它——窗口一变成可见，合成线程就会抓走一帧。
    /// 不碰 _started，时间轴仍等 <see cref="Play"/> 再起，所以开场那几拍不会丢。
    /// </summary>
    public void Prime() => Root.Opacity = 1;

    /// <summary>起播。重复调用无副作用；播完（或 <see cref="Skip"/>）触发 <see cref="Finished"/>。</summary>
    public void Play()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = RunAsync();
    }

    /// <summary>
    /// 用户点一下或按 Esc：立刻收尾淡出，不再等剩余时间轴。
    /// 也允许在「只铺了层、还没起播」时收掉——否则宿主一旦在 Prime 之后没能起播，
    /// 窗口就会被一层不透明的启印永远盖着（同时把 _started 置上，堵住随后补播的 Play）。
    /// </summary>
    public void Skip()
    {
        if (_finished)
        {
            return;
        }

        _started = true;
        _finished = true;
        _token++;
        FadeOutAndFinish();
    }

    /// <summary>宿主收层前调用：停掉所有还在跑的动画，避免离屏后仍在推帧。</summary>
    public void Stop()
    {
        _token++;
        _started = true;
        _finished = true;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
    }

    private async Task RunAsync()
    {
        var token = ++_token;
        Root.Opacity = 1;

        // 1) 译印二字先在画面中央立起来
        WordIn();
        await Delay(280, token);
        if (Aborted(token))
        {
            return;
        }

        // 2) 整块落款下移到位（印章待会儿从它上方砸下来）
        Animate(_signMove, TranslateTransform.YProperty, 0, 780, EaseRise);

        await Delay(800, token);
        if (Aborted(token))
        {
            return;
        }

        // 3) 印面从高处带透视显形，接触阴影同时亮起
        Animate(Seal, UIElement.OpacityProperty, 1, 180, EaseDec);
        Animate(SealShadow, UIElement.OpacityProperty, 1, 240, EaseDec);

        await Delay(220, token);
        if (Aborted(token))
        {
            return;
        }

        // 4) 抬手：放大两倍、抽到更高处；阴影跟着扩大变淡
        Animate(SealTranslate, TranslateTransform.YProperty, LiftY, 90, EaseStandard);
        Animate(SealScale, ScaleTransform.ScaleXProperty, LiftScale, 90, EaseStandard);
        Animate(SealScale, ScaleTransform.ScaleYProperty, LiftScale, 90, EaseStandard);
        Animate(SealRotate, RotateTransform.AngleProperty, 0, 90, EaseStandard);
        Animate(SealShadow, UIElement.OpacityProperty, 0.5, 120, EaseStandard, baseValue: 1);
        Animate(ShadowScale, ScaleTransform.ScaleXProperty, 1.16, 120, EaseStandard);
        Animate(ShadowScale, ScaleTransform.ScaleYProperty, 1.16, 120, EaseStandard);

        await Delay(92, token);
        if (Aborted(token))
        {
            return;
        }

        // 5) 落纸
        Animate(SealTranslate, TranslateTransform.YProperty, 0, 110, EaseAcc);
        Animate(SealScale, ScaleTransform.ScaleXProperty, 1, 110, EaseAcc);
        Animate(SealScale, ScaleTransform.ScaleYProperty, 1, 110, EaseAcc);

        await Delay(112, token);
        if (Aborted(token))
        {
            return;
        }

        // 6) 砸实：瞬间挤压，激起震荡波、飞沫与墨洇
        SetSeal(0, SquashScaleX, 0, SquashScaleY);
        Impact();
        Animate(SealShadow, UIElement.OpacityProperty, 0.92, 210, EaseStamp, baseValue: 0.5);
        Animate(ShadowScale, ScaleTransform.ScaleXProperty, 1, 210, EaseStamp);
        Animate(ShadowScale, ScaleTransform.ScaleYProperty, 1, 210, EaseStamp);
        Animate(SealScale, ScaleTransform.ScaleXProperty, 1, 210, EaseStamp);
        Animate(SealScale, ScaleTransform.ScaleYProperty, 1, 210, EaseStamp);

        await Delay(240, token);
        if (Aborted(token))
        {
            return;
        }

        // 7) INKSEAL 逐字打进，把「译印」顶到左边
        TypeTheWord();

        await Delay(_enChars.Count * TypeStepMs + TypeTailMs, token);
        if (Aborted(token))
        {
            return;
        }

        // 8) 信条与副题浮起
        Animate(Creed, UIElement.OpacityProperty, 1, 360, EaseDec, 210);
        Animate((TranslateTransform)Creed.RenderTransform, TranslateTransform.YProperty, 0, 360, EaseDec, 210);
        Animate(Sub, UIElement.OpacityProperty, 1, 430, EaseDec, 310);
        Animate((TranslateTransform)Sub.RenderTransform, TranslateTransform.YProperty, 0, 430, EaseDec, 310);

        await Delay(TailAfterMs, token);
        if (Aborted(token))
        {
            return;
        }

        // 9) 最后一刀割开纸：纸缝张开，缝里漏下的扇光铺到下方那两行字上
        Animate(Bleed, UIElement.OpacityProperty, 1, 420, EaseDec);
        Animate(BleedScale, ScaleTransform.ScaleYProperty, 1, 420, EaseDec);
        Animate(Cut, UIElement.OpacityProperty, 1, 200, EaseDec);
        Animate(CutScale, ScaleTransform.ScaleXProperty, 1, 420, EaseDec);

        await Delay(420 + HoldBeforeFadeMs, token);
        if (Aborted(token))
        {
            return;
        }

        // 10) 整层淡出，把界面还给用户
        _finished = true;
        FadeOutAndFinish();
    }

    private bool Aborted(int token) => token != _token || _finished;

    private void FadeOutAndFinish()
    {
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(FadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Finished?.Invoke(this, EventArgs.Empty);
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>印面三态用同一组属性，分开写是为了能一次摆到某个静止姿态（末帧出图用）。</summary>
    private void SetSeal(double y, double scaleX, double angle, double scaleY)
    {
        ClearAnimation(SealTranslate, TranslateTransform.YProperty);
        ClearAnimation(SealScale, ScaleTransform.ScaleXProperty);
        ClearAnimation(SealScale, ScaleTransform.ScaleYProperty);
        ClearAnimation(SealRotate, RotateTransform.AngleProperty);
        SealTranslate.Y = y;
        SealScale.ScaleX = scaleX;
        SealScale.ScaleY = scaleY;
        SealRotate.Angle = angle;
    }

    private void SetShadowScale(double scale)
    {
        ClearAnimation(ShadowScale, ScaleTransform.ScaleXProperty);
        ClearAnimation(ShadowScale, ScaleTransform.ScaleYProperty);
        ShadowScale.ScaleX = scale;
        ShadowScale.ScaleY = scale;
    }

    /// <summary>译印二字上移淡入（设计稿 heroWordIn：300ms 落印曲线 + 70ms 错峰）。</summary>
    private void WordIn()
    {
        var index = 0;
        foreach (var glyph in new[] { CjkYi, CjkYin })
        {
            Animate(glyph, UIElement.OpacityProperty, 1, 260, EaseDec, index * 70);
            Animate((TranslateTransform)glyph.RenderTransform, TranslateTransform.YProperty,
                0, 300, EaseStamp, index * 70);
            index++;
        }
    }

    /// <summary>INKSEAL 逐字打进：宽度整体撑开（把译印顶到左边），每字再各自浮起。</summary>
    private void TypeTheWord()
    {
        var duration = _enChars.Count * TypeStepMs + 60;
        ClearAnimation(EnBox, FrameworkElement.WidthProperty);
        EnBox.Width = 0;
        Animate(EnBox, FrameworkElement.WidthProperty, _enWidth, duration, EaseType);

        for (var i = 0; i < _enChars.Count; i++)
        {
            var glyph = _enChars[i];
            Animate(glyph, UIElement.OpacityProperty, 1, 150, EaseDec, i * TypeStepMs);
            Animate((TranslateTransform)glyph.RenderTransform, TranslateTransform.YProperty,
                0, 200, EaseStamp, i * TypeStepMs);
        }
    }

    /// <summary>震荡波三圈、飞沫六粒、墨洇再开一次——设计稿 heroImpact。</summary>
    private void Impact()
    {
        for (var i = 0; i < _rings.Count; i++)
        {
            var ring = _rings[i];
            var scale = (ScaleTransform)ring.RenderTransform;
            ClearAnimation(scale, ScaleTransform.ScaleXProperty);
            ClearAnimation(scale, ScaleTransform.ScaleYProperty);
            ClearAnimation(ring, UIElement.OpacityProperty);
            scale.ScaleX = 0.62;
            scale.ScaleY = 0.62;
            ring.Opacity = 0.62;
            Animate(scale, ScaleTransform.ScaleXProperty, 1.30, 460, EaseDec, i * 42, baseValue: 0.62);
            Animate(scale, ScaleTransform.ScaleYProperty, 1.30, 460, EaseDec, i * 42, baseValue: 0.62);
            Animate(ring, UIElement.OpacityProperty, 0, 440, null, i * 42, baseValue: 0.62);
        }

        for (var i = 0; i < _drops.Count; i++)
        {
            var drop = _drops[i];
            var angle = i / 6.0 * Math.Tau + 0.4;
            var distance = 32 + i % 3 * 9;
            var move = (TranslateTransform)drop.RenderTransform;
            ClearAnimation(move, TranslateTransform.XProperty);
            ClearAnimation(move, TranslateTransform.YProperty);
            ClearAnimation(drop, UIElement.OpacityProperty);
            move.X = 0;
            move.Y = 0;
            drop.Opacity = 0.9;
            Animate(move, TranslateTransform.XProperty, Math.Cos(angle) * distance, 400, EaseDec, i * 16, baseValue: 0);
            Animate(move, TranslateTransform.YProperty, Math.Sin(angle) * distance * 0.5 + 9, 400, EaseDec, i * 16, baseValue: 0);
            Animate(drop, UIElement.OpacityProperty, 0, 380, null, i * 16, baseValue: 0.9);
        }

        // 墨洇：0% 透明 .5 倍 → 30% .46 → 100% 透明 1.22 倍
        ClearAnimation(Mist, UIElement.OpacityProperty);
        ClearAnimation(MistScale, ScaleTransform.ScaleXProperty);
        ClearAnimation(MistScale, ScaleTransform.ScaleYProperty);
        Mist.Opacity = 0;
        MistScale.ScaleX = 0.5;
        MistScale.ScaleY = 0.5;
        var bloom = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        bloom.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bloom.KeyFrames.Add(new LinearDoubleKeyFrame(0.46, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        bloom.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        Mist.BeginAnimation(UIElement.OpacityProperty, bloom);
        Animate(MistScale, ScaleTransform.ScaleXProperty, 1.22, 500, EaseDec, baseValue: 0.5);
        Animate(MistScale, ScaleTransform.ScaleYProperty, 1.22, 500, EaseDec, baseValue: 0.5);
    }

    /// <summary>
    /// 从「当前值」补到目标值：先清掉在跑的动画、把底色钉在当前值上，再起一条新动画，
    /// 这样连续接力不会因为回到基值而跳一下。
    /// </summary>
    private static void Animate(
        IAnimatable target,
        DependencyProperty property,
        double to,
        int milliseconds,
        KeySpline? spline,
        double delayMs = 0,
        double? baseValue = null)
    {
        var from = baseValue
            ?? (target is DependencyObject current && current.GetValue(property) is double value ? value : to);
        ClearAnimation(target, property);
        if (target is DependencyObject obj)
        {
            obj.SetValue(property, from);
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.HoldEnd,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(spline is null
            ? new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)))
            : new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), spline));
        target.BeginAnimation(property, animation);
    }

    private static void ClearAnimation(IAnimatable target, DependencyProperty property) =>
        target.BeginAnimation(property, null);

    private static async Task Delay(int milliseconds, int token)
    {
        await Task.Delay(milliseconds).ConfigureAwait(true);
    }
}
