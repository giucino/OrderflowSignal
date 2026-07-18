using System;
using System.Collections.Generic;
using ATAS.Indicators;

namespace OrderflowSignal
{
    // Marktstruktur: Imbalance-Zonen (Footprint-Laeufe, Arm/Fill/Alarm),
    // Balance-Range (rollende Value Area) + Range-Detektor (H-Range-Stil).
    public partial class OrderflowSignal
    {
        // Imbalance-Zonen fuer eine Kerze verarbeiten: (1) offene Zonen gegen diese Kerze
        // auf Arm/Fill/Approach pruefen, (2) optional neue Zonen aus dem Footprint erkennen.
        // Fill = die Zone wird GEGEN-imbalanced (durchgenadelt), nicht schon beim Touch.
        private void ProcessImbZones(int bar, IndicatorCandle c, bool detectNew)
        {
            if (!_imbZonesEnabled || c == null)
                return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0)
                return;

            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid;
                ask[pv.Price] = pv.Ask;
            }
            if (bid.Count == 0)
                return;

            bool BuyImb(decimal p)
            {
                if (!ask.TryGetValue(p, out var a) || a <= 0) return false;
                decimal b = bid.TryGetValue(p - tick, out var bv) ? bv : 0m;
                return b <= 0 ? true : a >= _imbZoneRatio * b;   // 0-Gegenseite = extrem
            }
            bool SellImb(decimal p)
            {
                if (!bid.TryGetValue(p, out var b) || b <= 0) return false;
                decimal a = ask.TryGetValue(p + tick, out var av) ? av : 0m;
                return a <= 0 ? true : b >= _imbZoneRatio * a;
            }

            // (1) Arm / Fill / Approach gegen bestehende, aeltere offene Zonen.
            decimal armDist = tick * _imbZoneArmTicks;
            foreach (var z in _imbZones)
            {
                if (z.Filled || z.Bar >= bar)
                    continue;
                if (!z.Armed && (c.Low > z.High + armDist || c.High < z.Low - armDist))
                    z.Armed = true;   // Preis hat die Zone verlassen -> scharf

                // Fill: Gegen-Imbalance IN der Zone (buy-Zone durch sell-Imbalance gefuellt).
                bool counter = false;
                foreach (var p in bid.Keys)
                {
                    if (p < z.Low || p > z.High) continue;
                    if (z.Dir > 0 ? SellImb(p) : BuyImb(p)) { counter = true; break; }
                }
                if (counter) { z.Filled = true; continue; }

                // Approach-Alarm: scharf + Preis wieder in der Zone (einmalig).
                if (z.Armed && !z.Alerted && c.Low <= z.High && c.High >= z.Low)
                {
                    z.Alerted = true;
                    if (_histDone && _imbZoneAlertOnHit)
                        FireImbZoneAlert(z, c);
                }
            }

