using System;
using System.Collections.Generic;
using ATAS.Indicators;

namespace OrderflowSignal
{
    // Reversal-Engine: Score-Berechnung an Extrema, Treiber (DIV/ABS/VP/EXH/SPD/IMB/AUC
    // + Zusatz AC#/PWR/CDl), repaint-freie Folgekerzen-Bestaetigung, Emission, Alarm.
    public partial class OrderflowSignal
    {
        // ─────────────────────────────────────────────────────────────────
        //  REVERSAL-ENGINE
        // ─────────────────────────────────────────────────────────────────
        // Liefert den signierten Reversal-Score: > 0 Long-Umkehr (am Tief),
        // < 0 Short-Umkehr (am Hoch), 0 = keine. Gated auf neue lokale Extrema.
        private int RevEvaluate(int bar, IndicatorCandle c, decimal signedMld, decimal curCumDelta)
        {
            if (!_reversalEnabled)
                return 0;

            int start = bar - _reversalLookback;
            if (start < 0)
                return 0;
            if (!Thresholds(bar, out _, out _, out decimal absThr))
                return 0;

            // Referenz-Extrema + CVD am Extrem + Ø Tape-Speed im Fenster.
            decimal minLow = decimal.MaxValue, maxHigh = decimal.MinValue;
            decimal cdAtLow = 0, cdAtHigh = 0;
            decimal sumSpeed = 0, sumAbsDelta = 0, sumVol = 0;
            int speedN = 0;
            decimal firstClose = 0, prevClose = 0, path = 0;
            bool havePrev = false;
            for (int i = start; i < bar; i++)
            {
                var ci = GetCandle(i);
                if (ci == null)
                    return 0;
                decimal cd = i < _cumDeltaArr.Count ? _cumDeltaArr[i] : 0m;
                if (ci.Low < minLow) { minLow = ci.Low; cdAtLow = cd; }
                if (ci.High > maxHigh) { maxHigh = ci.High; cdAtHigh = cd; }
                sumSpeed += BarSpeed(ci);
                sumAbsDelta += Math.Abs(ci.Delta);
                sumVol += ci.Volume;
                speedN++;
                if (!havePrev) { firstClose = ci.Close; prevClose = ci.Close; havePrev = true; }
                else { path += Math.Abs(ci.Close - prevClose); prevClose = ci.Close; }
            }
            path += Math.Abs(c.Close - prevClose);   // aktuellen Bar in den Pfad einbeziehen
            decimal avgAbsDelta = speedN > 0 ? sumAbsDelta / speedN : 0m;
            decimal avgVol = speedN > 0 ? sumVol / speedN : 0m;

            int totalW = _revDivWeight + _revAbsWeight + _revVpocWeight + _revExhWeight + _revSpeedWeight
                       + _revImbWeight + _revAuctionWeight
                       + _revAcntWeight + _revPwrWeight + _revCdeltaWeight;
            if (totalW <= 0)
                totalW = 1;

            // Speed-Spike: Tape am Extrem deutlich schneller als der Schnitt = Klimax.
            decimal avgSpeed = speedN > 0 ? sumSpeed / speedN : 0m;
            bool speedSpike = avgSpeed > 0 && BarSpeed(c) >= _revSpeedFactor * avgSpeed;
            decimal spdX = avgSpeed > 0 ? BarSpeed(c) / avgSpeed : 0m;   // Speed-Ratio fuer Hover-Zahl

            // Impuls-Filter: Effizienz des Beins ins Extrem (|Netto-Weg| / Pfadlaenge).
            // Hoch + gerichtet = gesunder Impuls -> Gegentrend-Reversal nur mit Divergenz.
            decimal displacement = c.Close - firstClose;
            decimal eff = path > 0 ? Math.Abs(displacement) / path : 0m;

            decimal mid = (c.High + c.Low) / 2m;

            // Long-Umkehr: neues Tief, aber Orderflow dreht.
            if (c.Low <= minLow)
            {
                // (b) Echte Absorption: grosses Verkaeufer-Delta UND Tief abgelehnt
                //     (Close in der oberen Bar-Haelfte) -> Aggressor getrappt.
                bool absr = signedMld < 0 && Math.Abs(signedMld) >= absThr && c.Close >= mid;

                // Impuls-Filter: gesunder Abwaerts-Impuls -> Absorption allein reicht
                // nicht, UND Divergenz muss SIGNIFIKANT sein (kein Rauschen).
                bool strongDown = _revImpulseFilter && eff >= _revImpulseEff && displacement < 0;

                // (a) Kumulative Delta-Divergenz: tieferes Tief, aber CVD hoeher.
                //     Im Impuls: der CVD-Bruch muss >= Faktor * Ø-Bar-Delta sein.
                decimal divGap = curCumDelta - cdAtLow;
                decimal need = strongDown ? _revDivMinFactor * avgAbsDelta : 0m;
                bool divg = divGap > 0 && divGap >= need;

                if (divg || (absr && !strongDown))
                {
                    bool vpoc = VpocWickDir(c) > 0;                       // POC im unteren Docht
                    bool exh = ExhaustionAtExtreme(c, true);              // duennes Verkaufsvol am Tief
                    // A/B bei aktiver Diagnose IMMER auswerten (Anzeige), aber nur bei Gewicht > 0 werten.
                    bool imb = (_showRevDebug || _revImbWeight > 0) && HasImbStack(c, 1);
                    bool auc = (_showRevDebug || _revAuctionWeight > 0) && AuctionFinishedAtExtreme(c, true);
                    // Zusatz-Treiber (nur werten, wenn Gewicht > 0; Anzeige + Zahl bei Diagnose).
                    int acntN = (_showRevDebug || _revAcntWeight > 0 || _btLog || _revFilterAcnt) ? AbsorbedLevelCount(c, 1, signedMld) : 0;
                    bool acnt = (_showRevDebug || _revAcntWeight > 0) && acntN >= _revAcntMin;
                    bool pwr  = (_showRevDebug || _revPwrWeight > 0) && IsPowerBar(c, 1, avgVol);
                    bool cdlt = (_showRevDebug || _revCdeltaWeight > 0) && c.Delta >= _revCdeltaFactor * avgAbsDelta;
                    decimal pwrX = avgVol > 0 ? c.Volume / avgVol : 0m;
                    decimal cdlX = avgAbsDelta > 0 ? c.Delta / avgAbsDelta : 0m;
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (vpoc) s += _revVpocWeight;
                    if (exh) s += _revExhWeight;
                    if (speedSpike) s += _revSpeedWeight;                 // Klimax-Tape am Tief
                    if (imb) s += _revImbWeight;
                    if (auc) s += _revAuctionWeight;
                    if (acnt) s += _revAcntWeight;
                    if (pwr) s += _revPwrWeight;
                    if (cdlt) s += _revCdeltaWeight;
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                    {
                        SetRevCand(1, pct, eff, strongDown, divg, absr, vpoc, exh, speedSpike, imb, auc, acnt, pwr, cdlt, acntN, pwrX, cdlX, spdX);
                        return pct;
                    }
                }
            }

            // Short-Umkehr: neues Hoch, aber Orderflow dreht.
            if (c.High >= maxHigh)
            {
                bool absr = signedMld > 0 && Math.Abs(signedMld) >= absThr && c.Close <= mid;

                // Impuls-Filter: gesunder Aufwaerts-Impuls -> Absorption allein reicht
                // nicht, UND Divergenz muss SIGNIFIKANT sein.
                bool strongUp = _revImpulseFilter && eff >= _revImpulseEff && displacement > 0;

                decimal divGap = cdAtHigh - curCumDelta;   // > 0 = bearishe Divergenz
                decimal need = strongUp ? _revDivMinFactor * avgAbsDelta : 0m;
                bool divg = divGap > 0 && divGap >= need;

                if (divg || (absr && !strongUp))
                {
                    bool vpoc = VpocWickDir(c) < 0;
                    bool exh = ExhaustionAtExtreme(c, false);
                    // A/B bei aktiver Diagnose IMMER auswerten (Anzeige), aber nur bei Gewicht > 0 werten.
                    bool imb = (_showRevDebug || _revImbWeight > 0) && HasImbStack(c, -1);
                    bool auc = (_showRevDebug || _revAuctionWeight > 0) && AuctionFinishedAtExtreme(c, false);
                    // Zusatz-Treiber (nur werten, wenn Gewicht > 0; Anzeige + Zahl bei Diagnose).
                    int acntN = (_showRevDebug || _revAcntWeight > 0 || _btLog || _revFilterAcnt) ? AbsorbedLevelCount(c, -1, signedMld) : 0;
                    bool acnt = (_showRevDebug || _revAcntWeight > 0) && acntN >= _revAcntMin;
                    bool pwr  = (_showRevDebug || _revPwrWeight > 0) && IsPowerBar(c, -1, avgVol);
                    bool cdlt = (_showRevDebug || _revCdeltaWeight > 0) && c.Delta <= -_revCdeltaFactor * avgAbsDelta;
                    decimal pwrX = avgVol > 0 ? c.Volume / avgVol : 0m;
                    decimal cdlX = avgAbsDelta > 0 ? c.Delta / avgAbsDelta : 0m;
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (vpoc) s += _revVpocWeight;
                    if (exh) s += _revExhWeight;
                    if (speedSpike) s += _revSpeedWeight;                 // Klimax-Tape am Hoch
                    if (imb) s += _revImbWeight;
                    if (auc) s += _revAuctionWeight;
                    if (acnt) s += _revAcntWeight;
                    if (pwr) s += _revPwrWeight;
                    if (cdlt) s += _revCdeltaWeight;
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                    {
                        SetRevCand(-1, pct, eff, strongUp, divg, absr, vpoc, exh, speedSpike, imb, auc, acnt, pwr, cdlt, acntN, pwrX, cdlX, spdX);
                        return -pct;
                    }
                }
            }

            return 0;
        }

