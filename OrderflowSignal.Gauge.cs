using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;

namespace OrderflowSignal
{
    // Confluence-Druck-Gauge: reines LIVE-Messinstrument (-100..+100) mit Komponenten-
    // Zerlegung (Delta, Divergenz, Absorption, Big-Trade-Reaktion) und Orts-Daempfung.
    // KEIN Signalgeber: keine Marker, keine Schwellen, kein Backtest. Der Trader liest
    // den Kontext - das Gauge beantwortet nur: wer drueckt gerade, und wird er absorbiert?
    // Momentum-/Reversal-Logik komplett unberuehrt.
    public partial class OrderflowSignal
    {
        // ── Einstellungen ──
        private bool _gaugeEnabled = false;
        private int _gaugeWDelta = 30, _gaugeWDiv = 20, _gaugeWAbs = 30, _gaugeWBt = 20;
        private int _gaugeDeltaBars = 5;     // Fenster Delta-Druck
        private int _gaugeDivBars = 14;      // Fenster Divergenz
        private int _gaugeBtFollowTicks = 8; // Reaktion, die einen Big Trade als "gelaufen" saettigt
        private int _gaugeBtMemoryBars = 12; // so lange wirkt ein Big Print nach
        private bool _gaugeBalanceDamp = true;
        private int _gaugeX = 20, _gaugeY = 20;

