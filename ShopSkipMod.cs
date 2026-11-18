using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Logging;
using ShopSkip.Features;
using UnityEngine.SceneManagement;

namespace ShopSkip {
    public sealed class ShopSkipMod : ModBase {
        // Leave the old window-click fallback available for later use, but DO NOT
        // run it during the SDK pre-dispatch experiment: it would mask a failed hook.
        private static readonly bool EnableLegacyWindowFallback = false;

        private ShopSkipService? _service;
        private ShopWindowFallbackService? _windowFallback;
        private ShopPredictActionService? _predict;

        public override string Name => "ShopSkip";
        public override string Version => "0.1.4";
        public override string Description => "星币不足时自动跳过商店；使用 SDK 通用动作前置处理接口。";

        public override void OnInitialize() {
            _service = new ShopSkipService();
            _windowFallback = new ShopWindowFallbackService();
            _predict = new ShopPredictActionService();
            _predict.Start();
            if (!EnableLegacyWindowFallback)
                SdkLog.Warn("ShopSkip", "SDK 前置处理实验已开启；三个商店的旧窗口取消暂不执行。");
        }

        public override void OnUpdate() {
            _service?.Tick(); // Card shop and ATM only.
            if (EnableLegacyWindowFallback) _windowFallback?.Tick();
        }

        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            _service?.Reset();
            _windowFallback?.Reset();
        }

        public override void OnSceneUnloaded(Scene scene) {
            _service?.Reset();
            _windowFallback?.Reset();
        }

        public override void OnUnload() {
            _predict?.Stop();
            _predict = null;
            _service?.Reset();
            _windowFallback?.Reset();
            _service = null;
            _windowFallback = null;
        }
    }
}
