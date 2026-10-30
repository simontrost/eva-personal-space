using System;
using UnityEditor;
using UnityEngine;

public sealed class EVARocketboxAppearanceImporter : AssetPostprocessor
{
    public const string Root = "Assets/_Project/Avatars/Rocketbox/AppearanceOptions/";

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.motionNodeName = "Bip01";
        importer.importAnimation = false;
        importer.importBlendShapes = true;
        importer.optimizeGameObjects = false;
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
    }

    private void OnPostprocessModel(GameObject model)
    {
        if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
        foreach (Transform bone in model.GetComponentsInChildren<Transform>(true))
            if (bone.name.StartsWith("Bip02", StringComparison.Ordinal))
                bone.name = bone.name.Replace("Bip02", "Bip01");
    }

    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
        var importer = (TextureImporter)assetImporter;
        bool normal = assetPath.IndexOf("_normal", StringComparison.OrdinalIgnoreCase) >= 0;
        importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.convertToNormalmap = false;
        importer.sRGBTexture = !normal && assetPath.IndexOf("_specular", StringComparison.OrdinalIgnoreCase) < 0;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
    }
}
