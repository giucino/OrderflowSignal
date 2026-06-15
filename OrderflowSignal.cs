using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace OrderflowSignal
{
    [DisplayName("Orderflow Signal (Multi-Condition)")]
    [HelpLink("https://giucino.github.io/OrderflowSignal/OrderflowSignal_Doku.html")]
    [Description("Stufe 2 / Trigger — verrechnet mehrere Orderflow-Bedingungen (Delta, " +
                 "Relatives Volumen, Absorption, VWAP-Lage) zu einem richtungsgewichteten " +
                 "Bull/Bear-Score. Marker an der Kerze bei Konfluenz + HUD. Fuer Tick-Charts " +
                 "(500/900T) und Zeitcharts (M5). Schwellen relativ -> instrument-/TF-agnostisch. " +
                 "Rein informativ, kein Entry-Signal.")]
    public class OrderflowSignal : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Allgemein
        // ─────────────────────────────────────────────────────────────────
        // Anzahl der Bars fuer die gleitenden Durchschnitte (Volumen, |Delta|,
        // Range). Alle Schwellen sind relativ zu diesem Baseline -> der Indikator
        // ist dadurch instrument- und timeframe-unabhaengig.
        private int _lookback = 20;

        // Mindest-Gewichtssumme auf der dominanten Seite, damit ein Marker feuert.
        // Bei Default-Gewichten (Summe ~100) entspricht 50 grob "Mehrheit der
        // Bedingungen zeigt in eine Richtung".
        private int _signalThreshold = 50;

        // Mindestabstand (in Bars) zwischen zwei Markern -> Rausch-Bremse auf
        // schnellen Tick-Charts. 0 = aus.
        private int _signalCooldownBars = 3;

        private bool _showHud = true;
        private bool _showMarkers = true;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Bedingungen (je: aktiv? + Gewicht + Schwellen)
        // ─────────────────────────────────────────────────────────────────
        // 1) Delta signifikant. Richtung = Vorzeichen des Deltas.
        private bool _deltaEnabled = true;
        private int _deltaWeight = 30;
        private decimal _deltaFactor = 1.5m;   // |Delta| >= Faktor * Ø|Delta|
        private decimal _deltaMinAbs = 0m;     // zusaetzlicher absoluter Floor (0 = aus)

        // 2) Relatives Volumen. Richtung = Kerzenkoerper (Close vs Open).
        private bool _volEnabled = true;
        private int _volWeight = 20;
        private decimal _volFactor = 1.5m;     // Volume >= Faktor * ØVolume

        // 3) Absorption: viel Volumen, wenig Weg. Richtung = REVERSAL gegen den
        //    Aggressor (positives Delta absorbiert -> bearish und umgekehrt).
        private bool _absEnabled = true;
        private int _absWeight = 25;
        private decimal _absVolFactor = 1.5m;     // Volume >= Faktor * ØVolume
        private decimal _absRangeFactor = 0.7m;   // Range <= Faktor * ØRange

        // 4) VWAP-Lage als Bias. Richtung = Close ueber/unter Session-VWAP.
        private bool _vwapEnabled = true;
        private int _vwapWeight = 25;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        private int _fontSize = 14;
        private int _offsetX = 20;
        private int _offsetY = 20;
        private bool _topLeft = false;            // Default oben rechts (HUDs links sind belegt)
        private int _markerTickOffset = 4;        // Abstand Marker<->Kerze in Ticks

        private Color _colorBull = Color.FromArgb(230, 50, 205, 80);
        private Color _colorBear = Color.FromArgb(230, 225, 60, 60);
        private Color _colorNeutral = Color.FromArgb(230, 200, 170, 60);
        private Color _colorBackground = Color.FromArgb(180, 18, 20, 26);
        private Color _colorText = Color.FromArgb(235, 220, 225, 235);
        private Color _colorWarn = Color.FromArgb(235, 235, 150, 45);

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        // Session-VWAP-Akkumulation der ABGESCHLOSSENEN Bars (Reset je Session).
        private decimal _cumPv;   // Sum(BarVWAP * BarVolume)
        private decimal _cumVol;  // Sum(BarVolume)

        // Bis zu welchem Bar dauerhaft eingerechnet wurde (jeder Bar genau einmal).
        private int _lastProcessedBar = -1;

        // Pro Bar gespeicherte Signalrichtung: +1 Long, -1 Short, 0 keins.
        private readonly List<int> _signals = new();

        // Cooldown-Tracking ueber die ABGESCHLOSSENEN Signale.
        private int _lastSignalBar = -1;

        // Vermeidet unnoetiges Neuzeichnen.
        private string _lastRenderKey = "";

        // Gerenderte HUD-Werte (im OnCalculate berechnet, im OnRender gezeichnet).
        private int _hudBull, _hudBear, _hudSignal;
        private string _hudTags = "";
        private string _hudWarn = "";
        private string _chartLabel = "";

        private RenderFont _font = null!;
        private RenderFont _fontBig = null!;
        private RenderFont _fontMarker = null!;

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Allgemein
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Lookback (Bars)", GroupName = "Allgemein", Order = 1,
            Description = "Anzahl Bars fuer die gleitenden Durchschnitte. Alle Schwellen sind " +
                          "relativ dazu -> instrument-/timeframe-unabhaengig.")]
        [Range(3, 500)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(3, value); RecalculateValues(); } }

        [Display(Name = "Signal-Schwelle (Gewichtspunkte)", GroupName = "Allgemein", Order = 2,
            Description = "Mindest-Gewichtssumme der dominanten Seite, damit ein Marker feuert. " +
                          "Bei Default-Gewichten (Summe ~100) ist 50 = Mehrheit.")]
        [Range(0, 300)]
        public int SignalThreshold { get => _signalThreshold; set { _signalThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Display(Name = "Signal-Cooldown (Bars)", GroupName = "Allgemein", Order = 3,
            Description = "Mindestabstand zwischen Markern. Rausch-Bremse fuer Tick-Charts. 0 = aus.")]
        [Range(0, 100)]
        public int SignalCooldownBars { get => _signalCooldownBars; set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); } }

        [Display(Name = "HUD anzeigen", GroupName = "Allgemein", Order = 4)]
        public bool ShowHud { get => _showHud; set { _showHud = value; RedrawChart(); } }

        [Display(Name = "Marker anzeigen", GroupName = "Allgemein", Order = 5)]
        public bool ShowMarkers { get => _showMarkers; set { _showMarkers = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Delta
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Delta aktiv", GroupName = "Bedingung: Delta", Order = 10)]
        public bool DeltaEnabled { get => _deltaEnabled; set { _deltaEnabled = value; RecalculateValues(); } }

        [Display(Name = "Delta Gewicht", GroupName = "Bedingung: Delta", Order = 11)]
        [Range(0, 100)]
        public int DeltaWeight { get => _deltaWeight; set { _deltaWeight = value; RecalculateValues(); } }

        [Display(Name = "Delta Faktor (x Ø|Delta|)", GroupName = "Bedingung: Delta", Order = 12,
            Description = "Signifikant, wenn |Delta| >= Faktor * Durchschnitt(|Delta|).")]
        [Range(0.1, 20.0)]
        public decimal DeltaFactor { get => _deltaFactor; set { _deltaFactor = value; RecalculateValues(); } }

        [Display(Name = "Delta Mindest-Absolut", GroupName = "Bedingung: Delta", Order = 13,
            Description = "Zusaetzlicher absoluter Floor in Kontrakten (0 = aus). Filtert tote Bars.")]
        [Range(0, 100000)]
        public decimal DeltaMinAbs { get => _deltaMinAbs; set { _deltaMinAbs = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Relatives Volumen
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Volumen aktiv", GroupName = "Bedingung: Volumen", Order = 20)]
        public bool VolEnabled { get => _volEnabled; set { _volEnabled = value; RecalculateValues(); } }

        [Display(Name = "Volumen Gewicht", GroupName = "Bedingung: Volumen", Order = 21)]
        [Range(0, 100)]
        public int VolWeight { get => _volWeight; set { _volWeight = value; RecalculateValues(); } }

        [Display(Name = "Volumen Faktor (x ØVol)", GroupName = "Bedingung: Volumen", Order = 22,
            Description = "Spike, wenn Volume >= Faktor * Durchschnittsvolumen. Richtung = Kerzenkoerper.")]
        [Range(0.1, 20.0)]
        public decimal VolFactor { get => _volFactor; set { _volFactor = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Absorption
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Absorption aktiv", GroupName = "Bedingung: Absorption", Order = 30)]
        public bool AbsEnabled { get => _absEnabled; set { _absEnabled = value; RecalculateValues(); } }

        [Display(Name = "Absorption Gewicht", GroupName = "Bedingung: Absorption", Order = 31)]
        [Range(0, 100)]
        public int AbsWeight { get => _absWeight; set { _absWeight = value; RecalculateValues(); } }

        [Display(Name = "Absorption Vol-Faktor (x ØVol)", GroupName = "Bedingung: Absorption", Order = 32,
            Description = "Hohes Volumen: Volume >= Faktor * ØVolume.")]
        [Range(0.1, 20.0)]
        public decimal AbsVolFactor { get => _absVolFactor; set { _absVolFactor = value; RecalculateValues(); } }

        [Display(Name = "Absorption Range-Faktor (x ØRange)", GroupName = "Bedingung: Absorption", Order = 33,
            Description = "Kleiner Weg: Range <= Faktor * ØRange. Richtung = Reversal gegen den Aggressor.")]
        [Range(0.05, 5.0)]
        public decimal AbsRangeFactor { get => _absRangeFactor; set { _absRangeFactor = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — VWAP
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "VWAP aktiv", GroupName = "Bedingung: VWAP", Order = 40)]
        public bool VwapEnabled { get => _vwapEnabled; set { _vwapEnabled = value; RecalculateValues(); } }

        [Display(Name = "VWAP Gewicht", GroupName = "Bedingung: VWAP", Order = 41,
            Description = "Bias: Close ueber Session-VWAP = bullish, darunter = bearish. " +
                          "VWAP ankert taeglich (IsNewSession).")]
        [Range(0, 100)]
        public int VwapWeight { get => _vwapWeight; set { _vwapWeight = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Schriftgroesse", GroupName = "Darstellung", Order = 50)]
        [Range(8, 30)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 30); BuildFonts(); RedrawChart(); } }

        [Display(Name = "Oben Links (aus = Oben Rechts)", GroupName = "Darstellung", Order = 51)]
        public bool TopLeft { get => _topLeft; set { _topLeft = value; RedrawChart(); } }

        [Display(Name = "Abstand X (px)", GroupName = "Darstellung", Order = 52)]
        [Range(0, 600)]
        public int OffsetX { get => _offsetX; set { _offsetX = value; RedrawChart(); } }

        [Display(Name = "Abstand Y (px)", GroupName = "Darstellung", Order = 53)]
        [Range(0, 600)]
        public int OffsetY { get => _offsetY; set { _offsetY = value; RedrawChart(); } }

        [Display(Name = "Marker-Abstand (Ticks)", GroupName = "Darstellung", Order = 54)]
        [Range(0, 100)]
        public int MarkerTickOffset { get => _markerTickOffset; set { _markerTickOffset = value; RedrawChart(); } }

        [Display(Name = "Farbe Bull", GroupName = "Farben", Order = 60)]
        public Color ColorBull { get => _colorBull; set { _colorBull = value; RedrawChart(); } }

        [Display(Name = "Farbe Bear", GroupName = "Farben", Order = 61)]
        public Color ColorBear { get => _colorBear; set { _colorBear = value; RedrawChart(); } }

        [Display(Name = "Farbe Neutral", GroupName = "Farben", Order = 62)]
        public Color ColorNeutral { get => _colorNeutral; set { _colorNeutral = value; RedrawChart(); } }

        [Display(Name = "Hintergrund", GroupName = "Farben", Order = 63)]
        public Color ColorBackground { get => _colorBackground; set { _colorBackground = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public OrderflowSignal() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

            // Pflicht fuer persistentes Custom-Drawing: ohne dies zeichnet ATAS nur
            // bei DrawingLayouts.LatestBar -> Marker/HUD verschwinden beim Wegnavigieren.
            SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);

            BuildFonts();
        }

        private void BuildFonts()
        {
            _font = new RenderFont("Consolas", _fontSize);
            _fontBig = new RenderFont("Consolas", _fontSize + 4, FontStyle.Bold);
            _fontMarker = new RenderFont("Consolas", _fontSize + 6, FontStyle.Bold);
        }

        // ─────────────────────────────────────────────────────────────────
        //  HAUPTBERECHNUNG
        // ─────────────────────────────────────────────────────────────────
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
                ResetState();

            // Abgeschlossene Bars genau einmal verarbeiten. Der sich bildende Bar
            // (CurrentBar - 1) wird hier NICHT dauerhaft eingerechnet, sondern live
            // in ComputeLive (kann bis zum Schluss repainten).
            int lastClosed = CurrentBar - 2;
            while (_lastProcessedBar < lastClosed)
            {
                _lastProcessedBar++;
                ProcessClosedBar(_lastProcessedBar);
            }

            if (bar != CurrentBar - 1)
                return;

            ComputeLive();

            string key = $"{_hudBull}|{_hudBear}|{_hudSignal}|{_hudTags}|{_hudWarn}|{_chartLabel}|{CurrentBar}";
            if (key != _lastRenderKey)
            {
                _lastRenderKey = key;
                RedrawChart();
            }
        }

        private void ResetState()
        {
            _cumPv = 0;
            _cumVol = 0;
            _lastProcessedBar = -1;
            _signals.Clear();
            _lastSignalBar = -1;
            _hudBull = _hudBear = _hudSignal = 0;
            _hudTags = "";
            _hudWarn = "";
            _chartLabel = "";
            _lastRenderKey = "";
        }

        // Rechnet einen ABGESCHLOSSENEN Bar dauerhaft ein: Session-VWAP fortschreiben,
        // Bedingungen auswerten, Signal (mit Cooldown) committen.
        private void ProcessClosedBar(int bar)
        {
            var candle = GetCandle(bar);
            if (candle == null)
                return;

            if (IsNewSession(bar))
            {
                _cumPv = 0;
                _cumVol = 0;
            }

            _cumPv += BarPriceForVwap(candle) * candle.Volume;
            _cumVol += candle.Volume;
            decimal vwap = _cumVol > 0 ? _cumPv / _cumVol : 0;

            var o = EvaluateBar(bar, vwap, _cumVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);

            // Cooldown gegen das letzte committed Signal.
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;

            SetSignal(bar, sig);
            if (sig != 0)
                _lastSignalBar = bar;
        }

        // Live-Auswertung des sich bildenden Bars (CurrentBar - 1). Setzt das
        // Signal an diesem Bar (repaint-faehig) und die HUD-Werte; veraendert
        // _lastSignalBar NICHT (Bar ist noch nicht final).
        private void ComputeLive()
        {
            int last = CurrentBar - 1;
            var f = GetCandle(last);
            if (f == null)
                return;

            // Session-VWAP inkl. forming Bar (auf Kopie der Akkumulation).
            decimal basePv = _cumPv, baseVol = _cumVol;
            if (IsNewSession(last)) { basePv = 0; baseVol = 0; }
            basePv += BarPriceForVwap(f) * f.Volume;
            baseVol += f.Volume;
            decimal liveVwap = baseVol > 0 ? basePv / baseVol : 0;

            var o = EvaluateBar(last, liveVwap, baseVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (last - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;
            SetSignal(last, sig);

            _hudBull = o.Bull;
            _hudBear = o.Bear;
            _hudSignal = sig;
            _hudTags = $"Δ{Arrow(o.DirDelta)} Vol{Arrow(o.DirVol)} Abs{Arrow(o.DirAbs)} VW{Arrow(o.DirVwap)}";
            _hudWarn = BuildWarning();
            _chartLabel = BuildChartLabel();
        }

        // ─────────────────────────────────────────────────────────────────
        //  BEDINGUNGS-AUSWERTUNG
        // ─────────────────────────────────────────────────────────────────
        private struct EvalOut
        {
            public int Bull, Bear;
            public int DirDelta, DirVol, DirAbs, DirVwap;
        }

        private EvalOut EvaluateBar(int bar, decimal vwap, bool vwapValid)
        {
            var o = new EvalOut();
            var c = GetCandle(bar);
            if (c == null)
                return o;

            // Baseline aus den vorangehenden Bars (ohne den aktuellen).
            if (!ComputeAverages(bar, out decimal avgVol, out decimal avgAbsDelta, out decimal avgRange))
                return o;

            // 1) Delta signifikant.
            if (_deltaEnabled && avgAbsDelta > 0)
            {
                decimal ad = Math.Abs(c.Delta);
                if (ad >= _deltaFactor * avgAbsDelta && ad >= _deltaMinAbs)
                {
                    o.DirDelta = Math.Sign(c.Delta);
                    Accumulate(ref o, o.DirDelta, _deltaWeight);
                }
            }

            // 2) Relatives Volumen (Richtung = Kerzenkoerper).
            if (_volEnabled && avgVol > 0 && c.Volume >= _volFactor * avgVol)
            {
                o.DirVol = c.Close > c.Open ? 1 : c.Close < c.Open ? -1 : 0;
                Accumulate(ref o, o.DirVol, _volWeight);
            }

            // 3) Absorption (Reversal gegen den Aggressor).
            if (_absEnabled && avgVol > 0 && avgRange > 0)
            {
                decimal range = c.High - c.Low;
                if (c.Volume >= _absVolFactor * avgVol && range <= _absRangeFactor * avgRange)
                {
                    o.DirAbs = -Math.Sign(c.Delta);
                    Accumulate(ref o, o.DirAbs, _absWeight);
                }
            }

            // 4) VWAP-Lage.
            if (_vwapEnabled && vwapValid && vwap > 0)
            {
                o.DirVwap = c.Close > vwap ? 1 : c.Close < vwap ? -1 : 0;
                Accumulate(ref o, o.DirVwap, _vwapWeight);
            }

            return o;
        }

        private static void Accumulate(ref EvalOut o, int dir, int weight)
        {
            if (dir > 0) o.Bull += weight;
            else if (dir < 0) o.Bear += weight;
        }

        private int DetermineSignal(int bull, int bear)
        {
            if (bull >= _signalThreshold && bull > bear) return 1;
            if (bear >= _signalThreshold && bear > bull) return -1;
            return 0;
        }

        // Gleitende Durchschnitte ueber [bar-Lookback, bar-1]. false, wenn nicht
        // genug Historie vorhanden ist (kein Signal in der Anlaufphase).
        private bool ComputeAverages(int bar, out decimal avgVol, out decimal avgAbsDelta, out decimal avgRange)
        {
            avgVol = avgAbsDelta = avgRange = 0;
            int start = bar - _lookback;
            if (start < 0)
                return false;

            decimal sumVol = 0, sumAbsDelta = 0, sumRange = 0;
            for (int i = start; i < bar; i++)
            {
                var c = GetCandle(i);
                if (c == null)
                    return false;
                sumVol += c.Volume;
                sumAbsDelta += Math.Abs(c.Delta);
                sumRange += c.High - c.Low;
            }

            avgVol = sumVol / _lookback;
            avgAbsDelta = sumAbsDelta / _lookback;
            avgRange = sumRange / _lookback;
            return true;
        }

        // Bar-Preis fuer die VWAP-Akkumulation: bevorzugt der echte Bar-VWAP,
        // sonst Typical Price als Fallback.
        private static decimal BarPriceForVwap(IndicatorCandle c)
            => c.VWAP > 0 ? c.VWAP : (c.High + c.Low + c.Close) / 3m;

        // ─────────────────────────────────────────────────────────────────
        //  SIGNAL-SPEICHER
        // ─────────────────────────────────────────────────────────────────
        private void SetSignal(int bar, int v)
        {
            while (_signals.Count <= bar)
                _signals.Add(0);
            _signals[bar] = v;
        }

        private int GetSignal(int bar)
            => bar >= 0 && bar < _signals.Count ? _signals[bar] : 0;

        // ─────────────────────────────────────────────────────────────────
        //  HUD-HELFER
        // ─────────────────────────────────────────────────────────────────
        private static string Arrow(int dir) => dir > 0 ? "▲" : dir < 0 ? "▼" : "·";

        private string BuildChartLabel()
        {
            var ct = ChartInfo?.ChartType ?? "";
            var tf = ChartInfo?.TimeFrame ?? "";
            string s = (ct + " " + tf).Trim();
            return s.Length > 0 ? $"({s})" : "";
        }

        // Warnt, wenn der Charttyp eine Bedingung ungueltig macht.
        private string BuildWarning()
        {
            var ct = (ChartInfo?.ChartType ?? "").ToLowerInvariant();
            if (_volEnabled && ct.Contains("volume"))
                return "! Volumen-Bars: RelVol ungueltig";
            if (_absEnabled && (ct.Contains("range") || ct.Contains("renko")))
                return "! Range/Renko: Absorption ungueltig";
            return "";
        }

        // ─────────────────────────────────────────────────────────────────
        //  RENDER
        // ─────────────────────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_font == null)
                return;

            if (_showMarkers)
                DrawMarkers(context);

            if (_showHud)
                DrawHud(context);
        }

        private void DrawMarkers(RenderContext context)
        {
            var pc = ChartInfo?.PriceChartContainer;
            if (pc == null)
                return;

            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * _markerTickOffset;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(CurrentBar - 1, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int s = GetSignal(b);
                if (s == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                decimal price = s > 0 ? c.Low - offset : c.High + offset;
                int x = pc.GetXByBarNumber(b);
                int y = pc.GetYByPrice(price, true);

                string glyph = s > 0 ? "▲" : "▼";
                var col = s > 0 ? _colorBull : _colorBear;
                var sz = context.MeasureString(glyph, _fontMarker);

                // Long-Pfeil unter dem Low (Oberkante an y), Short-Pfeil ueber dem High.
                int drawY = s > 0 ? y : y - sz.Height;
                context.DrawString(glyph, _fontMarker, col, x - sz.Width / 2, drawY);
            }
        }

        private void DrawHud(RenderContext context)
        {
            var sigColor = _hudSignal > 0 ? _colorBull
                         : _hudSignal < 0 ? _colorBear
                         : _colorNeutral;

            string sigText = _hudSignal > 0 ? "LONG" : _hudSignal < 0 ? "SHORT" : "—";
            string header = $"ORDERFLOW {_chartLabel}".Trim();

            var lines = new List<(string text, Color col, RenderFont font)>
            {
                (header, sigColor, _fontBig),
                ($"▲ {_hudBull}    ▼ {_hudBear}", _colorText, _font),
                ($"SIGNAL: {sigText}", sigColor, _font),
                (_hudTags, _colorText, _font),
            };
            if (_hudWarn.Length > 0)
                lines.Add((_hudWarn, _colorWarn, _font));

            int w = 0, h = 0;
            const int padX = 12, padY = 10, lineGap = 4;
            foreach (var ln in lines)
            {
                var sz = context.MeasureString(ln.text, ln.font);
                if (sz.Width > w) w = sz.Width;
                h += sz.Height + lineGap;
            }
            h += padY * 2 - lineGap;
            w += padX * 2;

            int posX = _topLeft ? _offsetX : context.ClipBounds.Width - w - _offsetX;
            int posY = _offsetY;

            var box = new Rectangle(posX, posY, w, h);
            context.FillRectangle(_colorBackground, box);
            context.DrawRectangle(new RenderPen(sigColor, 2), box);
            context.FillRectangle(sigColor, new Rectangle(posX, posY, 4, h));

            int y = posY + padY;
            foreach (var ln in lines)
            {
                var sz = context.MeasureString(ln.text, ln.font);
                context.DrawString(ln.text, ln.font, ln.col, posX + padX, y);
                y += sz.Height + lineGap;
            }
        }
    }
}