            // (2) Neue Zonen aus diesem (abgeschlossenen) Bar (Grid-basiert, Voids ueberbrueckt).
            if (detectNew)
            {
                foreach (var run in DetectImbRuns(c))
                    AddImbZone(run.Low, run.High, run.Dir, bar, run.Len);
            }
        }

        // Imbalance-Laeufe einer Kerze ueber das TICK-GRID erkennen.
        // Level-Klasse: Void (kein/duenner Handel), Buy-Imb, Sell-Imb, Fair (zweiseitig).
        // Ein Lauf = zusammenhaengende Nicht-Fair-Level gleicher Aggressor-Richtung;
        // Voids UEBERBRUECKEN (kein fairer Handel = Ineffizienz), Fair BEENDET. Docht-Rand
        // ausgeschlossen. Nur Laeufe mit >= 1 Aggressor-Level UND Laenge >= Min-Stack.
        private List<(decimal Low, decimal High, int Dir, int Len)> DetectImbRuns(IndicatorCandle c)
        {
            var runs = new List<(decimal, decimal, int, int)>();
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0 || c == null)
                return runs;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            decimal lo = decimal.MaxValue, hi = decimal.MinValue;
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid; ask[pv.Price] = pv.Ask;
                if (pv.Price < lo) lo = pv.Price;
                if (pv.Price > hi) hi = pv.Price;
            }
            if (bid.Count == 0)
                return runs;

            decimal effLo = lo + _imbSkipEdgeLevels * tick;   // Docht-Rand weg
            decimal effHi = hi - _imbSkipEdgeLevels * tick;
            if (effHi < effLo)
                return runs;

            // 1 = Buy, -1 = Sell, 0 = Fair, 2 = Void
            int Classify(decimal p)
            {
                decimal b = bid.TryGetValue(p, out var bv) ? bv : 0m;
                decimal a = ask.TryGetValue(p, out var av) ? av : 0m;
                if (a + b <= _imbVoidThreshold)
                    return _imbIncludeVoids ? 2 : 0;   // Void ueberbrueckt nur, wenn aktiviert
                decimal bBelow = bid.TryGetValue(p - tick, out var bb) ? bb : 0m;
                bool buy = a > 0 && (bBelow <= 0 ? true : a >= _imbZoneRatio * bBelow);
                decimal aAbove = ask.TryGetValue(p + tick, out var aa) ? aa : 0m;
                bool sell = b > 0 && (aAbove <= 0 ? true : b >= _imbZoneRatio * aAbove);
                if (buy && !sell) return 1;
                if (sell && !buy) return -1;
                return 0;   // Fair oder ambivalent
            }

            bool inRun = false; int runDir = 0, len = 0, aggr = 0; decimal runLo = 0, runHi = 0;
            void Flush()
            {
                if (inRun && aggr > 0 && len >= _imbZoneMinStack)
                    runs.Add((runLo, runHi, runDir, len));
                inRun = false; runDir = 0; len = 0; aggr = 0;
            }
            for (decimal p = effLo; p <= effHi + tick * 0.5m; p += tick)
            {
                int cls = Classify(p);
                if (cls == 0) { Flush(); continue; }        // Fair beendet
                if (cls == 2) { if (inRun) { len++; runHi = p; } continue; }  // Void ueberbrueckt (startet nicht)
                if (inRun && cls != runDir) Flush();        // Gegenrichtung beendet
                if (!inRun) { inRun = true; runLo = p; }
                runDir = cls; aggr++; len++; runHi = p;
            }
            Flush();
            return runs;
        }

        // Zone speichern; ueberlappende offene Zonen gleicher Richtung verschmelzen.
        private void AddImbZone(decimal low, decimal high, int dir, int bar, int stack)
        {
            foreach (var z in _imbZones)
            {
                if (!z.Filled && z.Dir == dir && low <= z.High && z.Low <= high)
                {
                    z.Low = Math.Min(z.Low, low);
                    z.High = Math.Max(z.High, high);
                    z.Stack = Math.Max(z.Stack, stack);
                    return;
                }
            }
            _imbZones.Add(new ImbZone { Low = low, High = high, Dir = dir, Bar = bar, Stack = stack });
            while (_imbZones.Count > _imbZoneMaxZones && _imbZones.Count > 0)
                _imbZones.RemoveAt(0);
        }

        // (A) Hat die Kerze einen frischen, gestapelten Imbalance-Flip in Richtung dir?
        // dir > 0 = Buy-Stack (stuetzt Long-Umkehr am Tief), dir < 0 = Sell-Stack.
        // Docht-Rand ausgeschlossen (kein Finished-Auction-Tip).
        private bool HasImbStack(IndicatorCandle c, int dir)
        {
            foreach (var r in DetectImbRuns(c))
                if (r.Dir == dir) return true;
            return false;
        }

        // (B) Ist die Auktion am Extrem ABGESCHLOSSEN? Unfinished = Imbalance-Print am
        // aeussersten Level (Low: Sell-Imbalance, High: Buy-Imbalance) -> Momentum evtl.
        // weiter, Umkehr riskant. Finished = Gegenseite hat gestoppt -> stuetzt Umkehr.
        private bool AuctionFinishedAtExtreme(IndicatorCandle c, bool atLow)
        {
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0 || c == null)
                return false;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            decimal ext = atLow ? decimal.MaxValue : decimal.MinValue;
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid; ask[pv.Price] = pv.Ask;
                if (atLow ? pv.Price < ext : pv.Price > ext) ext = pv.Price;
            }
            if (bid.Count == 0)
                return false;

            if (atLow)
            {
                decimal b = bid.TryGetValue(ext, out var bv) ? bv : 0m;
                decimal a = ask.TryGetValue(ext + tick, out var av) ? av : 0m;
                bool unfinished = b > 0 && (a <= 0 ? true : b >= _imbZoneRatio * a);
                return !unfinished;
            }
            else
            {
                decimal a = ask.TryGetValue(ext, out var av) ? av : 0m;
                decimal b = bid.TryGetValue(ext - tick, out var bv) ? bv : 0m;
                bool unfinished = a > 0 && (b <= 0 ? true : a >= _imbZoneRatio * b);
                return !unfinished;
            }
        }

        private void FireImbZoneAlert(ImbZone z, IndicatorCandle c)
        {
            string side = z.Dir > 0 ? "Buy" : "Sell";
            string instr = InstrumentInfo?.Instrument ?? "";
            AlertsEnabled = true;
            try { AddAlert(_alertSound, $"Imbalance-Zone {z.Low}-{z.High} ({side}, Stack {z.Stack}) angelaufen | {instr} | {c?.LastTime:HH:mm}"); }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        //  BALANCE-RANGE (Value Area ueber rollendes Fenster)
        // ─────────────────────────────────────────────────────────────────
        private void ComputeBalanceRange()
        {
            _rangeValid = false;
            if (!_rangeEnabled)
                return;

            int last = CurrentBar - 1;
            int start = last - _rangeLookback + 1;
            if (start < 0)
                start = 0;
            if (last < start)
                return;

            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= last; i++)
            {
                var ci = GetCandle(i);
                if (ci != null)
                    AddCandleToHistogram(ci, hist);
            }
            if (hist.Count == 0)
                return;

            ComputeValueArea(hist, _rangeValuePct, out _rangeVpoc, out _rangeVah, out _rangeVal);
            _rangeStartBar = start;
            _rangeValid = _rangeVah > 0 && _rangeVal > 0;
        }

        // Range-Detektor: segmentiert die letzten N Bars in Konsolidierungs-Boxen.
        // Eine Range "laeuft", solange der Preis in einem Band <= maxWidth bleibt;
        // bricht er aus, wird sie eingefroren und nach Richtung gefaerbt.
        // Inkrementell: jede ABGESCHLOSSENE Range wird EINGEFROREN (kein Repaint).
        // Nur die aktive Kandidaten-Range waechst live. Break NUR per Close ausserhalb
        // des Bandes (Wicks brechen die Range nicht). Breite = fixe ATR je Range.
        private void ProcessDetector()
        {
            if (!_detectorEnabled)
                return;

            int lastClosed = CurrentBar - 2;   // nur abgeschlossene Bars
            if (_lastDetBar < 0)
                _lastDetBar = Math.Max(-1, lastClosed - _detectorLookback);

            while (_lastDetBar < lastClosed)
            {
                _lastDetBar++;
                DetectorStep(_lastDetBar);
            }
        }

        private void DetectorStep(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            if (!_candActive)
            {
                StartCandidate(bar, c);
                return;
            }

            decimal nh = Math.Max(_candHi, c.High), nl = Math.Min(_candLo, c.Low);

            // Passt der Bar (inkl. Docht) noch ins Breitenband? -> Range WAECHST, kein Break.
            // (Erst dadurch kann sich die Range ueberhaupt auf volle Breite etablieren.)
            if (_candWidth <= 0 || nh - nl <= _candWidth)
            {
                _candHi = nh;
                _candLo = nl;
                return;
            }

            // Bar wuerde das Band sprengen:
            //   Close AUSSERHALB des Bandes -> echter, Close-bestaetigter Break.
            //   Close noch drin (nur Docht raus)  -> Range bleibt, Band unveraendert.
            if (c.Close > _candHi || c.Close < _candLo)
            {
                int len = bar - _candStart;   // [_candStart .. bar-1]
                if (len >= _detectorMinBars)
                {
                    int dir = c.Close > _candHi ? 1 : -1;
                    TryAddRange(_candStart, bar - 1, _candHi, _candLo, dir);
                }
                StartCandidate(bar, c);
            }
            // else: Docht ueber die Breite, Close drin -> ignorieren, Range laeuft weiter.
        }

        private void StartCandidate(int bar, IndicatorCandle c)
        {
            _candActive = true;
            _candStart = bar;
            _candHi = c.High;
            _candLo = c.Low;
            _candWidth = WidthAtBar(bar);
        }

        // Stabile Breite: Ø Bar-Range ueber die letzten AtrPeriod Bars (bis bar) * Faktor.
        // Wird je Range einmal bei deren Start fixiert -> kein nachtraegliches Driften.
        private decimal WidthAtBar(int bar)
        {
            int p = Math.Max(1, _detectorAtrPeriod);
            int s = Math.Max(0, bar - p + 1);
            decimal sum = 0;
            int n = 0;
            for (int i = s; i <= bar; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) { sum += ci.High - ci.Low; n++; }
            }
            return n > 0 ? (sum / n) * _detectorWidthFactor : 0m;
        }

        // Friert eine fertige Range ein — bei aktiver Volumen-Akzeptanz nur, wenn sie
        // genug Volumen UND einen klaren vPOC hat (echte Auktions-Balance).
        private void TryAddRange(int start, int end, decimal hi, decimal lo, int dir)
        {
            decimal poc = 0m;
            if (_detectorVolumeFilter)
            {
                if (!RangePassesVolume(start, end, out poc))
                    return;
            }
            else
            {
                poc = PocOf(start, end);
            }

            // Merging: mit der letzten Range vereinen, wenn nah dran und ueberlappend.
            if (_detectorMerge && _detRanges.Count > 0)
            {
                var last = _detRanges[_detRanges.Count - 1];
                if (ShouldMerge(last, start, hi, lo))
                {
                    decimal mHi = Math.Max(last.High, hi), mLo = Math.Min(last.Low, lo);
                    _detRanges[_detRanges.Count - 1] = new DetRange
                    {
                        Start = last.Start,
                        End = end,
                        High = mHi,
                        Low = mLo,
                        Dir = dir,
                        Poc = PocOf(last.Start, end)   // vPOC ueber die ganze vereinte Spanne
                    };
                    return;
                }
            }

            _detRanges.Add(new DetRange { Start = start, End = end, High = hi, Low = lo, Dir = dir, Poc = poc });
        }

        // Merge-Kriterium: kleine Zeitluecke UND signifikante Preis-Ueberlappung
        // (>= 50 % der kleineren Box) -> dieselbe Balance.
        private bool ShouldMerge(DetRange last, int start, decimal hi, decimal lo)
        {
            int gap = start - last.End - 1;
            if (gap > _detectorMergeGapBars)
                return false;

            decimal ovHi = Math.Min(last.High, hi);
            decimal ovLo = Math.Max(last.Low, lo);
            decimal overlap = ovHi - ovLo;
            if (overlap <= 0)
                return false;

            decimal minH = Math.Min(last.High - last.Low, hi - lo);
            return minH > 0 && overlap >= 0.5m * minH;
        }

        // vPOC (Preislevel mit max. Volumen) ueber [start..end].
        private decimal PocOf(int start, int end)
        {
            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= end; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) AddCandleToHistogram(ci, hist);
            }
            decimal poc = 0m, max = -1m;
            foreach (var kv in hist)
                if (kv.Value > max) { max = kv.Value; poc = kv.Key; }
            return poc;
        }

        // Auktions-Akzeptanz: klarer vPOC (gepeakte Verteilung) UND genug Volumen
        // relativ zum Umfeld. poc = Value-Center der Range.
        private bool RangePassesVolume(int start, int end, out decimal poc)
        {
            poc = 0m;
            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= end; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) AddCandleToHistogram(ci, hist);
            }
            if (hist.Count == 0)
                return false;

            decimal total = 0m, maxVol = -1m;
            foreach (var kv in hist)
            {
                total += kv.Value;
                if (kv.Value > maxVol) { maxVol = kv.Value; poc = kv.Key; }
            }

            // (a) Klarer vPOC: POC-Level deutlich ueber dem Durchschnitt der Level.
            decimal meanLevel = total / hist.Count;
            if (!(meanLevel > 0 && maxVol >= _detectorPocFactor * meanLevel))
                return false;

            // (b) Genug Volumen: Range-Ø-Bar-Vol >= Faktor * Umfeld-Ø-Bar-Vol.
            if (_detectorMinVolFactor > 0)
            {
                int bars = end - start + 1;
                decimal rangeAvg = bars > 0 ? total / bars : 0m;
                decimal refAvg = AvgBarVolume(start - 1, _detectorAtrPeriod);
                if (refAvg > 0 && rangeAvg < _detectorMinVolFactor * refAvg)
                    return false;
            }
            return true;
        }

        private decimal AvgBarVolume(int refEnd, int period)
        {
            int s = Math.Max(0, refEnd - period + 1);
            decimal sum = 0m;
            int n = 0;
            for (int i = s; i <= refEnd; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) { sum += ci.Volume; n++; }
            }
            return n > 0 ? sum / n : 0m;
        }

        private static void AddCandleToHistogram(IndicatorCandle candle, Dictionary<decimal, decimal> hist)
        {
            bool any = false;
            foreach (var pv in candle.GetAllPriceLevels())
            {
                any = true;
                hist[pv.Price] = (hist.TryGetValue(pv.Price, out var v) ? v : 0m) + pv.Volume;
            }
            if (!any && candle.Volume > 0)
                hist[candle.Close] = (hist.TryGetValue(candle.Close, out var v2) ? v2 : 0m) + candle.Volume;
        }

        // Standard-Value-Area: vPOC finden, dann greedy zu beiden Seiten den
        // groesseren Nachbarn aufnehmen, bis pct% des Volumens abgedeckt sind.
        private static void ComputeValueArea(Dictionary<decimal, decimal> hist, int pct,
                                             out decimal vpoc, out decimal vah, out decimal val)
        {
            vpoc = vah = val = 0;
            if (hist.Count == 0)
                return;

            var prices = new List<decimal>(hist.Keys);
            prices.Sort();
            decimal total = 0;
            foreach (var v in hist.Values) total += v;

            decimal maxVol = -1m;
            int pocIdx = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                var vol = hist[prices[i]];
                if (vol > maxVol) { maxVol = vol; pocIdx = i; }
            }
            vpoc = prices[pocIdx];

            decimal target = total * (pct / 100m);
            decimal acc = hist[prices[pocIdx]];
            int lo = pocIdx, hi = pocIdx;
            while (acc < target && (lo > 0 || hi < prices.Count - 1))
            {
                decimal volBelow = lo > 0 ? hist[prices[lo - 1]] : -1m;
                decimal volAbove = hi < prices.Count - 1 ? hist[prices[hi + 1]] : -1m;
                if (volAbove >= volBelow)
                {
                    if (hi < prices.Count - 1) { hi++; acc += hist[prices[hi]]; }
                    else if (lo > 0) { lo--; acc += hist[prices[lo]]; }
                    else break;
                }
                else
                {
                    if (lo > 0) { lo--; acc += hist[prices[lo]]; }
                    else if (hi < prices.Count - 1) { hi++; acc += hist[prices[hi]]; }
                    else break;
                }
            }
            val = prices[lo];
            vah = prices[hi];
        }


    }
}