        // Diagnose: Treiber-Aufschluesselung des aktuellen Reversal-Kandidaten merken.
        private void SetRevCand(int dir, int pct, decimal eff, bool strong,
            bool div, bool abs, bool vpoc, bool exh, bool spd, bool imb, bool auc,
            bool acn, bool pwr, bool cdl, int acnN, decimal pwrX, decimal cdlX, decimal spdX)
        {
            _revCandDir = dir; _revCandPct = pct;
            _revCand = new RevDbg { Eff = eff, Strong = strong, Div = div, Abs = abs, Vp = vpoc, Exh = exh, Spd = spd, Imb = imb, Auc = auc, Acn = acn, Pwr = pwr, Cdl = cdl, AcnN = acnN, PwrX = pwrX, CdlX = cdlX, SpdX = spdX };
        }

        // Tape-Speed der Kerze: Trades pro Sekunde (Ticks / Dauer). Historisch
        // berechenbar aus den Candle-Zeiten -> kein Live-Trade-Stream noetig.
        private static decimal BarSpeed(IndicatorCandle c)
        {
            double secs = (c.LastTime - c.Time).TotalSeconds;
            if (secs < 1) secs = 1;   // Floor gegen Division durch ~0
            return c.Ticks / (decimal)secs;
        }

        // 2-Kerzen-Bestaetigung: die Folgekerze muss in Umkehr-Richtung schliessen.
        private bool RevConfirmed(int bar, int dir)
        {
            var cN = GetCandle(bar);
            var cN1 = GetCandle(bar + 1);
            if (cN == null || cN1 == null)
                return false;
            return dir > 0 ? cN1.Close > cN.Close
                 : dir < 0 ? cN1.Close < cN.Close
                 : false;
        }

