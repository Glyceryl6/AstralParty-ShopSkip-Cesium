using CesiumLoader.SDK.Mods;
using ShopSkip.Features;
using UnityEngine.SceneManagement;

namespace ShopSkip {
    public sealed class ShopSkipMod : ModBase {
        private ShopSkipService? _service;
        private ExperimentalEarlyShopSkipService? _early;
        public override string Name => "ShopSkip";
        public override string Version => "0.1.3";
        public override string Description => "商店余额不足时自动跳过；提供默认关闭的单人提前跳过实验。";
        public override void OnInitialize() {
            _service = new ShopSkipService();
            _early = new ExperimentalEarlyShopSkipService();
        }
        public override void OnUpdate() {
            _early?.Tick();
            _service?.Tick();
        }
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            _early?.ResetSession();
            _service?.Reset();
        }
        public override void OnSceneUnloaded(Scene scene) {
            _early?.ResetSession();
            _service?.Reset();
        }
        public override void OnUnload() {
            _early?.ResetSession();
            _early = null;
            _service?.Reset();
            _service = null;
        }
    }
}
