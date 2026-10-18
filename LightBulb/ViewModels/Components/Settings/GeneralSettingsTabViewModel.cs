using System;
using LightBulb.Core;
using LightBulb.Localization;
using LightBulb.Services;
using PowerKit.Extensions;

namespace LightBulb.ViewModels.Components.Settings;

public class GeneralSettingsTabViewModel(
    SettingsService settingsService,
    LocalizationManager localizationManager
) : SettingsTabViewModelBase(settingsService, localizationManager, 0)
{
    private const int MaximumIdleInvokeTimeMinutes = 1440;
    private const int MaximumIdleInvokeSliderPosition = 108;
    private const int IdleBrightnessSliderCenter = 100;

    public override string DisplayName => LocalizationManager.GeneralTabName;

    // This value is used for slider bounds, but it's not an actual restriction
    public double RecommendedMaximumDayTemperature => Math.Max(6600, DayTemperature);

    // This value is used for slider bounds, but it's not an actual restriction
    public double RecommendedMinimumDayTemperature => Math.Min(2500, DayTemperature);

    public double DayTemperature
    {
        get => SettingsService.DayConfiguration.Temperature;
        set
        {
            SettingsService.DayConfiguration = new ColorConfiguration(
                value.Clamp(SettingsService.MinimumTemperature, SettingsService.MaximumTemperature),
                DayBrightness
            );

            if (DayTemperature < NightTemperature)
                NightTemperature = DayTemperature;
        }
    }

    // This value is used for slider bounds, but it's not an actual restriction
    public double RecommendedMaximumNightTemperature => Math.Max(6600, NightTemperature);

    // This value is used for slider bounds, but it's not an actual restriction
    public double RecommendedMinimumNightTemperature => Math.Min(2500, NightTemperature);

    public double NightTemperature
    {
        get => SettingsService.NightConfiguration.Temperature;
        set
        {
            SettingsService.NightConfiguration = new ColorConfiguration(
                value.Clamp(SettingsService.MinimumTemperature, SettingsService.MaximumTemperature),
                NightBrightness
            );

            if (NightTemperature > DayTemperature)
                DayTemperature = NightTemperature;
        }
    }

    public double DayBrightness
    {
        get => SettingsService.DayConfiguration.Brightness;
        set
        {
            SettingsService.DayConfiguration = new ColorConfiguration(
                DayTemperature,
                value.Clamp(SettingsService.MinimumBrightness, SettingsService.MaximumBrightness)
            );

            if (DayBrightness < NightBrightness)
                NightBrightness = DayBrightness;
        }
    }

    public double NightBrightness
    {
        get => SettingsService.NightConfiguration.Brightness;
        set
        {
            SettingsService.NightConfiguration = new ColorConfiguration(
                NightTemperature,
                value.Clamp(SettingsService.MinimumBrightness, SettingsService.MaximumBrightness)
            );

            // Keep the saved idle offset within the range permitted by the new
            // night brightness, so the numeric box continues to show the real value.
            IdleBrightnessOffset = SettingsService.IdleBrightnessOffset;

            if (NightBrightness > DayBrightness)
                DayBrightness = NightBrightness;
        }
    }

    public TimeSpan ConfigurationTransitionDuration
    {
        get => SettingsService.ConfigurationTransitionDuration;
        set =>
            SettingsService.ConfigurationTransitionDuration = TimeSpan.FromHours(
                value.TotalHours.Clamp(0, 5)
            );
    }

    public double ConfigurationTransitionOffset
    {
        get => SettingsService.ConfigurationTransitionOffset;
        set => SettingsService.ConfigurationTransitionOffset = value.Clamp(0, 1);
    }

    public int IdleInvokeTimeMinutes
    {
        get => Math.Clamp(SettingsService.IdleInvokeTimeMinutes, 0, MaximumIdleInvokeTimeMinutes);
        set =>
            SettingsService.IdleInvokeTimeMinutes = Math.Clamp(
                value,
                0,
                MaximumIdleInvokeTimeMinutes
            );
    }

    public double IdleInvokeSliderPosition
    {
        get => MinutesToSliderPosition(IdleInvokeTimeMinutes);
        set => IdleInvokeTimeMinutes = SliderPositionToMinutes(value);
    }

    private static double MinutesToSliderPosition(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, MaximumIdleInvokeTimeMinutes);

        if (minutes <= 60)
            return minutes;

        if (minutes <= 360)
            return 60 + Math.Round((minutes - 60) / 10.0, MidpointRounding.AwayFromZero);

        return 90 + Math.Round((minutes - 360) / 60.0, MidpointRounding.AwayFromZero);
    }

    private static int SliderPositionToMinutes(double position)
    {
        var index = Math.Clamp(
            (int)Math.Round(position, MidpointRounding.AwayFromZero),
            0,
            MaximumIdleInvokeSliderPosition
        );

        if (index <= 60)
            return index;

        if (index <= 90)
            return 60 + (index - 60) * 10;

        return 360 + (index - 90) * 60;
    }

    private int MinimumIdleBrightnessOffsetPercent =>
        (int)Math.Ceiling((SettingsService.MinimumBrightness - NightBrightness) * 100 - 0.000001);

    private int MaximumIdleBrightnessOffsetPercent =>
        (int)Math.Floor((SettingsService.MaximumBrightness - NightBrightness) * 100 + 0.000001);

    private int ClampIdleBrightnessOffsetPercent(int percent) =>
        Math.Clamp(percent, MinimumIdleBrightnessOffsetPercent, MaximumIdleBrightnessOffsetPercent);

    private static int ToWholePercent(double fraction) =>
        (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

    public double IdleBrightnessOffset
    {
        get
        {
            var storedPercent = ToWholePercent(SettingsService.IdleBrightnessOffset);
            return ClampIdleBrightnessOffsetPercent(storedPercent) / 100.0;
        }
        set
        {
            if (!double.IsFinite(value))
                return;

            var requestedPercent = ToWholePercent(value);
            var acceptedPercent = ClampIdleBrightnessOffsetPercent(requestedPercent);

            SettingsService.IdleBrightnessOffset = acceptedPercent / 100.0;

            // Always refresh both controls�even if the stored value was already
            // at the limit and therefore did not raise a settings-change event.
            OnPropertyChanged(nameof(IdleBrightnessOffset));
            OnPropertyChanged(nameof(IdleBrightnessSliderPosition));
        }
    }

    public double IdleBrightnessSliderPosition
    {
        get
        {
            var offsetPercent = ToWholePercent(IdleBrightnessOffset);
            var minimum = MinimumIdleBrightnessOffsetPercent;
            var maximum = MaximumIdleBrightnessOffsetPercent;

            if (offsetPercent < 0 && minimum < 0)
                return IdleBrightnessSliderCenter + 100.0 * offsetPercent / -minimum;

            if (offsetPercent > 0 && maximum > 0)
                return IdleBrightnessSliderCenter + 100.0 * offsetPercent / maximum;

            return IdleBrightnessSliderCenter;
        }
        set
        {
            var position = Math.Clamp(value, 0, 200);
            int requestedPercent;

            if (position <= IdleBrightnessSliderCenter)
            {
                var fraction = (IdleBrightnessSliderCenter - position) / 100.0;
                requestedPercent = (int)
                    Math.Round(
                        MinimumIdleBrightnessOffsetPercent * fraction,
                        MidpointRounding.AwayFromZero
                    );
            }
            else
            {
                var fraction = (position - IdleBrightnessSliderCenter) / 100.0;
                requestedPercent = (int)
                    Math.Round(
                        MaximumIdleBrightnessOffsetPercent * fraction,
                        MidpointRounding.AwayFromZero
                    );
            }

            IdleBrightnessOffset = requestedPercent / 100.0;
        }
    }
}
