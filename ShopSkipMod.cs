using CesiumLoader.SDK.Mods;
using ShopSkip.Features;
using UnityEngine.SceneManagement;

namespace ShopSkip {
    public sealed class ShopSkipMod : ModBase {
        private ShopSkipService? _service;
        private ShopWindowFallbackService? _windowFallback;
        private ExperimentalEarlyShopSkipService? _early;
        public override string Name => "ShopSkip";
        public override string Version => "0.1.3";
        public override string Description => "商店余额不足时自动跳过；尝试始终启用的前置拦截实验。";
        public override void OnInitialize() {
            _service = new ShopSkipService();
            _windowFallback = new ShopWindowFallbackService();
            _early = new ExperimentalEarlyShopSkipService();
            ExperimentalEarlyShopSkipService.Initialize();
        }
        public override void OnUpdate() {
            _early?.Tick();
            _service?.Tick();
            // Keep the old non-card window checks suspended while testing prefixes,
            // even when installation fails, so the experiment is not masked.
            _windowFallback?.Tick(_early?.IsExperimentSessionActive == true);
        }
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            _early?.ResetSession();
            _service?.Reset();
            _windowFallback?.Reset();
        }
        public override void OnSceneUnloaded(Scene scene) {
            _early?.ResetSession();
            _service?.Reset();
            _windowFallback?.Reset();
        }
        public override void OnUnload() {
            _early?.ResetSession();
            _early = null;
            _service?.Reset();
            _windowFallback?.Reset();
            _service = null;
            _windowFallback = null;
        }
    }
}
