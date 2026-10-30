using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;

[InitializeOnLoad]
public static class EVAControlSetup
{
    private const string DesktopMenu = "EVA/Controls/Desktop (WASD + Mouse)";
    private const string VRMenu = "EVA/Controls/VR (Meta Quest)";
    static EVAControlSetup() { EditorApplication.delayCall += DisableAutomaticXR; }

    [MenuItem(DesktopMenu)] public static void UseDesktop() => SetMode(EVAControlMode.Desktop);
    [MenuItem(VRMenu)] public static void UseVR() => SetMode(EVAControlMode.VR);
    [MenuItem(DesktopMenu, true)] private static bool ValidateDesktop() => ValidateMode(DesktopMenu, EVAControlMode.Desktop);
    [MenuItem(VRMenu, true)] private static bool ValidateVR() => ValidateMode(VRMenu, EVAControlMode.VR);
    private static bool ValidateMode(string menu, EVAControlMode mode)
    {
        Menu.SetChecked(menu, EVAControlSettings.CurrentMode == mode);
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

    private static void SetMode(EVAControlMode mode)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var settings = Resources.Load<EVAControlSettings>("EVAControlSettings");
        if (settings == null) { Debug.LogError("[EVA] Control settings asset is missing."); return; }
        Undo.RecordObject(settings, "Change EVA Control Mode");
        settings.mode = mode;
        EditorUtility.SetDirty(settings);
        DisableAutomaticXR();
        AssetDatabase.SaveAssets();
        Debug.Log("[EVA] Control mode: " + mode + ". Applies on the next Play start.");
    }

    public static void DisableAutomaticXR()
    {
        var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (general != null && general.InitManagerOnStart)
        {
            // EVA starts XR explicitly only in VR mode, never before mode selection.
            general.InitManagerOnStart = false;
            EditorUtility.SetDirty(general);
            AssetDatabase.SaveAssets();
        }
    }
}
