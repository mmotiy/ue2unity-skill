using System.IO;
using UnityEditor;

// CityPacks images remain 2D textures on first import and on package updates.
public class CityPacksTexturePostprocessor : AssetPostprocessor {
  private void OnPreprocessTexture() {
    if (!assetPath.Replace('\\', '/').Contains("CityPacks/Textures/")) {
      return;
    }

    var importer = (TextureImporter)assetImporter;
    string name = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
    importer.textureShape = TextureImporterShape.Texture2D;

    if (importer.userData == "CityPacksColor") {
      importer.textureType = TextureImporterType.Default;
      importer.sRGBTexture = true;
    } else if (importer.userData == "CityPacksNormalXYZ" ||
               importer.userData == "CityPacksLinearData") {
      importer.textureType = TextureImporterType.Default;
      importer.sRGBTexture = false;
    } else if (importer.textureType == TextureImporterType.NormalMap || name.EndsWith("_n") ||
        name.EndsWith("_normal") || name.EndsWith("_normals")) {
      // glTFast samples normal textures as XYZ, without Unity's DXT5nm unpacking.
      importer.textureType = TextureImporterType.Default;
      importer.sRGBTexture = false;
    } else if (name.EndsWith("_m") || name.EndsWith("_m2") || name.EndsWith("_mr") ||
               name.Contains("_mask") || name.Contains("_orm") || name.EndsWith("_colormask") ||
               name.EndsWith("_occlusion") || name.EndsWith("_ao")) {
      importer.textureType = TextureImporterType.Default;
      importer.sRGBTexture = false;
    } else {
      importer.textureType = TextureImporterType.Default;
      // The supplied meta also identifies data textures with nonstandard names.
      // Keep its color-space setting instead of overriding it by filename.
    }

    importer.mipmapEnabled = true;
    importer.maxTextureSize = 4096;
  }
}