        // Exhaustion: am Extrem-Preislevel ist das Aggressor-Volumen duenn
        // (am Tief = Bid/Verkaufen, am Hoch = Ask/Kaufen) relativ zum Bar-Schnitt.
        private bool ExhaustionAtExtreme(IndicatorCandle c, bool atLow)
        {
            decimal extPrice = atLow ? decimal.MaxValue : decimal.MinValue;
            decimal aggrAtExt = 0, total = 0;
            int n = 0;
            foreach (var pv in c.GetAllPriceLevels())
            {
                total += pv.Volume;
                n++;
                if (atLow)
                {
                    if (pv.Price < extPrice) { extPrice = pv.Price; aggrAtExt = pv.Bid; }
                }
                else
                {
                    if (pv.Price > extPrice) { extPrice = pv.Price; aggrAtExt = pv.Ask; }
                }
            }
            if (n == 0)
                return false;
            decimal avg = total / n;
            return avg > 0 && aggrAtExt <= _revExhFactor * avg;
        }

        // Absorption Count: Anzahl Level mit absorbiertem Gegen-Aggressor-Delta
        // (>= Frac * staerkstem Level). Long-Umkehr -> absorbierte VERKAEUFER (d<0), Short -> KAEUFER (d>0).
        private int AbsorbedLevelCount(IndicatorCandle c, int dir, decimal mld)
        {
            if (mld == 0m) return 0;
            decimal thr = _revAcntFrac * Math.Abs(mld);
            if (thr <= 0m) return 0;
            int n = 0;
            foreach (var pv in c.GetAllPriceLevels())
            {
                decimal d = pv.Ask - pv.Bid;          // > 0 = Kaeufer-Aggressor
                if (dir > 0 && d <= -thr) n++;
                else if (dir < 0 && d >= thr) n++;
            }
            return n;
        }

