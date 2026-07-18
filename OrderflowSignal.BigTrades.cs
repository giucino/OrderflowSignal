using System;
using System.Collections.Generic;
using ATAS.Indicators;
using OFT.Rendering.Control;

namespace OrderflowSignal
{
    // Big-Trade-Levels + Live-Tape: OnCumulativeTrade/OnNewTrade (Fremd-Threads!),
    // Historien-Rekonstruktion via CumulativeTradesRequest, Hit-/Arm-Logik.
    public partial class OrderflowSignal
    {
        // Live-Tape: jeder Cumulative-Trade ab Mindestgroesse wird dem aktuell
        // bildenden Bar netto-signiert gutgeschrieben (Buy +, Sell -). Laeuft auf
        // einem Fremd-Thread -> nur in die ConcurrentDictionary schreiben, kein
        // RedrawChart (OnCalculate liest die Werte beim naechsten Tick).
        protected override void OnCumulativeTrade(CumulativeTrade trade)
        {
            if (trade == null)
                return;
            int bar = CurrentBar - 1;
            if (bar < 0)
                return;

            int dir = trade.Direction == TradeDirection.Buy ? 1
                    : trade.Direction == TradeDirection.Sell ? -1 : 0;
            if (dir == 0)
                return;

            // Tape-Bedingung (eigene Schwelle).
            if (_tapeEnabled && trade.Volume >= _tapeMinContracts)
            {
                int signed = dir * (int)trade.Volume;
                _tapeNet.AddOrUpdate(bar, signed, (_, cur) => cur + signed);
            }

            // Big-Trade-Levels im KUMULATIVEN Modus (separiert laeuft ueber OnNewTrade).
            if (_bigEnabled && !_bigSeparated && trade.Volume >= BigThresholdFor(trade.Time))
                AddBigLevel(trade.Lastprice, (int)trade.Volume, dir, bar);
        }

        // Separierte Big Trades: jeder EINZELNE Print >= Schwelle wird ein Level
        // (nicht kumulativ aggregiert). Default-Modus.
        protected override void OnNewTrade(MarketDataArg trade)
        {
            if (!_bigEnabled || !_bigSeparated || trade == null)
                return;
            int dir = trade.Direction == TradeDirection.Buy ? 1
                    : trade.Direction == TradeDirection.Sell ? -1 : 0;
            if (dir == 0)
                return;
            int bar = CurrentBar - 1;
            if (bar < 0)
                return;
            if (trade.Volume >= BigThresholdFor(trade.Time))
                AddBigLevel(trade.Price, (int)trade.Volume, dir, bar);
        }

        // Session-abhaengige Mindestgroesse fuer ein Big-Trade-Level.
        private int BigThresholdFor(DateTime t)
        {
            int h = t.Hour;
            bool london = _bigLondonStartHour <= _bigLondonEndHour
                ? (h >= _bigLondonStartHour && h < _bigLondonEndHour)
                : (h >= _bigLondonStartHour || h < _bigLondonEndHour);
            return london ? _bigMinLondon : _bigMinDefault;
        }

