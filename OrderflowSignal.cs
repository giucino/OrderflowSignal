using System;
using System.Collections.Concurrent;
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
                 "Volumen, Footprint-Absorption, VWAP-Lage) zu einem richtungsgewichteten " +
                 "Bull/Bear-Score. Schwellen per Perzentil-Auto-Kalibrierung (rollend, mit " +
                 "Freeze) -> instrument-/TF-agnostisch. Absorption footprint-basiert -> laeuft " +
                 "auf Tick UND Renko. Marker an der Kerze + HUD. Rein informativ.")]
    public class OrderflowSignal : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Allgemein
        // ─────────────────────────────────────────────────────────────────
        // Kalibrierungs-Fenster: Anzahl Bars, ueber die die Perzentil-Schwellen
        // bestimmt werden (= semaPHoreks "Last N Bars").
        private int _lookback = 50;

        private int _signalThreshold = 50;   // Mindest-Gewichtssumme der dominanten Seite
        private int _signalCooldownBars = 3; // Mindestabstand zwischen Markern (0 = aus)

        private bool _showHud = true;
        private bool _showMarkers = true;
        private bool _showCalibration = true;
        private bool _freezeCalibration = false;

        // Nur Marker mit Score >= diesem Wert zeichnen. 60 = mind. 3 Bedingungen
        // ausgerichtet (filtert die schwachen 50/55-Zweier-Konfluenzen weg).
        private int _minMarkerScore = 60;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Kalibrierung (Perzentil)
        // ─────────────────────────────────────────────────────────────────
        // Globaler Perzentil-Wert (Basic). Schwelle = dieses Perzentil der letzten
        // N Bars; ein Bar ist "aktiv", wenn seine Metrik >= Schwelle (also in den
        // oberen (100 - P) % liegt). Default 95 = semaPHoreks erprobte Selektivitaet.
        private int _globalPercentile = 95;

        // Advanced: pro Bedingung eigenen Perzentil-Wert verwenden.
        private bool _useAdvancedPercentiles = false;
        private int _volPercentile = 85;
        private int _deltaPercentile = 85;
        private int _absPercentile = 85;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Bedingungen (aktiv? + Gewicht)
        // ─────────────────────────────────────────────────────────────────
        // Gewichte summieren sich als Default auf 100; der Score wird auf den
        // Anteil der AKTIVEN Gewichte normiert (0-100 %), damit Filter/Schwelle
        // stabil bleiben, egal wie viele Bedingungen aktiv sind.

        // 1) Delta signifikant. Richtung = Vorzeichen des Candle-Deltas.
        private bool _deltaEnabled = true;
        private int _deltaWeight = 20;

        // 2) Volumen-Spike. Richtung = Kerzenkoerper (Close vs Open).
        private bool _volEnabled = true;
        private int _volWeight = 15;

        // 3) Footprint-Absorption: groesstes Level-Delta (Ask-Bid je Preislevel)
        //    ueber der Schwelle -> der Aggressor wurde absorbiert. Richtung =
        //    REVERSAL gegen den Aggressor. Range-unabhaengig -> auch auf Renko.
        private bool _absEnabled = true;
        private int _absWeight = 20;

        // 4) VWAP-Lage als Bias (keine Kalibrierung). Close ueber/unter Session-VWAP.
        private bool _vwapEnabled = true;
        private int _vwapWeight = 15;

        // 5) Imbalance (diagonal): gestapelte Ask[p] >= Ratio*Bid[p-Tick] (Buy) bzw.
        //    Bid[p] >= Ratio*Ask[p+Tick] (Sell). Richtung = dominante Stapelseite.
        private bool _imbEnabled = true;
        private int _imbWeight = 15;
        private decimal _imbRatio = 2.0m;
        private int _imbMinCount = 3;

        // 6) vPOC-in-Wick: POC im unteren Docht = Kaeufer-Rejection (bullish),
        //    im oberen Docht = Verkaeufer-Rejection (bearish). Location-basiert.
        private bool _vpocEnabled = true;
        private int _vpocWeight = 15;

        // 7) Tape / Big Trades: einzelne Cumulative-Trades >= Mindestgroesse.
        //    Buy = bullish, Sell = bearish. LIVE-ONLY (ab Laden vorwaerts), daher
        //    default AUS. Braucht Tick-Trade-Daten (OnCumulativeTrade).
        private bool _tapeEnabled = false;
        private int _tapeWeight = 15;
        private int _tapeMinContracts = 100;

        // ── REVERSAL-ENGINE ────────────────────────────────────────────────
        // Getrennte Umkehr-Logik (Divergenz, Absorption am Extrem, vPOC-Docht,
        // Exhaustion), nur an lokalen Extrema. Eigener Score + Rauten-Marker.
        private bool _reversalEnabled = true;
        private int _reversalLookback = 14;   // Bars fuer Extrem-/Divergenz-Referenz
        private int _reversalThreshold = 70;  // Mindest-Reversal-Score (%) -> ~3 Bedingungen
        private int _revDivWeight = 30;       // Delta-Divergenz
        private int _revAbsWeight = 30;       // Absorption am Extrem
        private int _revVpocWeight = 20;      // vPOC im Docht
        private int _revExhWeight = 20;       // Exhaustion (duennes Aggressor-Vol)
        // 2-Kerzen-Bestaetigung: Umkehr nur, wenn die Folgekerze in Umkehr-
        // Richtung schliesst (ISA-Prinzip). Default an.
        private bool _revConfirm = true;

        // ── BALANCE-RANGE (Phase 2a) ───────────────────────────────────────
        // Value Area (VAH/VAL/vPOC) ueber ein rollendes Fenster -> zeichnet die
        // aktuelle Balance. (Reversal-Gate an den Raendern folgt in Phase 2b.)
        private bool _rangeEnabled = false;   // rollendes VA-Band: Default AUS (Referenz)
        private int _rangeLookback = 100;     // Bars fuer das Volumen-Profil
        private int _rangeValuePct = 70;      // Value-Area-Anteil (%)
        private bool _rangeExtendRight = true;

        // ── RANGE-DETEKTOR (H-Range-Stil) ──────────────────────────────────
        // Sucht diskrete Konsolidierungen (Balance nach Imbalance) und markiert
        // jede als Box, gefaerbt nach Ausbruchsrichtung.
        private bool _detectorEnabled = true;
        private int _detectorLookback = 500;     // Bars rueckwaerts absuchen (Start bei Recalc)
        private int _detectorMinBars = 10;       // Mindest-Bars fuer eine Range
        private decimal _detectorWidthFactor = 3.0m; // max. Range-Hoehe = Faktor * ATR
        private int _detectorAtrPeriod = 14;     // Periode der Breiten-ATR (stabil, kein Drift)
        private Color _colorBreakUp = Color.FromArgb(200, 60, 190, 90);   // hoch ausgebrochen
        private Color _colorBreakDn = Color.FromArgb(200, 225, 70, 70);   // runter ausgebrochen
        private Color _colorFlat = Color.FromArgb(170, 150, 150, 150);    // noch aktiv/flat

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        private int _fontSize = 14;
        private int _offsetX = 20;
        private int _offsetY = 20;
        private bool _topLeft = false;
        private int _markerTickOffset = 4;

        private Color _colorBull = Color.FromArgb(230, 50, 205, 80);
        private Color _colorBear = Color.FromArgb(230, 225, 60, 60);
        private Color _colorNeutral = Color.FromArgb(230, 200, 170, 60);
        private Color _colorBackground = Color.FromArgb(180, 18, 20, 26);
        private Color _colorText = Color.FromArgb(235, 220, 225, 235);
        private Color _colorWarn = Color.FromArgb(235, 235, 150, 45);
        private Color _colorDim = Color.FromArgb(190, 140, 150, 170);
        private Color _colorRevBull = Color.FromArgb(235, 70, 220, 220);   // Reversal Long = tuerkis
        private Color _colorRevBear = Color.FromArgb(235, 240, 140, 40);   // Reversal Short = orange
        private Color _colorRangeBand = Color.FromArgb(30, 120, 140, 185); // Value-Area-Band (transparent)
        private Color _colorRangeEdge = Color.FromArgb(200, 130, 160, 210);// VAH/VAL-Linien
        private Color _colorRangePoc = Color.FromArgb(220, 235, 200, 90);  // vPOC-Linie

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        // Pro Bar gespeicherte Metriken (fuer das rollende Perzentil-Fenster).
        private readonly List<decimal> _volArr = new();   // Candle-Volumen
        private readonly List<decimal> _absDArr = new();  // |Candle-Delta|
        private readonly List<decimal> _mldArr = new();   // |max. Level-Delta|

        // Kumuliertes Delta (CVD) je Bar, Session-Reset -> fuer Delta-Divergenz.
        private readonly List<decimal> _cumDeltaArr = new();
        private decimal _cumDeltaRun;

        // Session-VWAP-Akkumulation der abgeschlossenen Bars (Reset je Session).
        private decimal _cumPv, _cumVol;

        private int _lastProcessedBar = -1;
        // Pro Bar gespeicherter SIGNIERTER Score: > 0 Long, < 0 Short, 0 keins.
        // Betrag = Gewichtssumme der dominanten Seite (Staerke, wie semaPHoreks Lichter-Zahl).
        private readonly List<int> _signals = new();
        private int _lastSignalBar = -1;

        // Tape: pro Bar netto-signiertes Big-Trade-Volumen (Buy +, Sell -).
        // Wird live aus OnCumulativeTrade (Fremd-Thread) befuellt -> Concurrent.
        private readonly ConcurrentDictionary<int, int> _tapeNet = new();

        // Reversal: pro Bar signierter Reversal-Score (> 0 Long-Umkehr, < 0 Short).
        private readonly List<int> _revSignals = new();
        private int _lastRevBar = -1;   // Cooldown-Tracking fuer Reversal-Marker

        // Balance-Range (Value Area) — zuletzt berechnete Werte fuer das Rendering.
        private decimal _rangeVah, _rangeVal, _rangeVpoc;
        private int _rangeStartBar;
        private bool _rangeValid;

        // Range-Detektor: erkannte Konsolidierungs-Boxen.
        private struct DetRange { public int Start, End; public decimal High, Low; public int Dir; }
        private readonly List<DetRange> _detRanges = new();   // EINGEFRORENE Ranges (kein Repaint)

        // Inkrementeller Detektor-State: nur die aktive Kandidaten-Range waechst live.
        private int _lastDetBar = -1;
        private bool _candActive;
        private int _candStart;
        private decimal _candHi, _candLo, _candWidth;

        // Zuletzt berechnete Schwellen (fuer HUD + Freeze-Snapshot).
        private decimal _liveVolThr, _liveDeltaThr, _liveAbsThr;
        // Eingefrorene Schwellen (gehalten, solange Freeze aktiv ist).
        private decimal _frzVol, _frzDelta, _frzAbs;

        private string _lastRenderKey = "";

        // Gerenderte HUD-Werte.
        private int _hudBull, _hudBear, _hudSignal, _hudRev;
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
            Description = "Kalibrierungs-Fenster: ueber so viele Bars werden die Perzentil-Schwellen bestimmt.")]
        [Range(10, 1000)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(10, value); RecalculateValues(); } }

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

        [Display(Name = "Kalibrierung im HUD zeigen", GroupName = "Allgemein", Order = 6)]
        public bool ShowCalibration { get => _showCalibration; set { _showCalibration = value; RedrawChart(); } }

        [Display(Name = "Min-Score für Marker", GroupName = "Allgemein", Order = 7,
            Description = "Nur Marker mit Score >= diesem Wert zeichnen. 60 = mind. 3 Bedingungen " +
                          "ausgerichtet (versteckt die schwachen 50/55). 0 = alle.")]
        [Range(0, 100)]
        public int MinMarkerScore { get => _minMarkerScore; set { _minMarkerScore = Math.Clamp(value, 0, 100); RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Kalibrierung
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Globaler Perzentil", GroupName = "Kalibrierung", Order = 10,
            Description = "Schwelle = dieses Perzentil der letzten N Bars. 85 = feuert in den oberen 15%.")]
        [Range(50, 99)]
        public int GlobalPercentile { get => _globalPercentile; set { _globalPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Advanced: Perzentil pro Bedingung", GroupName = "Kalibrierung", Order = 11,
            Description = "Aus = globaler Wert fuer alle. Ein = je Bedingung eigener Perzentil unten.")]
        public bool UseAdvancedPercentiles { get => _useAdvancedPercentiles; set { _useAdvancedPercentiles = value; RecalculateValues(); } }

        [Display(Name = "Freeze (Kalibrierung einfrieren)", GroupName = "Kalibrierung", Order = 12,
            Description = "Friert die aktuellen Schwellen ein (wie ein semaPHorek-Template). Aus = rollend live.")]
        public bool FreezeCalibration
        {
            get => _freezeCalibration;
            set
            {
                // Beim Einfrieren die zuletzt berechneten Live-Schwellen als Snapshot uebernehmen.
                if (value && !_freezeCalibration)
                {
                    _frzVol = _liveVolThr;
                    _frzDelta = _liveDeltaThr;
                    _frzAbs = _liveAbsThr;
                }
                _freezeCalibration = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Perzentil Volumen", GroupName = "Kalibrierung", Order = 13)]
        [Range(50, 99)]
        public int VolPercentile { get => _volPercentile; set { _volPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Perzentil Delta", GroupName = "Kalibrierung", Order = 14)]
        [Range(50, 99)]
        public int DeltaPercentile { get => _deltaPercentile; set { _deltaPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Perzentil Absorption", GroupName = "Kalibrierung", Order = 15)]
        [Range(50, 99)]
        public int AbsPercentile { get => _absPercentile; set { _absPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Bedingungen
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Delta aktiv", GroupName = "Bedingung: Delta", Order = 20)]
        public bool DeltaEnabled { get => _deltaEnabled; set { _deltaEnabled = value; RecalculateValues(); } }

        [Display(Name = "Delta Gewicht", GroupName = "Bedingung: Delta", Order = 21)]
        [Range(0, 100)]
        public int DeltaWeight { get => _deltaWeight; set { _deltaWeight = value; RecalculateValues(); } }

        [Display(Name = "Volumen aktiv", GroupName = "Bedingung: Volumen", Order = 30)]
        public bool VolEnabled { get => _volEnabled; set { _volEnabled = value; RecalculateValues(); } }

        [Display(Name = "Volumen Gewicht", GroupName = "Bedingung: Volumen", Order = 31)]
        [Range(0, 100)]
        public int VolWeight { get => _volWeight; set { _volWeight = value; RecalculateValues(); } }

        [Display(Name = "Absorption aktiv", GroupName = "Bedingung: Absorption", Order = 40)]
        public bool AbsEnabled { get => _absEnabled; set { _absEnabled = value; RecalculateValues(); } }

        [Display(Name = "Absorption Gewicht", GroupName = "Bedingung: Absorption", Order = 41,
            Description = "Footprint-Absorption: groesstes Level-Delta ueber Schwelle. Richtung = Reversal gegen den Aggressor.")]
        [Range(0, 100)]
        public int AbsWeight { get => _absWeight; set { _absWeight = value; RecalculateValues(); } }

        [Display(Name = "VWAP aktiv", GroupName = "Bedingung: VWAP", Order = 50)]
        public bool VwapEnabled { get => _vwapEnabled; set { _vwapEnabled = value; RecalculateValues(); } }

        [Display(Name = "VWAP Gewicht", GroupName = "Bedingung: VWAP", Order = 51,
            Description = "Bias: Close ueber Session-VWAP = bullish, darunter = bearish. VWAP ankert taeglich (IsNewSession).")]
        [Range(0, 100)]
        public int VwapWeight { get => _vwapWeight; set { _vwapWeight = value; RecalculateValues(); } }

        [Display(Name = "Imbalance aktiv", GroupName = "Bedingung: Imbalance", Order = 52)]
        public bool ImbEnabled { get => _imbEnabled; set { _imbEnabled = value; RecalculateValues(); } }

        [Display(Name = "Imbalance Gewicht", GroupName = "Bedingung: Imbalance", Order = 53)]
        [Range(0, 100)]
        public int ImbWeight { get => _imbWeight; set { _imbWeight = value; RecalculateValues(); } }

        [Display(Name = "Imbalance Ratio", GroupName = "Bedingung: Imbalance", Order = 54,
            Description = "Diagonale Schwelle: Ask[p] >= Ratio * Bid[p-Tick] (Buy) bzw. umgekehrt. Default 2.0 = 200%.")]
        [Range(1.0, 20.0)]
        public decimal ImbRatio { get => _imbRatio; set { _imbRatio = value; RecalculateValues(); } }

        [Display(Name = "Imbalance Mindest-Anzahl", GroupName = "Bedingung: Imbalance", Order = 55,
            Description = "Mindestanzahl diagonaler Imbalances auf der dominanten Seite (gestapelt).")]
        [Range(1, 50)]
        public int ImbMinCount { get => _imbMinCount; set { _imbMinCount = Math.Max(1, value); RecalculateValues(); } }

        [Display(Name = "vPOC-in-Wick aktiv", GroupName = "Bedingung: vPOC", Order = 56)]
        public bool VpocEnabled { get => _vpocEnabled; set { _vpocEnabled = value; RecalculateValues(); } }

        [Display(Name = "vPOC Gewicht", GroupName = "Bedingung: vPOC", Order = 57,
            Description = "POC im unteren Docht = bullish (Kaeufer-Rejection), oberer Docht = bearish.")]
        [Range(0, 100)]
        public int VpocWeight { get => _vpocWeight; set { _vpocWeight = value; RecalculateValues(); } }

        [Display(Name = "Tape aktiv (LIVE-ONLY)", GroupName = "Bedingung: Tape", Order = 58,
            Description = "Big Trades ab Mindestgroesse. Erfasst NUR live ab Laden vorwaerts (keine Historie). " +
                          "Buy = bullish, Sell = bearish.")]
        public bool TapeEnabled { get => _tapeEnabled; set { _tapeEnabled = value; RecalculateValues(); } }

        [Display(Name = "Tape Gewicht", GroupName = "Bedingung: Tape", Order = 59)]
        [Range(0, 100)]
        public int TapeWeight { get => _tapeWeight; set { _tapeWeight = value; RecalculateValues(); } }

        [Display(Name = "Tape Mindest-Kontrakte", GroupName = "Bedingung: Tape", Order = 60,
            Description = "Ein einzelner Cumulative-Trade ab dieser Groesse zaehlt als Big Trade. Pro Instrument tunen.")]
        [Range(1, 100000)]
        public int TapeMinContracts { get => _tapeMinContracts; set { _tapeMinContracts = Math.Max(1, value); RecalculateValues(); } }

        // ── Reversal-Engine ────────────────────────────────────────────────
        [Display(Name = "Reversal aktiv", GroupName = "Reversal", Order = 80,
            Description = "Eigene Umkehr-Logik an lokalen Extrema (Divergenz, Absorption, vPOC-Docht, Exhaustion). Eigener Rauten-Marker.")]
        public bool ReversalEnabled { get => _reversalEnabled; set { _reversalEnabled = value; RecalculateValues(); } }

        [Display(Name = "Reversal Lookback (Bars)", GroupName = "Reversal", Order = 81,
            Description = "Fenster fuer Extrem-/Divergenz-Referenz: neues Tief/Hoch ueber so viele Bars = Umkehr-Kandidat.")]
        [Range(2, 200)]
        public int ReversalLookback { get => _reversalLookback; set { _reversalLookback = Math.Max(2, value); RecalculateValues(); } }

        [Display(Name = "Reversal-Schwelle (%)", GroupName = "Reversal", Order = 82,
            Description = "Mindest-Reversal-Score, damit eine Raute feuert.")]
        [Range(0, 100)]
        public int ReversalThreshold { get => _reversalThreshold; set { _reversalThreshold = Math.Clamp(value, 0, 100); RecalculateValues(); } }

        [Display(Name = "Gewicht Delta-Divergenz", GroupName = "Reversal", Order = 83)]
        [Range(0, 100)]
        public int RevDivWeight { get => _revDivWeight; set { _revDivWeight = value; RecalculateValues(); } }

        [Display(Name = "Gewicht Absorption am Extrem", GroupName = "Reversal", Order = 84)]
        [Range(0, 100)]
        public int RevAbsWeight { get => _revAbsWeight; set { _revAbsWeight = value; RecalculateValues(); } }

        [Display(Name = "Gewicht vPOC-im-Docht", GroupName = "Reversal", Order = 85)]
        [Range(0, 100)]
        public int RevVpocWeight { get => _revVpocWeight; set { _revVpocWeight = value; RecalculateValues(); } }

        [Display(Name = "Gewicht Exhaustion", GroupName = "Reversal", Order = 86)]
        [Range(0, 100)]
        public int RevExhWeight { get => _revExhWeight; set { _revExhWeight = value; RecalculateValues(); } }

        [Display(Name = "Folgekerzen-Bestätigung (2-Kerzen)", GroupName = "Reversal", Order = 87,
            Description = "Umkehr nur, wenn die naechste Kerze in Umkehr-Richtung schliesst. Hoehere Qualitaet, 1 Bar Verzoegerung.")]
        public bool RevConfirm { get => _revConfirm; set { _revConfirm = value; RecalculateValues(); } }

        // ── Balance-Range ──────────────────────────────────────────────────
        [Display(Name = "Balance-Range zeichnen", GroupName = "Balance-Range", Order = 90,
            Description = "Value Area (VAH/VAL/vPOC) ueber ein rollendes Fenster zeichnen.")]
        public bool RangeEnabled { get => _rangeEnabled; set { _rangeEnabled = value; RecalculateValues(); } }

        [Display(Name = "Range Lookback (Bars)", GroupName = "Balance-Range", Order = 91,
            Description = "Anzahl Bars fuer das Volumen-Profil der Value Area.")]
        [Range(10, 2000)]
        public int RangeLookback { get => _rangeLookback; set { _rangeLookback = Math.Max(10, value); RecalculateValues(); } }

        [Display(Name = "Value-Area Anteil (%)", GroupName = "Balance-Range", Order = 92,
            Description = "Anteil des Volumens in der Value Area (Standard 70%).")]
        [Range(30, 95)]
        public int RangeValuePct { get => _rangeValuePct; set { _rangeValuePct = Math.Clamp(value, 30, 95); RecalculateValues(); } }

        [Display(Name = "Linien nach rechts verlaengern", GroupName = "Balance-Range", Order = 93)]
        public bool RangeExtendRight { get => _rangeExtendRight; set { _rangeExtendRight = value; RedrawChart(); } }

        [Display(Name = "Farbe Range-Band", GroupName = "Farben", Order = 76)]
        public Color ColorRangeBand { get => _colorRangeBand; set { _colorRangeBand = value; RedrawChart(); } }

        [Display(Name = "Farbe Range-Raender", GroupName = "Farben", Order = 77)]
        public Color ColorRangeEdge { get => _colorRangeEdge; set { _colorRangeEdge = value; RedrawChart(); } }

        [Display(Name = "Farbe vPOC", GroupName = "Farben", Order = 78)]
        public Color ColorRangePoc { get => _colorRangePoc; set { _colorRangePoc = value; RedrawChart(); } }

        // ── Range-Detektor ─────────────────────────────────────────────────
        [Display(Name = "Range-Detektor aktiv", GroupName = "Range-Detektor", Order = 100,
            Description = "Diskrete Konsolidierungen (Balance nach Imbalance) erkennen und als Box markieren.")]
        public bool DetectorEnabled { get => _detectorEnabled; set { _detectorEnabled = value; RecalculateValues(); } }

        [Display(Name = "Detektor Lookback (Bars)", GroupName = "Range-Detektor", Order = 101)]
        [Range(20, 5000)]
        public int DetectorLookback { get => _detectorLookback; set { _detectorLookback = Math.Max(20, value); RecalculateValues(); } }

        [Display(Name = "Min-Bars pro Range", GroupName = "Range-Detektor", Order = 102,
            Description = "So viele Bars muss der Preis im engen Band bleiben, damit es als Balance zaehlt.")]
        [Range(3, 200)]
        public int DetectorMinBars { get => _detectorMinBars; set { _detectorMinBars = Math.Max(3, value); RecalculateValues(); } }

        [Display(Name = "Breiten-Faktor (x ATR)", GroupName = "Range-Detektor", Order = 103,
            Description = "Max. Range-Hoehe = Faktor * ATR (stabil, kein Drift). Kleiner = engere Balances.")]
        [Range(0.5, 20.0)]
        public decimal DetectorWidthFactor { get => _detectorWidthFactor; set { _detectorWidthFactor = value; RecalculateValues(); } }

        [Display(Name = "Breiten-ATR Periode", GroupName = "Range-Detektor", Order = 104,
            Description = "Periode fuer die ATR, die die Range-Breite bestimmt (fix je Range -> kein Repaint).")]
        [Range(2, 200)]
        public int DetectorAtrPeriod { get => _detectorAtrPeriod; set { _detectorAtrPeriod = Math.Max(2, value); RecalculateValues(); } }

        [Display(Name = "Farbe Break Up", GroupName = "Farben", Order = 79)]
        public Color ColorBreakUp { get => _colorBreakUp; set { _colorBreakUp = value; RedrawChart(); } }

        [Display(Name = "Farbe Break Down", GroupName = "Farben", Order = 80)]
        public Color ColorBreakDn { get => _colorBreakDn; set { _colorBreakDn = value; RedrawChart(); } }

        [Display(Name = "Farbe Range aktiv/flat", GroupName = "Farben", Order = 81)]
        public Color ColorFlat { get => _colorFlat; set { _colorFlat = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Schriftgroesse", GroupName = "Darstellung", Order = 60)]
        [Range(8, 30)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 30); BuildFonts(); RedrawChart(); } }

        [Display(Name = "Oben Links (aus = Oben Rechts)", GroupName = "Darstellung", Order = 61)]
        public bool TopLeft { get => _topLeft; set { _topLeft = value; RedrawChart(); } }

        [Display(Name = "Abstand X (px)", GroupName = "Darstellung", Order = 62)]
        [Range(0, 600)]
        public int OffsetX { get => _offsetX; set { _offsetX = value; RedrawChart(); } }

        [Display(Name = "Abstand Y (px)", GroupName = "Darstellung", Order = 63)]
        [Range(0, 600)]
        public int OffsetY { get => _offsetY; set { _offsetY = value; RedrawChart(); } }

        [Display(Name = "Marker-Abstand (Ticks)", GroupName = "Darstellung", Order = 64)]
        [Range(0, 100)]
        public int MarkerTickOffset { get => _markerTickOffset; set { _markerTickOffset = value; RedrawChart(); } }

        [Display(Name = "Farbe Bull", GroupName = "Farben", Order = 70)]
        public Color ColorBull { get => _colorBull; set { _colorBull = value; RedrawChart(); } }

        [Display(Name = "Farbe Bear", GroupName = "Farben", Order = 71)]
        public Color ColorBear { get => _colorBear; set { _colorBear = value; RedrawChart(); } }

        [Display(Name = "Farbe Neutral", GroupName = "Farben", Order = 72)]
        public Color ColorNeutral { get => _colorNeutral; set { _colorNeutral = value; RedrawChart(); } }

        [Display(Name = "Hintergrund", GroupName = "Farben", Order = 73)]
        public Color ColorBackground { get => _colorBackground; set { _colorBackground = value; RedrawChart(); } }

        [Display(Name = "Farbe Reversal Long", GroupName = "Farben", Order = 74)]
        public Color ColorRevBull { get => _colorRevBull; set { _colorRevBull = value; RedrawChart(); } }

        [Display(Name = "Farbe Reversal Short", GroupName = "Farben", Order = 75)]
        public Color ColorRevBear { get => _colorRevBear; set { _colorRevBear = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public OrderflowSignal() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

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

            int lastClosed = CurrentBar - 2;
            while (_lastProcessedBar < lastClosed)
            {
                _lastProcessedBar++;
                ProcessClosedBar(_lastProcessedBar);
            }

            if (bar != CurrentBar - 1)
                return;

            ComputeLive();
            ComputeBalanceRange();
            ProcessDetector();

            string key = $"{_hudBull}|{_hudBear}|{_hudSignal}|{_hudRev}|{_hudTags}|{_hudWarn}|{_chartLabel}|" +
                         $"{_liveVolThr}|{_liveDeltaThr}|{_liveAbsThr}|{_freezeCalibration}|" +
                         $"{_rangeVah}|{_rangeVal}|{_rangeVpoc}|{CurrentBar}";
            if (key != _lastRenderKey)
            {
                _lastRenderKey = key;
                RedrawChart();
            }
        }

        // Live-Tape: jeder Cumulative-Trade ab Mindestgroesse wird dem aktuell
        // bildenden Bar netto-signiert gutgeschrieben (Buy +, Sell -). Laeuft auf
        // einem Fremd-Thread -> nur in die ConcurrentDictionary schreiben, kein
        // RedrawChart (OnCalculate liest die Werte beim naechsten Tick).
        protected override void OnCumulativeTrade(CumulativeTrade trade)
        {
            if (!_tapeEnabled || trade == null || trade.Volume < _tapeMinContracts)
                return;

            int bar = CurrentBar - 1;
            if (bar < 0)
                return;

            int signed = trade.Direction == TradeDirection.Buy ? (int)trade.Volume
                       : trade.Direction == TradeDirection.Sell ? -(int)trade.Volume : 0;
            if (signed == 0)
                return;

            _tapeNet.AddOrUpdate(bar, signed, (_, cur) => cur + signed);
        }

        private void ResetState()
        {
            _volArr.Clear();
            _absDArr.Clear();
            _mldArr.Clear();
            _cumDeltaArr.Clear();
            _cumDeltaRun = 0;
            _cumPv = 0;
            _cumVol = 0;
            _lastProcessedBar = -1;
            _signals.Clear();
            _revSignals.Clear();
            _lastSignalBar = -1;
            _lastRevBar = -1;
            _rangeValid = false;
            _detRanges.Clear();
            _lastDetBar = -1;
            _candActive = false;
            // _frz* NICHT zuruecksetzen -> eingefrorene Kalibrierung ueberlebt Recalc.
            _hudBull = _hudBear = _hudSignal = _hudRev = 0;
            _hudTags = "";
            _hudWarn = "";
            _chartLabel = "";
            _lastRenderKey = "";
        }

        private void ProcessClosedBar(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            // Metriken fuer das Perzentil-Fenster speichern.
            decimal signedMld = MaxLevelDeltaSigned(c);
            StoreMetric(_volArr, bar, c.Volume);
            StoreMetric(_absDArr, bar, Math.Abs(c.Delta));
            StoreMetric(_mldArr, bar, Math.Abs(signedMld));

            // Session-VWAP + kumuliertes Delta (CVD) fortschreiben.
            if (IsNewSession(bar))
            {
                _cumPv = 0;
                _cumVol = 0;
                _cumDeltaRun = 0;
            }
            _cumPv += BarPriceForVwap(c) * c.Volume;
            _cumVol += c.Volume;
            decimal vwap = _cumVol > 0 ? _cumPv / _cumVol : 0;

            _cumDeltaRun += c.Delta;
            StoreMetric(_cumDeltaArr, bar, _cumDeltaRun);

            var o = EvaluateBar(bar, c, signedMld, vwap, _cumVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;

            SetSignal(bar, SignedScore(sig, o));
            if (sig != 0)
                _lastSignalBar = bar;

            int rev = RevEvaluate(bar, c, signedMld, _cumDeltaRun);
            if (rev != 0 && _revConfirm && !RevConfirmed(bar, Math.Sign(rev)))
                rev = 0;   // 2-Kerzen-Bestaetigung fehlt
            if (rev != 0 && _lastRevBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastRevBar) <= _signalCooldownBars)
                rev = 0;
            SetRevSignal(bar, rev);
            if (rev != 0)
                _lastRevBar = bar;
        }

        private void ComputeLive()
        {
            int last = CurrentBar - 1;
            var c = GetCandle(last);
            if (c == null)
                return;

            decimal signedMld = MaxLevelDeltaSigned(c);

            decimal basePv = _cumPv, baseVol = _cumVol;
            if (IsNewSession(last)) { basePv = 0; baseVol = 0; }
            basePv += BarPriceForVwap(c) * c.Volume;
            baseVol += c.Volume;
            decimal liveVwap = baseVol > 0 ? basePv / baseVol : 0;

            var o = EvaluateBar(last, c, signedMld, liveVwap, baseVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (last - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;
            SetSignal(last, SignedScore(sig, o));

            decimal baseCd = IsNewSession(last) ? 0m
                : (last - 1 >= 0 && last - 1 < _cumDeltaArr.Count ? _cumDeltaArr[last - 1] : 0m);
            decimal liveCumDelta = baseCd + c.Delta;

            int rev = RevEvaluate(last, c, signedMld, liveCumDelta);
            if (rev != 0 && _revConfirm)
                rev = 0;   // Folgekerze existiert noch nicht -> erst nach Bestaetigung (naechste Bar)
            if (rev != 0 && _lastRevBar >= 0 && _signalCooldownBars > 0
                && (last - _lastRevBar) <= _signalCooldownBars)
                rev = 0;
            SetRevSignal(last, rev);
            _hudRev = rev;

            // Schwellen rollend halten, solange nicht eingefroren.
            if (!_freezeCalibration)
            {
                _frzVol = _liveVolThr;
                _frzDelta = _liveDeltaThr;
                _frzAbs = _liveAbsThr;
            }

            _hudBull = o.Bull;
            _hudBear = o.Bear;
            _hudSignal = sig;
            _hudTags = $"Δ{Arrow(o.DirDelta)} Vol{Arrow(o.DirVol)} Abs{Arrow(o.DirAbs)} VW{Arrow(o.DirVwap)} " +
                       $"Im{Arrow(o.DirImb)} vP{Arrow(o.DirVpoc)}" + (_tapeEnabled ? $" Tp{Arrow(o.DirTape)}" : "");
            _hudWarn = BuildWarning();
            _chartLabel = BuildChartLabel();
        }

        // ─────────────────────────────────────────────────────────────────
        //  BEDINGUNGS-AUSWERTUNG
        // ─────────────────────────────────────────────────────────────────
        private struct EvalOut
        {
            public int Bull, Bear;
            public int DirDelta, DirVol, DirAbs, DirVwap, DirImb, DirVpoc, DirTape;
        }

        private EvalOut EvaluateBar(int bar, IndicatorCandle c, decimal signedMld, decimal vwap, bool vwapValid)
        {
            var o = new EvalOut();

            if (!Thresholds(bar, out decimal volThr, out decimal deltaThr, out decimal absThr))
                return o;

            _liveVolThr = volThr;
            _liveDeltaThr = deltaThr;
            _liveAbsThr = absThr;

            // 1) Delta signifikant.
            if (_deltaEnabled && deltaThr > 0 && Math.Abs(c.Delta) >= deltaThr)
            {
                o.DirDelta = Math.Sign(c.Delta);
                Accumulate(ref o, o.DirDelta, _deltaWeight);
            }

            // 2) Volumen-Spike (Richtung = Kerzenkoerper).
            if (_volEnabled && volThr > 0 && c.Volume >= volThr)
            {
                o.DirVol = c.Close > c.Open ? 1 : c.Close < c.Open ? -1 : 0;
                Accumulate(ref o, o.DirVol, _volWeight);
            }

            // 3) Footprint-Absorption (Reversal gegen den absorbierten Aggressor).
            if (_absEnabled && absThr > 0 && Math.Abs(signedMld) >= absThr)
            {
                o.DirAbs = -Math.Sign(signedMld);
                Accumulate(ref o, o.DirAbs, _absWeight);
            }

            // 4) VWAP-Lage (Bias, keine Kalibrierung).
            if (_vwapEnabled && vwapValid && vwap > 0)
            {
                o.DirVwap = c.Close > vwap ? 1 : c.Close < vwap ? -1 : 0;
                Accumulate(ref o, o.DirVwap, _vwapWeight);
            }

            // 5) Imbalance (diagonal, gestapelt).
            if (_imbEnabled)
            {
                decimal tick = InstrumentInfo?.TickSize ?? 0m;
                if (tick > 0 && EvaluateImbalance(c, tick, _imbRatio, _imbMinCount, out int idir))
                {
                    o.DirImb = idir;
                    Accumulate(ref o, idir, _imbWeight);
                }
            }

            // 6) vPOC-in-Wick (location-basiert).
            if (_vpocEnabled)
            {
                int vdir = VpocWickDir(c);
                if (vdir != 0)
                {
                    o.DirVpoc = vdir;
                    Accumulate(ref o, vdir, _vpocWeight);
                }
            }

            // 7) Tape / Big Trades (live erfasst, netto-signiert).
            if (_tapeEnabled && _tapeNet.TryGetValue(bar, out int tnet) && tnet != 0)
            {
                o.DirTape = Math.Sign(tnet);
                Accumulate(ref o, o.DirTape, _tapeWeight);
            }

            // Score auf Anteil der AKTIVEN Gewichte normieren (0-100 %).
            int totalW = TotalEnabledWeight();
            if (totalW > 0)
            {
                o.Bull = (int)Math.Round(100.0 * o.Bull / totalW);
                o.Bear = (int)Math.Round(100.0 * o.Bear / totalW);
            }

            return o;
        }

        private int TotalEnabledWeight()
        {
            int w = 0;
            if (_deltaEnabled) w += _deltaWeight;
            if (_volEnabled) w += _volWeight;
            if (_absEnabled) w += _absWeight;
            if (_vwapEnabled) w += _vwapWeight;
            if (_imbEnabled) w += _imbWeight;
            if (_vpocEnabled) w += _vpocWeight;
            if (_tapeEnabled) w += _tapeWeight;
            return w;
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

        // Signierter Score fuer die Speicherung: Richtung * Gewichtssumme der
        // dominanten Seite. 0 wenn kein Signal.
        private static int SignedScore(int dir, EvalOut o)
            => dir > 0 ? o.Bull : dir < 0 ? -o.Bear : 0;

        // Liefert die Schwellen fuer einen Bar. Eingefroren -> feste Snapshots,
        // sonst rollendes Perzentil ueber [bar-Lookback, bar-1]. false in der
        // Anlaufphase (zu wenig Historie).
        private bool Thresholds(int bar, out decimal volThr, out decimal deltaThr, out decimal absThr)
        {
            volThr = deltaThr = absThr = 0;

            if (_freezeCalibration && _frzVol > 0)
            {
                volThr = _frzVol;
                deltaThr = _frzDelta;
                absThr = _frzAbs;
                return true;
            }

            int start = bar - _lookback;
            if (start < 0)
                return false;

            volThr = Percentile(_volArr, start, _lookback, EffPercentile(_volPercentile));
            deltaThr = Percentile(_absDArr, start, _lookback, EffPercentile(_deltaPercentile));
            absThr = Percentile(_mldArr, start, _lookback, EffPercentile(_absPercentile));
            return true;
        }

        private double EffPercentile(int specific)
            => _useAdvancedPercentiles ? specific : _globalPercentile;

        // Perzentil (Nearest-Rank) ueber src[from .. from+count-1].
        private static decimal Percentile(List<decimal> src, int from, int count, double p)
        {
            if (count <= 0 || from < 0 || from + count > src.Count)
                return decimal.MaxValue; // keine valide Schwelle -> Bedingung feuert nicht

            var tmp = new decimal[count];
            for (int i = 0; i < count; i++)
                tmp[i] = src[from + i];
            Array.Sort(tmp);

            int rank = (int)Math.Ceiling(p / 100.0 * count); // 1-basiert
            rank = Math.Clamp(rank, 1, count);
            return tmp[rank - 1];
        }

        // Groesstes (betragsmaessig) Level-Delta (Ask-Bid je Preislevel) der Kerze.
        // Footprint-basiert -> range-unabhaengig (auch auf Renko gueltig).
        // Fallback ohne Cluster-Daten: das Candle-Delta als einzelnes Level.
        private static decimal MaxLevelDeltaSigned(IndicatorCandle c)
        {
            decimal best = 0;
            bool any = false;
            foreach (var pv in c.GetAllPriceLevels())
            {
                any = true;
                decimal d = pv.Ask - pv.Bid;
                if (Math.Abs(d) > Math.Abs(best))
                    best = d;
            }
            return any ? best : c.Delta;
        }

        // Diagonale Imbalances zaehlen: Buy = Ask[p] >= Ratio * Bid[p-Tick],
        // Sell = Bid[p] >= Ratio * Ask[p+Tick]. Aktiv, wenn eine Seite >= minCount
        // erreicht und dominiert. Richtung = dominante Stapelseite.
        private static bool EvaluateImbalance(IndicatorCandle c, decimal tick, decimal ratio, int minCount, out int dir)
        {
            dir = 0;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid;
                ask[pv.Price] = pv.Ask;
            }
            if (bid.Count == 0)
                return false;

            int buy = 0, sell = 0;
            foreach (var kv in ask)
            {
                if (bid.TryGetValue(kv.Key - tick, out var bBelow) && bBelow > 0 && kv.Value >= ratio * bBelow)
                    buy++;
            }
            foreach (var kv in bid)
            {
                if (ask.TryGetValue(kv.Key + tick, out var aAbove) && aAbove > 0 && kv.Value >= ratio * aAbove)
                    sell++;
            }

            if (buy >= minCount && buy > sell) { dir = 1; return true; }
            if (sell >= minCount && sell > buy) { dir = -1; return true; }
            return false;
        }

        // POC (Preislevel mit max. Volumen) relativ zum Kerzenkoerper.
        // Unterer Docht = bullishe Rejection (+1), oberer Docht = bearish (-1).
        private static int VpocWickDir(IndicatorCandle c)
        {
            decimal pocPrice = 0, pocVol = -1;
            foreach (var pv in c.GetAllPriceLevels())
            {
                if (pv.Volume > pocVol) { pocVol = pv.Volume; pocPrice = pv.Price; }
            }
            if (pocVol < 0)
                return 0;

            decimal bodyHi = Math.Max(c.Open, c.Close);
            decimal bodyLo = Math.Min(c.Open, c.Close);
            if (pocPrice > bodyHi) return -1;
            if (pocPrice < bodyLo) return 1;
            return 0;
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
                    _detRanges.Add(new DetRange { Start = _candStart, End = bar - 1, High = _candHi, Low = _candLo, Dir = dir });
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

            // Referenz-Extrema + kumuliertes Delta (CVD) am jeweiligen Extrem.
            decimal minLow = decimal.MaxValue, maxHigh = decimal.MinValue;
            decimal cdAtLow = 0, cdAtHigh = 0;
            for (int i = start; i < bar; i++)
            {
                var ci = GetCandle(i);
                if (ci == null)
                    return 0;
                decimal cd = i < _cumDeltaArr.Count ? _cumDeltaArr[i] : 0m;
                if (ci.Low < minLow) { minLow = ci.Low; cdAtLow = cd; }
                if (ci.High > maxHigh) { maxHigh = ci.High; cdAtHigh = cd; }
            }

            int totalW = _revDivWeight + _revAbsWeight + _revVpocWeight + _revExhWeight;
            if (totalW <= 0)
                totalW = 1;

            decimal mid = (c.High + c.Low) / 2m;

            // Long-Umkehr: neues Tief, aber Orderflow dreht.
            if (c.Low <= minLow)
            {
                // Primaere Treiber (mind. einer Pflicht):
                // (a) Kumulative Delta-Divergenz: tieferes Tief, aber CVD hoeher.
                bool divg = curCumDelta > cdAtLow;
                // (b) Echte Absorption: grosses Verkaeufer-Delta UND Tief abgelehnt
                //     (Close in der oberen Bar-Haelfte) -> Aggressor getrappt.
                bool absr = signedMld < 0 && Math.Abs(signedMld) >= absThr && c.Close >= mid;

                if (divg || absr)
                {
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (VpocWickDir(c) > 0) s += _revVpocWeight;          // POC im unteren Docht
                    if (ExhaustionAtExtreme(c, true)) s += _revExhWeight; // duennes Verkaufsvol am Tief
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                        return pct;
                }
            }

            // Short-Umkehr: neues Hoch, aber Orderflow dreht.
            if (c.High >= maxHigh)
            {
                bool divg = curCumDelta < cdAtHigh;
                bool absr = signedMld > 0 && Math.Abs(signedMld) >= absThr && c.Close <= mid;

                if (divg || absr)
                {
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (VpocWickDir(c) < 0) s += _revVpocWeight;
                    if (ExhaustionAtExtreme(c, false)) s += _revExhWeight;
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                        return -pct;
                }
            }

            return 0;
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
        private static bool ExhaustionAtExtreme(IndicatorCandle c, bool atLow)
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
            return avg > 0 && aggrAtExt <= 0.5m * avg;
        }

        private static decimal BarPriceForVwap(IndicatorCandle c)
            => c.VWAP > 0 ? c.VWAP : (c.High + c.Low + c.Close) / 3m;

        // ─────────────────────────────────────────────────────────────────
        //  SPEICHER-HELFER
        // ─────────────────────────────────────────────────────────────────
        private static void StoreMetric(List<decimal> list, int bar, decimal v)
        {
            while (list.Count <= bar)
                list.Add(0);
            list[bar] = v;
        }

        private void SetSignal(int bar, int v)
        {
            while (_signals.Count <= bar)
                _signals.Add(0);
            _signals[bar] = v;
        }

        private int GetSignal(int bar)
            => bar >= 0 && bar < _signals.Count ? _signals[bar] : 0;

        private void SetRevSignal(int bar, int v)
        {
            while (_revSignals.Count <= bar)
                _revSignals.Add(0);
            _revSignals[bar] = v;
        }

        private int GetRevSignal(int bar)
            => bar >= 0 && bar < _revSignals.Count ? _revSignals[bar] : 0;

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

        // Warnt nur noch bei Volumen-Bars: dort ist das Volumen je Bar konstant,
        // das Volumen-Perzentil also bedeutungslos. (Absorption ist jetzt
        // footprint-basiert und damit auch auf Range/Renko gueltig.)
        private string BuildWarning()
        {
            var ct = (ChartInfo?.ChartType ?? "").ToLowerInvariant();
            if (_volEnabled && ct.Contains("volume"))
                return "! Volumen-Bars: Volumen-Bedingung ungueltig";
            return "";
        }

        // ─────────────────────────────────────────────────────────────────
        //  RENDER
        // ─────────────────────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_font == null)
                return;

            DrawBalanceRange(context);
            DrawDetectorRanges(context);

            if (_showMarkers)
            {
                DrawMarkers(context);
                DrawReversalMarkers(context);
            }

            if (_showHud)
                DrawHud(context);
        }

        private void DrawBalanceRange(RenderContext context)
        {
            if (!_rangeEnabled || !_rangeValid)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            int yVah, yVal, yPoc, xStart;
            try
            {
                yVah = cont.GetYByPrice(_rangeVah, false);
                yVal = cont.GetYByPrice(_rangeVal, false);
                yPoc = cont.GetYByPrice(_rangeVpoc, false);
                xStart = _rangeExtendRight ? region.Left
                       : Math.Max(region.Left, cont.GetXByBar(_rangeStartBar, false));
            }
            catch { return; }

            int xEnd = region.Right;
            if (xEnd <= xStart)
                return;

            // Band (VAL..VAH). Hoeherer Preis = kleineres y -> yVah < yVal.
            int top = Math.Min(yVah, yVal);
            int h = Math.Abs(yVal - yVah);
            if (h > 0)
                context.FillRectangle(_colorRangeBand, new Rectangle(xStart, top, xEnd - xStart, h));

            var edgePen = new RenderPen(_colorRangeEdge, 1);
            context.DrawLine(edgePen, xStart, yVah, xEnd, yVah);
            context.DrawLine(edgePen, xStart, yVal, xEnd, yVal);
            context.DrawLine(new RenderPen(_colorRangePoc, 1), xStart, yPoc, xEnd, yPoc);

            // Labels rechts an den Linien.
            context.DrawString($"VAH {_rangeVah:0.##}", _font, _colorRangeEdge, xEnd - 110, yVah - 14);
            context.DrawString($"VAL {_rangeVal:0.##}", _font, _colorRangeEdge, xEnd - 110, yVal + 2);
            context.DrawString($"POC {_rangeVpoc:0.##}", _font, _colorRangePoc, xEnd - 110, yPoc - 14);
        }

        private void DrawDetectorRanges(RenderContext context)
        {
            if (!_detectorEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;

            // Eingefrorene Ranges enden an ihrem End (kein Repaint, kein Verlaengern).
            foreach (var r in _detRanges)
                DrawOneRange(context, cont, region, r.Start, r.End, r.High, r.Low, r.Dir, false);

            // Aktive Kandidaten-Range (grau, laeuft nach rechts), sobald sie lang genug ist.
            if (_candActive && (CurrentBar - 2) - _candStart + 1 >= _detectorMinBars)
                DrawOneRange(context, cont, region, _candStart, CurrentBar - 1, _candHi, _candLo, 0, true);
        }

        private void DrawOneRange(RenderContext context, IChartContainer cont, Rectangle region,
                                  int startBar, int endBar, decimal high, decimal low, int dir, bool extendRight)
        {
            int x1, x2, yH, yL;
            try
            {
                x1 = cont.GetXByBar(startBar, false);
                x2 = extendRight ? region.Right : cont.GetXByBar(endBar, false);
                yH = cont.GetYByPrice(high, false);
                yL = cont.GetYByPrice(low, false);
            }
            catch { return; }

            if (x2 < region.Left || x1 > region.Right)
                return;
            x1 = Math.Max(x1, region.Left);
            x2 = Math.Min(x2, region.Right);
            if (x2 <= x1)
                return;

            var col = dir > 0 ? _colorBreakUp : dir < 0 ? _colorBreakDn : _colorFlat;
            int top = Math.Min(yH, yL), h = Math.Abs(yL - yH);
            context.DrawRectangle(new RenderPen(col, 1), new Rectangle(x1, top, x2 - x1, h));
        }

        private void DrawMarkers(RenderContext context)
        {
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * _markerTickOffset;
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int v = GetSignal(b);
                if (v == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                int dir = Math.Sign(v);
                int strength = Math.Abs(v);
                if (strength < _minMarkerScore)
                    continue;

                decimal price = dir > 0 ? c.Low - offset : c.High + offset;

                int x, y;
                try
                {
                    x = cont.GetXByBar(b, false);
                    y = cont.GetYByPrice(price, false);
                }
                catch { continue; }

                if (x < region.Left || x > region.Right)
                    continue;

                string glyph = dir > 0 ? "▲" : "▼";
                var col = dir > 0 ? _colorBull : _colorBear;
                var sz = context.MeasureString(glyph, _fontMarker);

                // Long-Pfeil unter dem Low (Oberkante an y), Short-Pfeil ueber dem High.
                int drawY = dir > 0 ? y : y - sz.Height;
                context.DrawString(glyph, _fontMarker, col, x - sz.Width / 2, drawY);

                // Staerke-Zahl (Score) wie semaPHoreks Lichter-Zahl: Long darunter, Short darueber.
                string num = strength.ToString();
                var nsz = context.MeasureString(num, _font);
                int numY = dir > 0 ? drawY + sz.Height : drawY - nsz.Height;
                context.DrawString(num, _font, col, x - nsz.Width / 2, numY);
            }
        }

        // Reversal-Marker: Rauten (◆), getrennt von den Momentum-Pfeilen und
        // weiter vom Kurs weg gezeichnet, damit an einer Wende beide sichtbar sind.
        private void DrawReversalMarkers(RenderContext context)
        {
            if (!_reversalEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * (_markerTickOffset * 2 + 6);
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int v = GetRevSignal(b);
                if (v == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                int dir = Math.Sign(v);
                int strength = Math.Abs(v);
                decimal price = dir > 0 ? c.Low - offset : c.High + offset;

                int x, y;
                try
                {
                    x = cont.GetXByBar(b, false);
                    y = cont.GetYByPrice(price, false);
                }
                catch { continue; }

                if (x < region.Left || x > region.Right)
                    continue;

                var col = dir > 0 ? _colorRevBull : _colorRevBear;

                // Raute als echtes Polygon zeichnen (Glyph ◆ rendert im ATAS-Font nicht).
                int r = Math.Max(5, _fontSize / 2 + 1);
                var pts = new[]
                {
                    new Point(x, y - r),
                    new Point(x + r, y),
                    new Point(x, y + r),
                    new Point(x - r, y),
                };
                context.FillPolygon(col, pts);

                // Staerke-Zahl: Long unter der Raute, Short darueber.
                string num = strength.ToString();
                var nsz = context.MeasureString(num, _font);
                int numY = dir > 0 ? y + r + 1 : y - r - nsz.Height - 1;
                context.DrawString(num, _font, col, x - nsz.Width / 2, numY);
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

            if (_reversalEnabled)
            {
                string rt = _hudRev > 0 ? "LONG" : _hudRev < 0 ? "SHORT" : "—";
                var rc = _hudRev > 0 ? _colorRevBull : _hudRev < 0 ? _colorRevBear : _colorNeutral;
                lines.Add(($"REV: {rt} {Math.Abs(_hudRev)}", rc, _font));
            }

            if (_showCalibration)
            {
                int p = _useAdvancedPercentiles ? 0 : _globalPercentile;
                string pTxt = _useAdvancedPercentiles ? "adv" : p.ToString();
                string frozen = _freezeCalibration ? " ❄" : "";
                lines.Add(($"Cal P{pTxt} N{_lookback}{frozen}", _colorDim, _font));
                lines.Add(($"V≥{_liveVolThr:0}  Δ≥{_liveDeltaThr:0}  A≥{_liveAbsThr:0}", _colorDim, _font));
            }

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
