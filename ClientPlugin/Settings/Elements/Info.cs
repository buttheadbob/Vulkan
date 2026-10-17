using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;

namespace ClientPlugin.Settings.Elements;

// Read-only text: a label and the property's current value (for status and capability readouts)
internal class InfoAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public InfoAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        var value = new MyGuiControlLabel(text: propertyGetter()?.ToString() ?? "");
        if (Description != null)
            value.SetToolTip(Description);

        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(value, fillFactor: 1f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(string),
    };
}
