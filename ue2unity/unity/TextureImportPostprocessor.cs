using UnityEditor;

// Auto-configures CityPacks textures on import:
//   *_N / *_normal        -> TextureImporterType.NormalMap
//   *_M / *_M2 / *_MR / masks -> linear (no sRGB)
// Everything else (base colors, emissive) stays sRGB.
public class CityPacksTexturePostprocessor : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Replace('\\', '/').Contains("CityPacks/Textures")) return;
        var importer = (TextureImporter)assetImporter;
        var name = System.IO.Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();

        if (name.EndsWith("_n") || name.EndsWith("_normal"))
        {
            importer.textureType = TextureImporterType.NormalMap;
        }
        else if (name.EndsWith("_m") || name.EndsWith("_m2") || name.EndsWith("_mr")
                 || name.EndsWith("_mask") || name.EndsWith("_mask01") || name.EndsWith("_mask02")
                 || name.EndsWith("_colormask") || name.EndsWith("_occlusion"))
        {
            importer.sRGBTexture = false;
        }

        importer.mipmapEnabled = true;
        importer.maxTextureSize = 4096;
    }
}