        // Big-Trade als Level speichern. Dedupe am gleichen Preis unabhaengig von der
        // Richtung -> Buy und Sell am selben Preis werden EINE Linie (kein Ueberlappen).
        // Buy/Sell-Volumen getrennt gefuehrt, Richtung = dominante Seite.
        private void AddBigLevel(decimal price, int vol, int dir, int bar)
        {
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal tol = tick * _bigHitTolerance;
            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    if (!l.Hit && Math.Abs(l.Price - price) <= tol)
                    {
                        if (dir > 0) l.BuyVol += vol; else l.SellVol += vol;
                        l.Volume = l.BuyVol + l.SellVol;
                        l.Dir = l.BuyVol >= l.SellVol ? 1 : -1;   // dominante Seite
                        l.Bar = bar;
                        return;
                    }
                }
                _bigLevels.Add(new BigLevel
                {
                    Price = price, Volume = vol, Dir = dir, Bar = bar,
                    BuyVol = dir > 0 ? vol : 0, SellVol = dir < 0 ? vol : 0
                });
                while (_bigLevels.Count > _bigMaxLevels && _bigLevels.Count > 0)
                    _bigLevels.RemoveAt(0);
            }
        }

        // Re-Test-Erkennung: ein „scharfgemachtes" Level (Preis war weg) wird wieder
        // angelaufen -> Hit + Alarm (einmalig). Wird live je Tick aus ComputeLive geprueft.
        private void CheckBigLevelHits(IndicatorCandle c, int bar)
        {
            if (!_bigEnabled || c == null)
                return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal armDist = tick * _bigArmTicks;
            decimal tol = tick * _bigHitTolerance;

            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    if (l.Hit)
                        continue;
                    if (l.Bar > bar)
                        continue;         // Level existierte an diesem Bar noch nicht
                    if (!l.Armed)
                    {
                        if (Math.Abs(c.Close - l.Price) >= armDist)
                            l.Armed = true;   // Preis hat das Level verlassen -> scharf
                        continue;
                    }
                    if (c.Low - tol <= l.Price && l.Price <= c.High + tol)
                    {
                        l.Hit = true;
                        if (_histDone && !_bigReplaying && _bigAlertOnHit)
                        {
                            AlertsEnabled = true;
                            string side = l.Dir > 0 ? "Buy" : "Sell";
                            try { AddAlert(_alertSound, $"Big-Trade-Level {l.Price} ({l.Volume} {side}) angelaufen | {InstrumentInfo?.Instrument} | {c.LastTime:HH:mm}"); }
                            catch { }
                        }
                    }
                }
            }
        }

        // Historien-Anfrage: kumulative Trades ueber den geladenen Zeitraum holen,
        // um die Big-Trade-Levels nach einem Reload zu rekonstruieren.
        private void RequestBigLevelHistory()
        {
            try
            {
                var c0 = GetCandle(0);
                var cN = GetCandle(CurrentBar - 1);
                if (c0 == null || cN == null)
                    return;
                int minVol = Math.Min(_bigMinLondon, _bigMinDefault);
                if (minVol < 1) minVol = 1;
                var req = new CumulativeTradesRequest(c0.Time, cN.LastTime, minVol, int.MaxValue);
                _bigReqId = req.RequestId;
                RequestForCumulativeTrades(req);
            }
            catch { }
        }

        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
        {
            if (cumulativeTrades == null)
                return;
            if (_bigReqId != 0 && request != null && request.RequestId != _bigReqId)
                return;   // gehoert zu einer anderen Anfrage
            RebuildBigLevelsFromHistory(cumulativeTrades);
        }

        // Levels aus historischen Trades neu aufbauen: separated = jeder EINZELNE Tick
        // >= Session-Schwelle wird ein Level (identisch zur Live-Logik in OnNewTrade).
        private void RebuildBigLevelsFromHistory(IEnumerable<CumulativeTrade> trades)
        {
            if (!_bigEnabled)
                return;
            int lastBar = CurrentBar - 1;
            if (lastBar < 0)
                return;

            lock (_bigLock) { _bigLevels.Clear(); }

            int barPtr = 0;
            int minLevelBar = lastBar;
            foreach (var ctd in trades.OrderBy(t => t.Time))
            {
                if (ctd?.Ticks == null)
                    continue;
                foreach (var tk in ctd.Ticks)
                {
                    if (tk == null)
                        continue;
                    int dir = tk.Direction == TradeDirection.Buy ? 1
                            : tk.Direction == TradeDirection.Sell ? -1 : 0;
                    if (dir == 0)
                        continue;
                    if (tk.Volume < BigThresholdFor(tk.Time))
                        continue;
                    int b = FindBarByTime(tk.Time, ref barPtr, lastBar);
                    AddBigLevel(tk.Price, (int)tk.Volume, dir, b);
                    if (b < minLevelBar) minLevelBar = b;
                }
            }

            // Hit-Status historisch nachspielen (ohne Alarm) -> gehittete Levels
            // erscheinen wieder gestrichelt, ungetestete durchgezogen.
            _bigReplaying = true;
            for (int b = Math.Max(0, minLevelBar); b <= lastBar; b++)
            {
                var cc = GetCandle(b);
                if (cc != null)
                    CheckBigLevelHits(cc, b);
            }
            _bigReplaying = false;

            RedrawChart();
        }

        // Ctrl + Links-Klick auf eine Big-Trade-Linie -> dieses Level einzeln loeschen.
        public override bool ProcessMouseClick(RenderControlMouseEventArgs e)
        {
            if (e == null || !_bigEnabled || !e.Control || e.Button != RenderControlMouseButtons.Left)
                return base.ProcessMouseClick(e);

            var cont = ChartInfo?.PriceChartContainer;
            if (cont == null)
                return false;
            var region = cont.Region;
            if (e.X < region.Left || e.X > region.Right)
                return false;   // nur im Kurschart-Bereich

            const int tolPx = 6;
            BigLevel target = null;
            int best = int.MaxValue;
            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    int y;
                    try { y = cont.GetYByPrice(l.Price, false); }
                    catch { continue; }
                    int d = Math.Abs(e.Y - y);
                    if (d <= tolPx && d < best) { best = d; target = l; }
                }
                if (target != null)
                    _bigLevels.Remove(target);
            }

            if (target != null)
            {
                RedrawChart();
                return true;   // Klick verbraucht
            }
            return false;
        }

        // Bar-Index zu einem Trade-Zeitpunkt (Trades + Bars aufsteigend -> laufender Pointer).
        private int FindBarByTime(DateTime t, ref int barPtr, int lastBar)
        {
            while (barPtr < lastBar)
            {
                var next = GetCandle(barPtr + 1);
                if (next == null || next.Time > t)
                    break;      // t liegt noch im aktuellen Bar
                barPtr++;
            }
            return barPtr;
        }
    }
}
