using UnityEngine;

public sealed class EVATurnAnimationLibrary : ScriptableObject
{
    public const string LayerName = "EVA Turning";
    public AnimationClip femaleLeft, femaleRight, maleLeft, maleRight;

    public AnimationClip GetClip(bool male, bool right) => male
        ? (right ? maleRight : maleLeft) : (right ? femaleRight : femaleLeft);

    public static string StateName(bool male, bool right) => LayerName + "."
        + (male ? "Male" : "Female") + (right ? "Right" : "Left");
}
