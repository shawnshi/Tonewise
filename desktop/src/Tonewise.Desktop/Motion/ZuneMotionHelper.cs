using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Tonewise.Desktop.Motion;

public static class ZuneMotionHelper
{
    private sealed class GenerationCounter
    {
        public int Value;
    }

    private static readonly ConditionalWeakTable<UIElement, GenerationCounter> ElementGenerations = new();

    private static readonly Lazy<UISettings?> LazyUiSettings = new(() =>
    {
        try
        {
            return new UISettings();
        }
        catch
        {
            return null;
        }
    });

    public static bool IsMotionEnabled()
    {
        try
        {
            return LazyUiSettings.Value?.AnimationsEnabled ?? true;
        }
        catch
        {
            return true;
        }
    }

    public static void PlayStaggeredEntrance(UIElement? headerElement, UIElement? contentElement, float horizontalOffset = 28f)
    {
        if (headerElement is null && contentElement is null) return;

        if (!IsMotionEnabled())
        {
            SnapVisual(headerElement, 1f);
            SnapVisual(contentElement, 1f);
            return;
        }

        if (headerElement is not null)
        {
            AnimateSlideIn(headerElement, MathF.Round(horizontalOffset * 0.6f), durationMs: 180, delayMs: 0);
        }
        if (contentElement is not null)
        {
            AnimateSlideIn(contentElement, MathF.Round(horizontalOffset), durationMs: 240, delayMs: 35);
        }
    }

    public static void AnimateOpacity(UIElement? element, float targetOpacity, int durationMs = 200)
    {
        if (element is null) return;
        float clamped = Math.Clamp(targetOpacity, 0f, 1f);

        if (!IsMotionEnabled())
        {
            SnapVisual(element, clamped);
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, clamped, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));
            anim.Duration = TimeSpan.FromMilliseconds(Math.Max(60, durationMs));
            visual.StartAnimation("Opacity", anim);
        }
        catch
        {
            element.Opacity = clamped;
        }
    }

    private static void AnimateSlideIn(UIElement element, float startOffsetX, int durationMs, int delayMs)
    {
        try
        {
            var counter = ElementGenerations.GetOrCreateValue(element);
            int generation = ++counter.Value;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

            var transAnim = compositor.CreateVector3KeyFrameAnimation();
            transAnim.InsertKeyFrame(0.0f, new Vector3(MathF.Round(startOffsetX), 0f, 0f));
            transAnim.InsertKeyFrame(1.0f, Vector3.Zero, ease);
            transAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            if (delayMs > 0) transAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            var opAnim = compositor.CreateScalarKeyFrameAnimation();
            opAnim.InsertKeyFrame(0.0f, 0.0f);
            opAnim.InsertKeyFrame(1.0f, 1.0f, ease);
            opAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            if (delayMs > 0) opAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Translation", transAnim);
            visual.StartAnimation("Opacity", opAnim);
            batch.End();

            batch.Completed += (_, _) =>
            {
                try
                {
                    if (counter.Value != generation) return;
                    visual.StopAnimation("Translation");
                    visual.StopAnimation("Opacity");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    visual.Opacity = 1f;
                    // Releasing Composition Translation restores subpixel ClearType DirectWrite crispness on resting views
                    ElementCompositionPreview.SetIsTranslationEnabled(element, false);
                }
                catch
                {
                    element.Opacity = 1.0;
                }
                finally
                {
                    batch.Dispose();
                }
            };
        }
        catch
        {
            element.Opacity = 1.0;
        }
    }

    private static void SnapVisual(UIElement? element, float opacity)
    {
        if (element is null) return;
        try
        {
            var counter = ElementGenerations.GetOrCreateValue(element);
            counter.Value++;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = opacity;
            ElementCompositionPreview.SetIsTranslationEnabled(element, false);
        }
        catch
        {
            element.Opacity = opacity;
        }
    }
}
