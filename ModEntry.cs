using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;

namespace ShopSkip {
    public static class ModEntry {
        public static void Main() {
            SdkManifest.ExportSidecar();
            ModBase.Run(new ShopSkipMod(), tag: "ShopSkip");
        }
    }
}
