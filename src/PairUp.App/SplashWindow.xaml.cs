using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PairUp.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartAnimation();
        Closed += (_, _) => StopAnimation();
    }

    private void StartAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var rings = new[] { RippleOne, RippleTwo, RippleThree };
        for (var i = 0; i < rings.Length; i++)
        {
            var begin = TimeSpan.FromSeconds(i);
            var expand = new DoubleAnimation(0.25, 1.18, TimeSpan.FromSeconds(3))
            {
                BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var scale = (ScaleTransform)rings[i].RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            var fade = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(3), BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.18)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            rings[i].BeginAnimation(OpacityProperty, fade);
        }
    }

    private void StopAnimation()
    {
        foreach (var ring in new[] { RippleOne, RippleTwo, RippleThree })
        {
            ring.BeginAnimation(OpacityProperty, null);
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }
}