        // Volume Power Bar: Volumen >= Mult * Ø-Bar-Volumen UND Kerzenkoerper in Umkehr-Richtung.
        private bool IsPowerBar(IndicatorCandle c, int dir, decimal avgVol)
        {
            if (avgVol <= 0m) return false;
            bool bigVol = c.Volume >= _revPwrVolMult * avgVol;
            bool body = dir > 0 ? c.Close > c.Open : c.Close < c.Open;
            return bigVol && body;
        }

        // Harte Test-Filter auf die Roh-Kandidatin. Beide nutzen nur Daten des Signal-Bars
        // (kein Look-ahead) -> Ergebnis bleibt ehrlich vergleichbar.
        private bool PassesRevFilters(IndicatorCandle c, int rev, RevDbg dbg)
        {
            if (_revFilterAcnt && dbg.AcnN < _revAcntMin)
                return false;
            if (_revFilterKl)
            {
                decimal extreme = rev > 0 ? c.Low : c.High;   // Long-Umkehr am Tief, Short am Hoch
                if (!NearestKeyLevel(extreme).HasValue)
                    return false;
            }
            return true;
        }

        // Emittiert eine (ggf. bestaetigte) Umkehr fuer Bar b: Cooldown, Marker, Backtest, Position-Tool,
        // Alarm, und merkt sie fuer die Bridge (_freshRev). Zentral -> confirm/no-confirm nutzen denselben Pfad.
        private void EmitReversal(int b, int rev, RevDbg dbg)
        {
            if (rev != 0 && _lastRevBar >= 0 && _revCooldownBars > 0 && (b - _lastRevBar) <= _revCooldownBars)
                rev = 0;   // Cooldown
            SetRevSignal(b, rev);
            if (rev == 0)
                return;
            _lastRevBar = b;
            _revDbgByBar[b] = dbg;   // Treiber-Aufschluesselung fuer die Hover-Diagnose
            _freshRev = rev;         // fuer die Bridge in diesem Bar
            var cb = GetCandle(b);
            if (cb != null)
            {
                AddBacktestPending(b, cb, rev, dbg);
                if (_posSource == PosSource.Reversal) AddPosPending(b, cb, rev);
                if (_histDone && _alertOnReversal) FireReversalAlert(b, rev, cb);
            }
        }

        // Loest einen ATAS-Alarm aus (geht automatisch durch die konfigurierte
        // Telegram-Anbindung). rev > 0 = Long-Umkehr, rev < 0 = Short-Umkehr.
        private void FireReversalAlert(int bar, int rev, IndicatorCandle c)
        {
            if (rev > 0 && !_alertLong) return;
            if (rev < 0 && !_alertShort) return;

            string dir = rev > 0 ? "LONG-Umkehr ▲" : "SHORT-Umkehr ▼";
            string instr = InstrumentInfo?.Instrument ?? "";
            string msg = $"Orderflow {instr}: {dir} | Score {Math.Abs(rev)}% | {c?.Close} | {c?.LastTime:HH:mm}";

            // Defensiv: AlertsEnabled direkt vor dem Senden setzen (falls beim Laden
            // ueberschrieben). Einfache Overload (ohne Color) -> kein WPF noetig.
            AlertsEnabled = true;
            try { AddAlert(_alertSound, msg); }
            catch { /* Alarm darf nie die Berechnung stoppen */ }
        }
    }
}
