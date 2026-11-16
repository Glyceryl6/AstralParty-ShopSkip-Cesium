using CesiumLoader.SDK.Mods;
using ShopSkip.Features;
using UnityEngine.SceneManagement;

namespace ShopSkip {
    public sealed class ShopSkipMod : ModBase {
        private ShopSkipService? _service;
        public override string Name => "ShopSkip";
        public override string Version => "0.1.1";
        public override string Description => "当卡牌商店所有未售商品都买不起时自动离开，金币不足时自动取消遗物购买。";
        public override void OnInitialize() => _service = new ShopSkipService();
        public override void OnUpdate() => _service?.Tick();
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _service?.Reset();
        public override void OnSceneUnloaded(Scene scene) => _service?.Reset();
        public override void OnUnload() { _service?.Reset(); _service = null; }
    }
}
