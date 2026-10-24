using Sandbox;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Settings.Elements;

internal class SliderAttribute : Attribute, IElement
{
    public enum SliderType
    {
        Integer,
        Float,
    }

    public readonly float Min;
    public readonly float Max;
    public readonly float Step;
    public readonly SliderType Type;
    public readonly string Label;
    public readonly string Description;
    public readonly string[] ValueLabels;

    // The dialog's CanHideOthers setter is protected, so a subclass sets it to
    // hide the settings screen behind the dialog.
    private sealed class AmountDialog : MyGuiScreenDialogAmount
    {
        public AmountDialog(SliderAttribute slider, float amount)
            : base(
                slider.Min,
                slider.Max,
                MyCommonTexts.DialogAmount_SetValueCaption,
                defaultAmount: amount,
                parseAsInteger: slider.Type == SliderType.Integer,
                backgroundTransition: MySandboxGame.Config.UIBkOpacity,
                guiTransition: MySandboxGame.Config.UIOpacity,
                incrementStep: slider.Step)
        {
            CanHideOthers = true;
        }
    }

    public SliderAttribute(float min, float max, float step = 1f, SliderType type = SliderType.Float, string label = null,
        string description = null, string[] valueLabels = null)
    {
        Min = min;
        Max = max;
        Step = step;
        Type = type;
        Label = label;
        Description = description;
        ValueLabels = valueLabels;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var valueLabel = new MyGuiControlLabel();
        var defaultValue = GetDefaultValue(name);
        var defaultFloat = Convert.ToSingle(defaultValue, CultureInfo.InvariantCulture);

        void ValueUpdate(MyGuiControlSlider element)
        {
            switch (Type)
            {
                case SliderType.Integer:
                    int intValue = Convert.ToInt32(element.Value);
                    propertySetter(intValue);
                    var index = intValue - (int)Min;
                    valueLabel.Text = ValueLabels != null && index >= 0 && index < ValueLabels.Length
                        ? ValueLabels[index]
                        : intValue.ToString();
                    break;

                case SliderType.Float:
                    propertySetter(element.Value);
                    valueLabel.Text = MyValueFormatter.GetFormatedFloat(element.Value, element.LabelDecimalPlaces);
                    break;
            }
        }

        bool SpecifyValue(MyGuiControlSlider element)
        {
            var screen = new AmountDialog(this, Convert.ToSingle(propertyGetter()));
            screen.OnConfirmed += (value) => element.Value = value;
            MyGuiSandbox.AddScreen(screen);
            return true;
        }

        var slider = new MyGuiControlSlider(
            toolTip: Description,
            defaultValue: Convert.ToSingle(propertyGetter()),
            minValue: Min,
            maxValue: Max,
            intValue: Type == SliderType.Integer)
        {
            MinimumStepOverride = Step,
        };

        if (Type == SliderType.Float)
        {
            // Four decimals so small slider movements are visible; the old one-decimal display
            // rounded 0.01/0.05 steps to the same text.
            slider.LabelDecimalPlaces = 4;
        }

        slider.ValueChanged += ValueUpdate;
        slider.SliderSetValueManual = SpecifyValue;

        ValueUpdate(slider);

        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        var resetText = new StringBuilder("Reset");
        var reset = new MyGuiControlButton(
            size: new Vector2(0.06f, 0.04f),
            visualStyle: MyGuiControlButtonStyleEnum.RectangularBorderLess,
            text: resetText,
            toolTip: $"Reset {label} to its default ({defaultFloat.ToString("0.####", CultureInfo.InvariantCulture)})",
            textAlignment: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
        // Size the button to its text, not the much wider default button style. RectangularBorderLess
        // has a tiny background texture, so its minimum width does not override this size.
        var resetTextWidth = MyGuiManager.MeasureString(reset.TextFont, resetText, reset.TextScaleWithLanguage).X;
        var resetWidth = Math.Max(0.05f, resetTextWidth + 0.012f);
        reset.Size = new Vector2(resetWidth, 0.04f);
        reset.ButtonClicked += _ =>
        {
            propertySetter(defaultValue);
            slider.Value = defaultFloat;
            ValueUpdate(slider);
        };

        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(slider, fillFactor: 1f, rightMargin: 0.005f),
            new Control(valueLabel, minWidth: 0.06f),
            new Control(reset, fixedWidth: resetWidth, rightMargin: 0.005f),
        };
    }

    private static object GetDefaultValue(string name)
    {
        var property = typeof(Config).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null)
            throw new InvalidOperationException($"No Config property named {name}");

        return property.GetValue(Config.Default);
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(float),
        typeof(int),
    };
}