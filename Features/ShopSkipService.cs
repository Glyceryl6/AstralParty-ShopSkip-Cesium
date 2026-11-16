using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using GameLogic;
using UI;
using UnityEngine;

namespace ShopSkip.Features {
    /// <summary>
    /// 只跳过已确认无法购买的卡牌商店与遗物购买。遇到不完整数据不操作，
    /// 仅调用原版离开／取消按钮，不发送自定义购买协议。
    /// </summary>
    internal sealed class ShopSkipService {
        private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo? ShopCache = typeof(UIManager).GetField("_cachedLandShopWindow", Private);
        private static readonly FieldInfo? RelicCache = typeof(UIManager).GetField("_cacheLandRelicWindow", Private);
        private static readonly FieldInfo? ShoppingPlayer = typeof(LandShopWindow).GetField("shoppingPlayer", Private);
        private static readonly FieldInfo? CardGoods = typeof(LandShopWindow).GetField("CardGoods", Private);
        private static readonly FieldInfo? AlreadyCards = typeof(LandShopWindow).GetField("alreadyCards", Private);
        private LandShopWindow? _handledShop;
        private LandRelicWindow? _handledRelic;
        private float _nextPoll;

        internal void Tick() {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + .22f;
            if (!UIManager.hasInstance) { Reset(); return; }
            try {
                UIManager manager = UIManager.inst;
                // 不访问 UIManager.landShop/landRelic 属性，避免主界面提前创建窗口。
                LandShopWindow? shop = ShopCache?.GetValue(manager) as LandShopWindow;
                LandRelicWindow? relic = RelicCache?.GetValue(manager) as LandRelicWindow;
                if (shop is not { isShowing: true }) _handledShop = null;
                else if (!ReferenceEquals(shop, _handledShop)) TrySkipCards(shop);
                if (relic is not { isShowing: true }) _handledRelic = null;
                else if (!ReferenceEquals(relic, _handledRelic)) TrySkipRelic(relic);
            } catch (Exception) {
                // 运行时数据未就绪时保留原游戏行为；下一次轮询重试。
            }
        }

        private void TrySkipCards(LandShopWindow window) {
            if (ShoppingPlayer == null || CardGoods == null || AlreadyCards == null ||
                window.contentPane is not UILandShopWindow pane) return;
            var player = ShoppingPlayer.GetValue(window) as BattlePlayerData;
            var goods = CardGoods.GetValue(window) as List<(int, int)>;
            var sold = AlreadyCards.GetValue(window) as Google.Protobuf.Collections.RepeatedField<bool>;
            if (player == null || goods == null || goods.Count == 0 ||
                (sold != null && goods.Count > sold.Count) || goods.Count > 32 || 
                pane.btn_Leave is not { visible: true } || pane.btn_Leave.grayed) return;
            int gold = player.Property.gold.Value;
            if (gold < 0) return;
            int available = 0;
            foreach (int index in Indices(goods.Count)) {
                if (sold != null && sold[index]) continue;
                int cost = goods[index].Item2;
                if (cost is < 0 or > 100000000) return;
                available++;
                if (cost <= gold) return;
            }
            // 已售罄商品也不用额外触发“离开”；只处理剩余但无法购买的情况。
            if (available == 0) return;
            _handledShop = window;
            pane.btn_Leave.onClick.Call();
        }

        private void TrySkipRelic(LandRelicWindow window) {
            if (window.contentPane is not UILandRelicWindow pane || pane.btn_Sure?.txt_Desc == null || 
                pane.btn_Cancel is not { visible: true } || pane.btn_Cancel.grayed || !GameLogicManager.hasInstance) return;
            // 与旧版一致：只能从已经 FlushVars 的实际渲染文本中读取需支付金币。
            string html = pane.btn_Sure.txt_Desc.richTextField?.htmlText ?? "";
            string visibleText = Regex.Replace(html, "<[^>]+>", "");
            MatchCollection prices = Regex.Matches(visibleText, @"\d+");
            if (prices.Count != 1 || !int.TryParse(prices[0].Value, out var price) || price < 0) return;
            BattlePlayerData? self = GameLogicManager.inst.battle?.GetSelfPlayerData();
            if (self == null || self.Property.gold.Value >= price) return;
            _handledRelic = window;
            pane.btn_Cancel.onClick.Call();
        }

        private static IEnumerable<int> Indices(int count) {
            for (int i = 0; i < count; i++) yield return i;
        }
        internal void Reset() { _handledShop = null; _handledRelic = null; _nextPoll = 0f; }
    }
}
