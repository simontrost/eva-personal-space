#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

[InitializeOnLoad]
public static class EVAXRProjectSetup
{
    private const string OpenXRLoaderName =
        "UnityEngine.XR.OpenXR.OpenXRLoader";

    private const string SessionKey =
        "EVA.VR.OpenXRConfiguredThisSession";

    static EVAXRProjectSetup()
    {
        EditorApplication.delayCall += ConfigureAfterImport;
    }

    [MenuItem("EVA/Configure Meta Quest PC VR")]
    public static void ConfigureMetaQuestPcVr()
    {
        ConfigureXR();
        EVAControlSetup.UseVR();
    }

    private static void ConfigureXR()
    {
        XRGeneralSettings generalSettings =
            GetOrCreateStandaloneGeneralSettings();

        if (
            generalSettings == null
            || generalSettings.Manager == null
        )
        {
            Debug.LogError(
                "[EVA/VR] XR Plug-in Management settings are " +
                "not available yet. Wait for package import and " +
                "run EVA > Configure Meta Quest PC VR again."
            );
            return;
        }

        bool loaderAssigned = XRPackageMetadataStore.AssignLoader(
            generalSettings.Manager,
            OpenXRLoaderName,
            BuildTargetGroup.Standalone
        );

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);

        OpenXRSettings openXRSettings =
            OpenXRSettings.GetSettingsForBuildTargetGroup(
                BuildTargetGroup.Standalone
            );

        if (openXRSettings == null)
        {
            Debug.LogError(
                "[EVA/VR] Standalone OpenXR settings could not " +
                "be created."
            );
            return;
        }

        bool featureChanged = false;

        featureChanged |= EnableFeature<
            OculusTouchControllerProfile
        >(openXRSettings);

        featureChanged |= EnableFeature<
            MetaQuestTouchPlusControllerProfile
        >(openXRSettings);

        generalSettings.InitManagerOnStart = false;

        EditorUtility.SetDirty(generalSettings);
        EditorUtility.SetDirty(generalSettings.Manager);
        EditorUtility.SetDirty(openXRSettings);
        AssetDatabase.SaveAssets();

        Debug.Log(
            "[EVA/VR] Meta Quest PC VR configured: OpenXR " +
            $"loader={(loaderAssigned ? "assigned" : "already set")}, " +
            $"controller profiles={(featureChanged ? "enabled" : "ready")}."
        );
    }

    private static void ConfigureAfterImport()
    {
        if (
            SessionState.GetBool(SessionKey, false)
            || EditorApplication.isCompiling
            || EditorApplication.isUpdating
        )
        {
            if (
                !SessionState.GetBool(SessionKey, false)
                && (EditorApplication.isCompiling
                    || EditorApplication.isUpdating)
            )
            {
                EditorApplication.delayCall += ConfigureAfterImport;
            }

            return;
        }

        XRGeneralSettings generalSettings =
            GetOrCreateStandaloneGeneralSettings();

        if (
            generalSettings == null
            || generalSettings.Manager == null
        )
        {
            EditorApplication.delayCall += ConfigureAfterImport;
            return;
        }

        ConfigureXR();
        SessionState.SetBool(SessionKey, true);
    }

    private static XRGeneralSettings
        GetOrCreateStandaloneGeneralSettings()
    {
        MethodInfo getOrCreateMethod = typeof(
            XRGeneralSettingsPerBuildTarget
        ).GetMethod(
            "GetOrCreate",
            BindingFlags.Static | BindingFlags.NonPublic
        );

        XRGeneralSettingsPerBuildTarget settingsPerBuildTarget =
            getOrCreateMethod?.Invoke(null, null)
                as XRGeneralSettingsPerBuildTarget;

        if (settingsPerBuildTarget == null)
        {
            return null;
        }

        if (
            !settingsPerBuildTarget
                .HasManagerSettingsForBuildTarget(
                    BuildTargetGroup.Standalone
                )
        )
        {
            settingsPerBuildTarget
                .CreateDefaultManagerSettingsForBuildTarget(
                    BuildTargetGroup.Standalone
                );
        }

        return settingsPerBuildTarget.SettingsForBuildTarget(
            BuildTargetGroup.Standalone
        );
    }

    private static bool EnableFeature<TFeature>(
        OpenXRSettings settings
    ) where TFeature : OpenXRFeature
    {
        TFeature feature = settings.GetFeature<TFeature>();

        if (feature == null || feature.enabled)
        {
            return false;
        }

        feature.enabled = true;
        EditorUtility.SetDirty(feature);
        return true;
    }
}
#endif