        // ── Laufzeit (nur Anzeige) ──
        private int _gaugeScore, _gD, _gV, _gA, _gB;
        private string _gaugeTag = "";

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Confluence-Gauge anzeigen", GroupName = "Gauge", Order = 900,
            Description = "Live-Druckmesser -100..+100 aus Delta, CVD-Divergenz, Absorption und Big-Trade-Reaktion, mit Orts-Daempfung (Balance). REINE ANZEIGE als Entscheidungshilfe - keine Marker, keine Signale, kein Backtest. Aendert Momentum/Reversal nicht.")]
        public bool GaugeEnabled { get => _gaugeEnabled; set { _gaugeEnabled = value; RedrawChart(); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Gewicht Delta-Druck", GroupName = "Gewichte", Order = 902,
            Description = "Netto-Delta der letzten K Bars, normiert am Kalibrier-Durchschnitt. Default 30.")]
        [Range(0, 100)]
        public int GaugeWDelta { get => _gaugeWDelta; set { _gaugeWDelta = Math.Clamp(value, 0, 100); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Gewicht Divergenz", GroupName = "Gewichte", Order = 903,
            Description = "Preis-Weg vs. CVD-Weg ueber das Divergenz-Fenster; wirkt nur bei Gegenlauf, Richtung = gegen den Preis. Default 20.")]
        [Range(0, 100)]
        public int GaugeWDiv { get => _gaugeWDiv; set { _gaugeWDiv = Math.Clamp(value, 0, 100); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Gewicht Absorption", GroupName = "Gewichte", Order = 904,
            Description = "Footprint-Absorption (Max-Level-Delta >= Kalibrier-Schwelle, Close auf der Gegenseite). Richtung = gegen den Aggressor. Default 30.")]
        [Range(0, 100)]
        public int GaugeWAbs { get => _gaugeWAbs; set { _gaugeWAbs = Math.Clamp(value, 0, 100); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Gewicht Big-Trade-Reaktion", GroupName = "Gewichte", Order = 905,
            Description = "Reaktion des Preises auf die Big Prints der letzten Bars: Follow-through = Bestaetigung, Gegenhalten = Absorption (zaehlt 1,5x). Braucht aktivierte Big-Trade-Levels. Default 20.")]
        [Range(0, 100)]
        public int GaugeWBt { get => _gaugeWBt; set { _gaugeWBt = Math.Clamp(value, 0, 100); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Delta-Fenster (Bars)", GroupName = "Fenster", Order = 910,
            Description = "So viele Bars fliessen in den Delta-Druck ein (inkl. Live-Bar). Default 5.")]
        [Range(1, 50)]
        public int GaugeDeltaBars { get => _gaugeDeltaBars; set { _gaugeDeltaBars = Math.Clamp(value, 1, 50); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Divergenz-Fenster (Bars)", GroupName = "Fenster", Order = 911,
            Description = "Fenster fuer Preis-Weg vs. CVD-Weg. Default 14.")]
        [Range(3, 200)]
        public int GaugeDivBars { get => _gaugeDivBars; set { _gaugeDivBars = Math.Clamp(value, 3, 200); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "BT: Follow-Through (Ticks)", GroupName = "Fenster", Order = 912,
            Description = "Ab so viel Preis-Reaktion gilt ein Big Print als voll gelaufen (saettigt den Beitrag). Default 8.")]
        [Range(1, 200)]
        public int GaugeBtFollowTicks { get => _gaugeBtFollowTicks; set { _gaugeBtFollowTicks = Math.Max(1, value); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "BT: Gedaechtnis (Bars)", GroupName = "Fenster", Order = 913,
            Description = "So viele Bars wirkt ein Big Print nach (linear abklingend). Default 12.")]
        [Range(1, 100)]
        public int GaugeBtMemoryBars { get => _gaugeBtMemoryBars; set { _gaugeBtMemoryBars = Math.Max(1, value); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Balance-Daempfung", GroupName = "Ort", Order = 920,
            Description = "In einer laufenden Balance des Range-Detektors wird der Score halbiert (Druck verpufft dort haeufig) und 'IN BALANCE' angezeigt. Default an.")]
        public bool GaugeBalanceDamp { get => _gaugeBalanceDamp; set { _gaugeBalanceDamp = value; } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Position X", GroupName = "Darstellung", Order = 930)]
        [Range(0, 4000)]
        public int GaugeX { get => _gaugeX; set { _gaugeX = Math.Max(0, value); RedrawChart(); } }

        [Tab(TabName = "Gauge", TabOrder = 8)]
        [Display(Name = "Position Y", GroupName = "Darstellung", Order = 931)]
        [Range(0, 4000)]
        public int GaugeY { get => _gaugeY; set { _gaugeY = Math.Max(0, value); RedrawChart(); } }

        // Pro Tick aus ComputeLive aufgerufen. Nutzt nur vorhandene Metriken - reine Messung.
        private void ComputeGauge(int last, IndicatorCandle c, decimal signedMld, decimal liveCumDelta)
        {
            _gD = _gV = _gA = _gB = 0;
            _gaugeTag = "";

            // Ø-|Delta| aus dem Kalibrier-Fenster als Normierungsgroesse.
            decimal avgAbsD = 0m;
            int n0 = Math.Max(0, last - _lookback), cnt = 0;
            for (int i = n0; i < last && i < _absDArr.Count; i++) { avgAbsD += _absDArr[i]; cnt++; }
            avgAbsD = cnt > 0 ? avgAbsD / cnt : 0m;

            // (1) Delta-Druck: Netto-Delta der letzten K Bars (inkl. Live-Bar).
            if (avgAbsD > 0m)
            {
                decimal sum = 0m;
                for (int i = Math.Max(0, last - _gaugeDeltaBars + 1); i <= last; i++)
                {
                    var ci = GetCandle(i);
                    if (ci != null) sum += ci.Delta;
                }
                decimal d = sum / (_gaugeDeltaBars * avgAbsD);
                _gD = (int)(Math.Clamp(d, -1m, 1m) * 100m);
            }

            // (2) Divergenz: Preis-Weg vs. CVD-Weg ueber W Bars. Nur bei Gegenlauf, gegen den Preis.
            int w0 = last - _gaugeDivBars;
            if (avgAbsD > 0m && w0 >= 0 && w0 < _cumDeltaArr.Count)
            {
                var cw = GetCandle(w0);
                if (cw != null)
                {
                    decimal priceChg = c.Close - cw.Close;
                    decimal cvdChg = liveCumDelta - _cumDeltaArr[w0];
                    if (priceChg != 0m && cvdChg != 0m && Math.Sign(priceChg) != Math.Sign(cvdChg))
                    {
                        decimal mag = Math.Min(1m, Math.Abs(cvdChg) / (0.5m * _gaugeDivBars * avgAbsD));
                        _gV = (int)(-Math.Sign(priceChg) * mag * 100m);
                    }
                }
            }

            // (3) Absorption: Live-Bar zuerst, sonst letzter geschlossener Bar (gedaempft).
            if (Thresholds(last, out _, out _, out decimal absThr) && absThr > 0m)
            {
                int a = AbsorptionScore(c, signedMld, absThr);
                if (a == 0 && last - 1 >= 0)
                {
                    var cp = GetCandle(last - 1);
                    if (cp != null)
                        a = (int)(AbsorptionScore(cp, MaxLevelDeltaSigned(cp), absThr) * 0.6m);
                }
                _gA = a;
            }

            // (4) Big-Trade-Reaktion: Preis-Antwort auf die Prints der letzten M Bars.
            //     Follow-through bestaetigt (x1.0), Gegenhalten = Absorption (x1.5). Linear abklingend.
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (_bigEnabled && tick > 0m)
            {
                decimal acc = 0m;
                lock (_bigLock)
                {
                    foreach (var lv in _bigLevels)
                    {
                        int age = last - lv.Bar;
                        if (age < 0 || age > _gaugeBtMemoryBars) continue;
                        decimal r = Math.Clamp((c.Close - lv.Price) / (_gaugeBtFollowTicks * tick), -1m, 1m);
                        if (r == 0m) continue;
                        bool absorbed = Math.Sign(r) == -lv.Dir;          // Reaktion GEGEN den Aggressor
                        decimal wgt = absorbed ? 1.5m : 1.0m;
                        decimal decay = 1m - (decimal)age / (_gaugeBtMemoryBars + 1);
                        acc += r * wgt * decay;
                    }
                }
                _gB = (int)(Math.Clamp(acc / 2m, -1m, 1m) * 100m);        // ~2 volle Events saettigen
            }

            // Aggregation + Orts-Daempfung.
            int totalW = _gaugeWDelta + _gaugeWDiv + _gaugeWAbs + _gaugeWBt;
            if (totalW <= 0) totalW = 1;
            decimal s = (_gaugeWDelta * _gD + _gaugeWDiv * _gV + _gaugeWAbs * _gA + _gaugeWBt * _gB) / (decimal)totalW;

            if (_gaugeBalanceDamp && _detectorEnabled && _candActive
                && (last - _candStart) >= _detectorMinBars
                && c.Close <= _candHi && c.Close >= _candLo)
            {
                s *= 0.5m;
                _gaugeTag = "IN BALANCE";
            }

            var kl = NearestKeyLevel(c.Close);
            if (kl.HasValue)
                _gaugeTag = (_gaugeTag.Length > 0 ? _gaugeTag + "  " : "") + "@KL " + kl.Value.Label;

            _gaugeScore = (int)Math.Clamp(s, -100m, 100m);
        }

        // Absorptions-Beitrag einer Kerze: Aggressor-Level ueber Schwelle + Close auf der Gegenseite.
        private static int AbsorptionScore(IndicatorCandle c, decimal mld, decimal absThr)
        {
            if (Math.Abs(mld) < absThr) return 0;
            decimal mid = (c.High + c.Low) / 2m;
            decimal ratio = Math.Min(1m, Math.Abs(mld) / (2m * absThr));   // 2x Schwelle = Saettigung
            if (mld < 0 && c.Close >= mid) return (int)(ratio * 100m);     // Verkaeufer absorbiert -> bullisch
            if (mld > 0 && c.Close <= mid) return (int)(-ratio * 100m);    // Kaeufer absorbiert -> bearish
            return 0;
        }

        // Gauge-Panel zeichnen (aus OnRender). Nutzt die gecachten Werte aus ComputeGauge.
        private void DrawGauge(RenderContext context)
        {
            if (!_gaugeEnabled || _font == null)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;
            var region = cont.Region;

            int x = region.Left + _gaugeX, y = region.Top + _gaugeY;
            int w = 250, pad = 8, barH = 14;
            var lineSz = context.MeasureString("Ag", _font);
            int lh = lineSz.Height + 2;
            int h = pad + lh + barH + 6 + lh + (_gaugeTag.Length > 0 ? lh : 0) + pad;

            context.FillRectangle(_colorBackground, new Rectangle(x, y, w, h));

            // Titel + Score
            Color scoreCol = _gaugeScore > 15 ? _colorBull : _gaugeScore < -15 ? _colorBear : _colorNeutral;
            context.DrawString($"DRUCK  {_gaugeScore:+0;-0;0}", _font, scoreCol, x + pad, y + pad);

            // Balken: Mitte = 0, rechts gruen, links rot.
            int by = y + pad + lh, bw = w - 2 * pad, cx = x + pad + bw / 2;
            context.FillRectangle(Color.FromArgb(60, 120, 130, 150), new Rectangle(x + pad, by, bw, barH));
            int fill = (int)(Math.Abs(_gaugeScore) / 100m * (bw / 2m));
            if (_gaugeScore > 0)
                context.FillRectangle(_colorBull, new Rectangle(cx, by, fill, barH));
            else if (_gaugeScore < 0)
                context.FillRectangle(_colorBear, new Rectangle(cx - fill, by, fill, barH));
            context.FillRectangle(_colorText, new Rectangle(cx, by - 1, 1, barH + 2));   // Null-Marke

            // Komponenten-Zeile
            string comp = $"Δ{_gD:+0;-0;0}  Div{_gV:+0;-0;0}  Abs{_gA:+0;-0;0}  BT{_gB:+0;-0;0}";
            context.DrawString(comp, _font, _colorDim, x + pad, by + barH + 6);

            // Orts-Tag
            if (_gaugeTag.Length > 0)
                context.DrawString(_gaugeTag, _font, _colorWarn, x + pad, by + barH + 6 + lh);
        }
    }
}
