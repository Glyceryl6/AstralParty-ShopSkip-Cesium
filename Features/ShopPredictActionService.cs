using System;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using Core.Net;
using GameLogic;
using party.protocol;

namespace ShopSkip.Features;

/// <summary>
/// Experimental: handle confirmed unaffordable shop requests before the original
/// PredictActionS2C callback queues them. This relies on the patched CesiumLoader
/// SDK's PredictActionPreDispatch API, not Harmony or a method detour.
/// </summary>
internal sealed class ShopPredictActionService {
    // ActionListener.DealActionCall IDs in the supplied game runtime snapshot.
    private const int RelicOffer = 5249;
    private const int VendorOffer = 5323;
    private const int BuildOffer = 5399;
    private bool _registered;

    internal void Start() {
        if (_registered) return;
        PredictActionPreDispatch.BeforeDispatch += OnBeforeDispatch;
        try {
            // The SDK hook only exists while a subscriber needs the predict RPC.
            // The periodic hook also reinstalls it after the game replaces callbacks.
            GameEvents.StartAutoHook();
            _registered = true;
            SdkLog.Warn("ShopSkip.PreDispatch", "已订阅前置动作事件；旧窗口取消保持暂停，等待游戏内验证。");
        } catch {
            PredictActionPreDispatch.BeforeDispatch -= OnBeforeDispatch;
            throw;
        }
    }

    internal void Stop() {
        if (!_registered) return;
        PredictActionPreDispatch.BeforeDispatch -= OnBeforeDispatch;
        _registered = false;
    }

    private static bool OnBeforeDispatch(PredictActionRequest request) {
        if (request == null ||
            (request.ActionId != RelicOffer && request.ActionId != VendorOffer &&
             request.ActionId != BuildOffer)) return false;
        try {
            if (request.Sn <= 0 || !GameLogicManager.hasInstance) return false;
            GameLogicManager game = GameLogicManager.inst;
            BattlePlayerData? self = game.battle?.GetSelfPlayerData();
            if (self == null || self.player.Id != request.PlayerId || game.land == null)
                return false;
            int gold = self.Property.gold.Value;
            if (gold < 0) return false;

            byte[] payload = request.Data;
            if (payload.Length == 0) return false;
            int price;
            switch (request.ActionId) {
                case RelicOffer:
                    BuyRelicC2S relic = ByteBuf.ReadObject<BuyRelicC2S>(payload);
                    price = relic.RelicGold;
                    break;
                case VendorOffer:
                    VendorBuyCardC2S vendor = ByteBuf.ReadObject<VendorBuyCardC2S>(payload);
                    price = vendor.Gold;
                    break;
                case BuildOffer:
                    BuyPlantC2S build = ByteBuf.ReadObject<BuyPlantC2S>(payload);
                    price = build.Gold;
                    break;
                default: return false;
            }

            // Zero is a legitimate free offer. Unknown/negative data is never skipped.
            if (price <= 0 || price <= gold) return false;

            // These are the same cancellation methods used by each native button.
            // They send the protocol response and record the Sn in the game.
            // Only claim the action when a request object was returned.
            bool sent;
            switch (request.ActionId) {
                case RelicOffer:
                    sent = game.land.RequestBuyRelicC2S(request.Sn, 0) != null;
                    break;
                case VendorOffer:
                    sent = game.land.RequestVendorBuyCardC2S(request.Sn, false) != null;
                    break;
                case BuildOffer:
                    sent = game.land.RequestPurchaseBuildCardC2S(request.Sn, false) != null;
                    break;
                default: return false;
            }
            if (sent) {
                SdkLog.Warn("ShopSkip.PreDispatch", "尝试提前跳过: action=" + request.ActionId +
                    ", sn=" + request.Sn + ", gold=" + gold + ", price=" + price);
            }
            return sent;
        } catch (Exception e) {
            // Never claim an action that cannot be decoded or answered.
            SdkLog.Warn("ShopSkip.PreDispatch", "前置处理失败，交还原版: " + e.Message);
            return false;
        }
    }
}
