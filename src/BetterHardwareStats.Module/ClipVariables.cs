using System.Globalization;
using BetterHardwareStats.Core.Output;
using VRCOSC.App.ChatBox.Clips.Variables;
using VRCOSC.App.ChatBox.Clips.Variables.Instances;

namespace BetterHardwareStats.Module;
public class MissingAwareIntClipVariable : IntClipVariable
{
    public MissingAwareIntClipVariable() { }

    public MissingAwareIntClipVariable(ClipVariableReference reference) : base(reference) { }

    protected override string Format(object value) =>
        value is int i && i == ChatBoxModel.MissingInt ? ChatBoxModel.MissingText : base.Format(value);
}
public class MissingAwareFloatClipVariable : FloatClipVariable
{
    public MissingAwareFloatClipVariable() { }

    public MissingAwareFloatClipVariable(ClipVariableReference reference) : base(reference) { }

    protected override string Format(object value) =>
        value is float f && float.IsNaN(f) ? ChatBoxModel.MissingText : base.Format(value);
}
