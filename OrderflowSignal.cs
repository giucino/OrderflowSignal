using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Attributes.Editors;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
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
    public partial class OrderflowSignal : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Allgemein
        // ─────────────────────────────────────────────────────────────────
        // Kalibrierungs-Fenster: Anzahl Bars, ueber die die Perzentil-Schwellen
        // bestimmt werden (= die letzten N Bars).
        private int _lookback = 50;

        private int _signalThreshold = 50;   // Mindest-Gewichtssumme der dominanten Seite
        private int _signalCooldownBars = 3; // Mindestabstand zwischen Markern (0 = aus)

        private bool _showHud = true;
        private bool _showMarkers = true;
        private bool _showReversalMarkers = true;
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
        // oberen (100 - P) % liegt). Default 95 = erprobte Selektivitaet.
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

        // ── BIG-TRADE-LEVELS ───────────────────────────────────────────────
        // Grosse Prints als verteidigte Levels markieren; Alarm beim Re-Test.
        // Session-abhaengige Mindestgroesse (London niedriger als US).
        private bool _bigEnabled = true;
        private bool _bigSeparated = true;     // einzelne Prints (OnNewTrade) statt kumulativ
        private int _bigMinLondon = 25;
        private int _bigMinDefault = 40;       // US / sonstige Zeiten
        private int _bigLondonStartHour = 9;   // Chart-Zeitzone (wie Sessions sonst)
        private int _bigLondonEndHour = 15;
        private int _bigArmTicks = 8;          // so weit muss der Preis weg, bevor Re-Test zaehlt
        private int _bigHitTolerance = 2;      // Toleranz in Ticks fuer Hit/Dedupe
        private bool _bigAlertOnHit = true;
        private bool _bigKeepAfterHit = true;   // gehittete Levels behalten (gedimmt/gestrichelt)
        private Color _colorBigBuy = Color.FromArgb(220, 70, 200, 120);
        private Color _colorBigSell = Color.FromArgb(220, 230, 100, 100);

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
        private int _revSpeedWeight = 15;     // Speed of Tape: Klimax-Spike am Extrem
        private int _revImbWeight = 0;        // (A) frischer Imbalance-Flip am Extrem (Default aus)
        private int _revAuctionWeight = 0;    // (B) Finished Auction am Extrem (Default aus)
        // Zusatz-Treiber (alle Default-Gewicht 0 = neutral, aendern nichts):
        private int _revAcntWeight = 0;         // (C) Absorption Count: mehrere absorbierte Level
        private int _revAcntMin = 2;            // min. Anzahl absorbierter Level ("X of Y")
        private bool _revFilterAcnt = false;    // HARTER Filter: nur Reversals mit AC# >= _revAcntMin
        private bool _revFilterKl = false;      // HARTER Filter: nur Reversals mit KeyLevel-Konfluenz
        private decimal _revAcntFrac = 0.5m;    // Level zaehlt ab Frac * staerkstem Level-Delta
        private int _revPwrWeight = 0;          // (D) Volume Power Bar: Power-Kerze in Umkehr-Richtung
        private decimal _revPwrVolMult = 1.5m;  // Volumen >= Mult * Ø-Bar-Volumen im Fenster
        private int _revCdeltaWeight = 0;       // (E) Kerzen-Delta im Reversal (Netto-Delta der Umkehrkerze)
        private decimal _revCdeltaFactor = 0.5m;// Kerzen-Delta >= Faktor * Ø-|Bar-Delta|
        private decimal _revExhFactor = 0.5m; // Exhaustion: Aggressor-Vol am Extrem <= Faktor * Ø (niedriger = strenger)

        // Reversal-Diagnose: Treiber-Aufschluesselung der letzten ANGEZEIGTEN Raute.
        private bool _showRevDebug = false;
        private struct RevDbg { public decimal Eff; public bool Strong, Div, Abs, Vp, Exh, Spd, Imb, Auc, Acn, Pwr, Cdl; public int AcnN; public decimal PwrX, CdlX, SpdX; }
        private RevDbg _revCand; private int _revCandDir, _revCandPct;
        private readonly Dictionary<int, RevDbg> _revDbgByBar = new();   // Treiber je Raute (fuer Hover-Diagnose)
        private int _hoverRevBar = -1;                                   // Raute unter dem Mauszeiger
        private decimal _revSpeedFactor = 1.5m; // Spike, wenn Bar-Speed >= Faktor * Ø-Speed
        // Impuls-Filter: in einem gesunden, gerichteten Impuls braucht ein Gegentrend-
        // Reversal echte CVD-Divergenz (Absorption allein reicht nicht). Default AUS.
        private bool _revImpulseFilter = false;
        private decimal _revImpulseEff = 0.5m;  // ab dieser Effizienz gilt das Bein als Impuls
        private decimal _revDivMinFactor = 1.5m; // im Impuls: CVD-Divergenz >= Faktor * Ø-Bar-Delta
        // 2-Kerzen-Bestaetigung: Umkehr nur, wenn die Folgekerze in Umkehr-
        // Richtung schliesst (ISA-Prinzip). Default an.
        private bool _revConfirm = true;

        // Phase 2b: Reversal-Rauten NUR an Range-Kanten anzeigen (reiner Display-
        // Filter, aendert die gespeicherten Signale nicht). Default AUS.
        private bool _revEdgeOnly = false;
        private int _revEdgeTolerance = 6;   // Toleranz in Ticks zur Kante (High/Low/vPOC)

        // ── KeyLevels-Konfluenz (Link zu KeyLevels; REINE Markierung, aendert Signale nicht) ──
        private bool _klConfluence = true;
        private int _klTolTicks = 4;
        private readonly List<(decimal Price, string Label)> _klLevels = new();
        private DateTime _klLastRead = DateTime.MinValue;

        // ── Auto-Bridge: Signal pro geschlossenem Bar in Datei -> OrderflowAuto-Strategie liest es ──
        private bool _bridgeExport = false;
        private string _bridgeKey = "";   // optionaler Zusatz im Dateinamen -> mehrere Timeframes je Instrument

        // ── Backtest-Log (CSV): jede Umkehr + auto-gemessener Ausgang (MFE/MAE/Net) ──
        private bool _btLog = false;
        private int _btHorizon = 15;   // geschlossene Bars fuer die Ausgangsmessung
        private struct BtPending { public int Dir, Age, Pct; public decimal Entry, Mfe, Mae; public DateTime Time; public RevDbg D; public bool Kl; }
        private readonly List<BtPending> _btPending = new();

        // ── Position-Tool: SL/TP-Boxen automatisch an jedem Signal einzeichnen (rein visuell, keine Order) ──
        public enum PosSource { Reversal, Signal }
        private PosSource _posSource = PosSource.Reversal;   // welche Signale ausgewertet werden
        private bool _posTool = false;
        private int _posSlTicks = 50;
        private int _posTpTicks = 100;
        private int _posBoxBars = 20;   // Breite der Boxen in Bars
        private Color _posSlColor = Color.FromArgb(55, 225, 70, 70);
        private Color _posTpColor = Color.FromArgb(55, 60, 200, 100);
        private bool _posShowStats = true;
        private int _sessTz = 2;    // Std von UTC -> lokale Zeit (CEST = +2)
        private int _sessAsia = 2, _sessLon = 8, _sessNy = 15, _sessNyEnd = 23;   // lokale Session-Grenzen (Std)
        private static readonly string[] SessNames = { "Asia  ", "London", "NY    " };
        private int _posBeTrigger = 0;   // SL auf Entry (Breakeven), sobald +X Ticks im Plus (0 = aus)
        private bool _posLikeAuto = false;   // Entry auf Folge-Open (wie die Auto-Strategie), statt massgeblichem Close
        // First-Touch-Auswertung je Session (0=Asia, 1=London, 2=NY). MaxFav/BeArmed = laufender Zustand.
        private readonly List<(int Bar, int Dir, int Sess, decimal Entry, decimal Tp, decimal Sl, decimal MaxFav, bool BeArmed)> _posPend = new();
        private readonly Dictionary<int, int> _posOutcome = new();   // bar -> 1 Win, -1 Loss, -2 ambivalent, 2 Breakeven, 0 offen
        private readonly int[] _psW = new int[3], _psL = new int[3], _psAmb = new int[3], _psBe = new int[3], _psOpen = new int[3];
        private readonly long[] _psNet = new long[3];
        private decimal _posCostTicks = 3m;   // Kosten (Kommission + Slippage) pro Trade in Ticks
        private readonly List<(int Bar, int Sess, int Oc)> _posResolved = new();   // fuer Serien (in Entry-Reihenfolge)
        private readonly int[] _psMaxW = new int[3], _psMaxL = new int[3];
        private readonly decimal[] _psMaxDD = new decimal[3];   // max. Equity-Drawdown je Session (Ticks, netto)
        private int _posStreakN = -1;
        private decimal _posStreakCost = -1m;   // Cache-Invalidierung bei Kosten-Aenderung

        // ── Repaint-freie Folgekerzen-Bestaetigung: Reversal erst emittieren, wenn die
        //    Folgekerze VOLL geschlossen ist (live = reload = backtest, keine Zukunftsschau). ──
        private int _revPendBar = -1;   // Bar mit noch unbestaetigter Reversal-Rohkandidatin
        private int _revPendVal = 0;    // signierter Roh-Score der Kandidatin
        private RevDbg _revPendDbg;     // Treiber-Snapshot der Kandidatin
        private int _freshRev = 0;      // in DIESER ProcessClosedBar emittierte Reversal (fuer Bridge)

        // ── ALARM (Telegram via ATAS-Alarme) ───────────────────────────────
        // Latch: erst nach dem Historien-Nachladen alarmieren -> kein Spam alter Umkehren.
        private bool _histDone;
        private bool _alertOnReversal = true;
        private bool _alertLong = true;
        private bool _alertShort = true;
        private string _alertSound = "alert1.wav";

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
        private bool _detectorVolumeFilter = true;    // Volumen-Akzeptanz: nur echte Auktions-Balances
        private decimal _detectorPocFactor = 1.5m;    // klarer vPOC: POC-Vol >= Faktor * Ø Level-Vol
        private decimal _detectorMinVolFactor = 0.7m; // genug Volumen: Range-Ø-Bar-Vol >= Faktor * Umfeld
        private bool _detectorMerge = true;           // benachbarte, ueberlappende Balances vereinen
        private int _detectorMergeGapBars = 20;       // max. Lueckenbars zwischen zwei Ranges fuers Merging
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
        // Betrag = Gewichtssumme der dominanten Seite (Staerke = gefeuerte Bedingungen).
        private readonly List<int> _signals = new();
        private int _lastSignalBar = -1;

        // Tape: pro Bar netto-signiertes Big-Trade-Volumen (Buy +, Sell -).
        // Wird live aus OnCumulativeTrade (Fremd-Thread) befuellt -> Concurrent.
        private readonly ConcurrentDictionary<int, int> _tapeNet = new();

        // Big-Trade-Levels: grosse Prints als verteidigte Levels. Aus OnCumulativeTrade
        // (Fremd-Thread) befuellt -> per Lock geschuetzt.
        private sealed class BigLevel { public decimal Price; public int Volume; public int BuyVol; public int SellVol; public int Dir; public int Bar; public bool Armed; public bool Hit; }
        private readonly object _bigLock = new();
        private readonly List<BigLevel> _bigLevels = new();
        private int _bigMaxLevels = 20;   // Obergrenze sichtbarer Levels (gegen Zumuellen)
        private bool _bigReqDone;      // Historien-Anfrage pro Instanz nur einmal (erstes Laden)
        private bool _bigDirty;        // Big-Trade-Setting geaendert -> Levels neu aus Historie holen
        private bool _bigReplaying;    // waehrend historischem Hit-Nachspielen -> keine Alarme
        private int _bigReqId;         // Request-ID zum Zuordnen der Antwort

        // Reversal: pro Bar signierter Reversal-Score (> 0 Long-Umkehr, < 0 Short).
        private readonly List<int> _revSignals = new();
        private int _lastRevBar = -1;   // Cooldown-Tracking fuer Reversal-Marker

        // Balance-Range (Value Area) — zuletzt berechnete Werte fuer das Rendering.
        private decimal _rangeVah, _rangeVal, _rangeVpoc;
        private int _rangeStartBar;
        private bool _rangeValid;

        // Range-Detektor: erkannte Konsolidierungs-Boxen.
        private struct DetRange { public int Start, End; public decimal High, Low; public int Dir; public decimal Poc; }
        private readonly List<DetRange> _detRanges = new();   // EINGEFRORENE Ranges (kein Repaint)

        // Inkrementeller Detektor-State: nur die aktive Kandidaten-Range waechst live.
        private int _lastDetBar = -1;
        private bool _candActive;
        private int _candStart;
        private decimal _candHi, _candLo, _candWidth;

        // Imbalance-Zonen: gestapelte Footprint-Imbalances (Bid/Ask-Dominanz) als
        // verteidigte Preiszonen. Aus historischen Candle-Clustern -> ueberlebt Reload.
        private sealed class ImbZone { public decimal Low, High; public int Dir; public int Bar; public int Stack; public bool Armed; public bool Filled; public bool Alerted; }
        private readonly List<ImbZone> _imbZones = new();
        private bool _imbZonesEnabled = false;
        private int _imbZoneMinStack = 3;        // min. konsekutive imbalanced Level
        private int _imbSkipEdgeLevels = 1;      // Docht-Rand ignorieren (Finished-Auction-Tips)
        private bool _imbIncludeVoids = true;    // Single Prints / Volumen-Voids ueberbruecken den Stack
        private int _imbVoidThreshold = 0;       // Level gilt als Void, wenn Gesamtvolumen <= Schwelle (0 = nur echte 0/0)
        private decimal _imbZoneRatio = 3.0m;    // Ask >= Ratio*Bid (bzw. umgekehrt); 0-Gegenseite = extrem
        private int _imbZoneMaxZones = 20;
        private int _imbZoneArmTicks = 8;        // Preis muss so weit weg sein, bevor Re-Test zaehlt
        private bool _imbZoneAlertOnHit = true;
        private Color _colorImbBuy = Color.FromArgb(60, 70, 200, 120);
        private Color _colorImbSell = Color.FromArgb(60, 230, 100, 100);

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
        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Lookback (Bars)", GroupName = "Allgemein", Order = 100,
            Description = "Kalibrierungs-Fenster: ueber so viele Bars werden die Perzentil-Schwellen bestimmt.")]
        [Range(10, 1000)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(10, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Schwelle (Gewichtspunkte)", GroupName = "Signal", Order = 200,
            Description = "Mindest-Gewichtssumme der dominanten Seite, damit ein Marker feuert. " +
                          "Bei Default-Gewichten (Summe ~100) ist 50 = Mehrheit. (Standard: 50)")]
        [Range(0, 300)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 300.0, Step = 5.0)]
        public int SignalThreshold { get => _signalThreshold; set { _signalThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Cooldown (Bars)", GroupName = "Signal", Order = 202,
            Description = "Mindestabstand zwischen Markern. Rausch-Bremse fuer Tick-Charts. 0 = aus.")]
        [Range(0, 100)]
        public int SignalCooldownBars { get => _signalCooldownBars; set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "HUD anzeigen", GroupName = "HUD & Panel", Order = 110,
            Description = "Blendet das Info-Panel (HUD) mit Scores, Kalibrierung und Status ein/aus.")]
        public bool ShowHud { get => _showHud; set { _showHud = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Marker anzeigen", GroupName = "Marker", Order = 210,
            Description = "Zeigt die Bull/Bear-Signal-Marker (Dreiecke). Betrifft NICHT die Reversal-Rauten (eigener Schalter im Reiter Reversal).")]
        public bool ShowMarkers { get => _showMarkers; set { _showMarkers = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Kalibrierung im HUD zeigen", GroupName = "HUD & Panel", Order = 112,
            Description = "Zeigt die aktuellen Kalibrierungs-Schwellen (Volumen/Delta/Absorption) im HUD.")]
        public bool ShowCalibration { get => _showCalibration; set { _showCalibration = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Min-Score für Marker", GroupName = "Marker", Order = 212,
            Description = "Nur Marker mit Score >= diesem Wert zeichnen. 60 = mind. 3 Bedingungen " +
                          "ausgerichtet (versteckt die schwachen 50/55). 0 = alle. (Standard: 60)")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 100.0, Step = 5.0)]
        public int MinMarkerScore { get => _minMarkerScore; set { _minMarkerScore = Math.Clamp(value, 0, 100); RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Kalibrierung
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Globaler Perzentil", GroupName = "Kalibrierung", Order = 230,
            Description = "Schwelle = dieses Perzentil der letzten N Bars. 85 = feuert in den oberen 15%. (Standard: 95)")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        public int GlobalPercentile { get => _globalPercentile; set { _globalPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Advanced: Perzentil pro Bedingung", GroupName = "Kalibrierung", Order = 232,
            Description = "Aus = globaler Wert fuer alle. Ein = je Bedingung eigener Perzentil unten.")]
        public bool UseAdvancedPercentiles { get => _useAdvancedPercentiles; set { _useAdvancedPercentiles = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Freeze (Kalibrierung einfrieren)", GroupName = "Kalibrierung", Order = 234,
            Description = "Friert die aktuellen Schwellen ein (wie ein gespeichertes Template). Aus = rollend live.")]
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

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Volumen", GroupName = "Kalibrierung", Order = 236,
            Description = "Perzentil-Schwelle nur fuer Volumen (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int VolPercentile { get => _volPercentile; set { _volPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Delta", GroupName = "Kalibrierung", Order = 238,
            Description = "Perzentil-Schwelle nur fuer Delta (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int DeltaPercentile { get => _deltaPercentile; set { _deltaPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Absorption", GroupName = "Kalibrierung", Order = 240,
            Description = "Perzentil-Schwelle nur fuer Absorption (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int AbsPercentile { get => _absPercentile; set { _absPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Bedingungen
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Delta aktiv", GroupName = "Bedingung: Delta", Order = 250,
            Description = "Bedingung Delta in die Bull/Bear-Wertung einbeziehen.")]
        public bool DeltaEnabled { get => _deltaEnabled; set { _deltaEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Delta Gewicht", GroupName = "Bedingung: Delta", Order = 252,
            Description = "Gewichtspunkte der Delta-Bedingung in der Gesamt-Score.")]
        [Range(0, 100)]
        public int DeltaWeight { get => _deltaWeight; set { _deltaWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Volumen aktiv", GroupName = "Bedingung: Volumen", Order = 254,
            Description = "Bedingung Relatives Volumen in die Wertung einbeziehen.")]
        public bool VolEnabled { get => _volEnabled; set { _volEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Volumen Gewicht", GroupName = "Bedingung: Volumen", Order = 256,
            Description = "Gewichtspunkte der Volumen-Bedingung.")]
        [Range(0, 100)]
        public int VolWeight { get => _volWeight; set { _volWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Absorption aktiv", GroupName = "Bedingung: Absorption", Order = 258,
            Description = "Bedingung Absorption (Footprint) in die Wertung einbeziehen.")]
        public bool AbsEnabled { get => _absEnabled; set { _absEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Absorption Gewicht", GroupName = "Bedingung: Absorption", Order = 260,
            Description = "Footprint-Absorption: groesstes Level-Delta ueber Schwelle. Richtung = Reversal gegen den Aggressor.")]
        [Range(0, 100)]
        public int AbsWeight { get => _absWeight; set { _absWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "VWAP aktiv", GroupName = "Bedingung: VWAP", Order = 262,
            Description = "Bedingung VWAP-Bias in die Wertung einbeziehen.")]
        public bool VwapEnabled { get => _vwapEnabled; set { _vwapEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "VWAP Gewicht", GroupName = "Bedingung: VWAP", Order = 264,
            Description = "Bias: Close ueber Session-VWAP = bullish, darunter = bearish. VWAP ankert taeglich (IsNewSession).")]
        [Range(0, 100)]
        public int VwapWeight { get => _vwapWeight; set { _vwapWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance aktiv", GroupName = "Bedingung: Imbalance", Order = 266,
            Description = "Bedingung Diagonale Imbalance in die Wertung einbeziehen.")]
        public bool ImbEnabled { get => _imbEnabled; set { _imbEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Gewicht", GroupName = "Bedingung: Imbalance", Order = 268,
            Description = "Gewichtspunkte der Imbalance-Bedingung.")]
        [Range(0, 100)]
        public int ImbWeight { get => _imbWeight; set { _imbWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Ratio", GroupName = "Bedingung: Imbalance", Order = 270,
            Description = "Diagonale Schwelle: Ask[p] >= Ratio * Bid[p-Tick] (Buy) bzw. umgekehrt. Default 2.0 = 200%. (Standard: 2.0)")]
        [Range(1.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal ImbRatio { get => _imbRatio; set { _imbRatio = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Mindest-Anzahl", GroupName = "Bedingung: Imbalance", Order = 272,
            Description = "Mindestanzahl diagonaler Imbalances auf der dominanten Seite (gestapelt).")]
        [Range(1, 50)]
        public int ImbMinCount { get => _imbMinCount; set { _imbMinCount = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "vPOC-in-Wick aktiv", GroupName = "Bedingung: vPOC", Order = 274,
            Description = "Bedingung vPOC-im-Docht in die Wertung einbeziehen.")]
        public bool VpocEnabled { get => _vpocEnabled; set { _vpocEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "vPOC Gewicht", GroupName = "Bedingung: vPOC", Order = 276,
            Description = "POC im unteren Docht = bullish (Kaeufer-Rejection), oberer Docht = bearish.")]
        [Range(0, 100)]
        public int VpocWeight { get => _vpocWeight; set { _vpocWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape aktiv (LIVE-ONLY)", GroupName = "Bedingung: Tape", Order = 278,
            Description = "Big Trades ab Mindestgroesse. Erfasst NUR live ab Laden vorwaerts (keine Historie). " +
                          "Buy = bullish, Sell = bearish.")]
        public bool TapeEnabled { get => _tapeEnabled; set { _tapeEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape Gewicht", GroupName = "Bedingung: Tape", Order = 280,
            Description = "Gewichtspunkte der Tape-Bedingung (Big Trades live).")]
        [Range(0, 100)]
        public int TapeWeight { get => _tapeWeight; set { _tapeWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape Mindest-Kontrakte", GroupName = "Bedingung: Tape", Order = 282,
            Description = "Ein einzelner Cumulative-Trade ab dieser Groesse zaehlt als Big Trade. Pro Instrument tunen.")]
        [Range(1, 100000)]
        public int TapeMinContracts { get => _tapeMinContracts; set { _tapeMinContracts = Math.Max(1, value); RecalculateValues(); } }

        // ── Reversal-Engine ────────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal aktiv", GroupName = "Reversal", Order = 300,
            Description = "Eigene Umkehr-Logik an lokalen Extrema (Divergenz, Absorption, vPOC-Docht, Exhaustion). Eigener Rauten-Marker.")]
        public bool ReversalEnabled { get => _reversalEnabled; set { _reversalEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Marker anzeigen", GroupName = "Reversal", Order = 301,
            Description = "Zeigt die Reversal-Rauten. Unabhaengig vom Signal-Marker-Schalter (Reiter Signal).")]
        public bool ShowReversalMarkers { get => _showReversalMarkers; set { _showReversalMarkers = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Diagnose (Hover)", GroupName = "Reversal", Order = 306,
            Description = "An = beim Hovern ueber eine Raute erscheint ein farbcodierter Tooltip mit den Treibern (hell = gefeuert): DIV/ABS/VP/EXH/SPD/IMB/AUC, plus Effizienz (eff) und Impuls-Flag (IMP). Zum Diagnostizieren, warum eine Raute kam.")]
        public bool ShowRevDebug { get => _showRevDebug; set { _showRevDebug = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal Lookback (Bars)", GroupName = "Reversal", Order = 302,
            Description = "Fenster fuer Extrem-/Divergenz-Referenz: neues Tief/Hoch ueber so viele Bars = Umkehr-Kandidat.")]
        [Range(2, 200)]
        public int ReversalLookback { get => _reversalLookback; set { _reversalLookback = Math.Max(2, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Schwelle (%)", GroupName = "Reversal", Order = 304,
            Description = "Mindest-Reversal-Score, damit eine Raute feuert. (Standard: 70)")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 100.0, Step = 5.0)]
        public int ReversalThreshold { get => _reversalThreshold; set { _reversalThreshold = Math.Clamp(value, 0, 100); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Delta-Divergenz", GroupName = "Treiber-Gewichte", Order = 310,
            Description = "Gewicht der CVD-Divergenz als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevDivWeight { get => _revDivWeight; set { _revDivWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Absorption am Extrem", GroupName = "Treiber-Gewichte", Order = 312,
            Description = "Gewicht der Absorption am Extrem als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevAbsWeight { get => _revAbsWeight; set { _revAbsWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht vPOC-im-Docht", GroupName = "Treiber-Gewichte", Order = 314,
            Description = "Gewicht von vPOC-im-Docht als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevVpocWeight { get => _revVpocWeight; set { _revVpocWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Exhaustion", GroupName = "Treiber-Gewichte", Order = 316,
            Description = "Gewicht der Exhaustion (duennes Aggressor-Volumen am Extrem).")]
        [Range(0, 100)]
        public int RevExhWeight { get => _revExhWeight; set { _revExhWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Speed of Tape (Klimax)", GroupName = "Treiber-Gewichte", Order = 318,
            Description = "Speed-Spike am Extrem (schnelles Tape = Klimax/Kapitulation) staerkt die Umkehr. 0 = aus.")]
        [Range(0, 100)]
        public int RevSpeedWeight { get => _revSpeedWeight; set { _revSpeedWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Imbalance-Flip", GroupName = "Treiber-Gewichte", Order = 319,
            Description = "(A) Frischer gestapelter Imbalance-Flip am Extrem (am Tief Buy-Stack, am Hoch Sell-Stack = Aggression kippt). Docht-Rand ausgeschlossen. 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevImbWeight { get => _revImbWeight; set { _revImbWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Finished Auction", GroupName = "Treiber-Gewichte", Order = 319,
            Description = "(B) Auktion am Extrem ist abgeschlossen (kein Imbalance-Tip = Gegenseite hat gestoppt = Erschoepfung). Unfinished (Tip noch imbalanced) zaehlt nicht. 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevAuctionWeight { get => _revAuctionWeight; set { _revAuctionWeight = value; RecalculateValues(); } }

        // ── Zusatz-Treiber (Default-Gewicht 0 = neutral) ──────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Absorption Count", GroupName = "Zusatz-Treiber", Order = 330,
            Description = "(C) Mehrere absorbierte Level am Extrem (nicht nur das staerkste). 0 = aus (Default). Per-Kerze -> saturiert nicht.")]
        [Range(0, 100)]
        public int RevAcntWeight { get => _revAcntWeight; set { _revAcntWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Absorption Count: min. Level", GroupName = "Zusatz-Treiber", Order = 331,
            Description = "Ab wie vielen absorbierten Leveln der Treiber feuert ('X of Y'-Schwelle). Ein Level zaehlt ab 50% des staerksten Level-Deltas. Default 2.")]
        [Range(1, 10)]
        public int RevAcntMin { get => _revAcntMin; set { _revAcntMin = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "FILTER: nur mit AC# >= min", GroupName = "Zusatz-Treiber", Order = 350,
            Description = "HARTER Filter (unabhaengig vom Gewicht): laesst nur Umkehren durch, die mindestens 'min. Level' absorbierte Level haben. Zum sauberen Testen der AC#-Hypothese. Achtung: reduziert die Signalmenge stark.")]
        public bool RevFilterAcnt { get => _revFilterAcnt; set { _revFilterAcnt = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "FILTER: nur mit KL-Konfluenz", GroupName = "Zusatz-Treiber", Order = 351,
            Description = "HARTER Filter: laesst nur Umkehren durch, deren Extrem nahe an einem KeyLevel liegt (Toleranz = KL-Konfluenz-Einstellung). Braucht den Level-Export im KeyLevels-Indikator. Zum sauberen Testen der KL-Hypothese.")]
        public bool RevFilterKl { get => _revFilterKl; set { _revFilterKl = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Volume Power Bar", GroupName = "Zusatz-Treiber", Order = 332,
            Description = "(D) Power-Kerze: hohes Volumen + Kerzenkoerper in Umkehr-Richtung. 0 = aus (Default). Per-Kerze -> saturiert nicht.")]
        [Range(0, 100)]
        public int RevPwrWeight { get => _revPwrWeight; set { _revPwrWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Power Bar: Volumen-Faktor", GroupName = "Zusatz-Treiber", Order = 333,
            Description = "Kerzen-Volumen >= Faktor * Ø-Bar-Volumen im Fenster, damit es als Power-Kerze zaehlt. Default 1.5.")]
        [Range(1.0, 10.0)]
        public decimal RevPwrVolMult { get => _revPwrVolMult; set { _revPwrVolMult = Math.Max(1.0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Kerzen-Delta", GroupName = "Zusatz-Treiber", Order = 334,
            Description = "(E) Netto-Delta der Umkehrkerze in Umkehr-Richtung (Long: Kaeufer-Delta am Tief, Short: Verkaeufer-Delta am Hoch). 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevCdeltaWeight { get => _revCdeltaWeight; set { _revCdeltaWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Kerzen-Delta: Faktor", GroupName = "Zusatz-Treiber", Order = 335,
            Description = "Kerzen-Delta muss >= Faktor * Ø-|Bar-Delta| sein (in Umkehr-Richtung). Default 0.5.")]
        [Range(0.0, 5.0)]
        public decimal RevCdeltaFactor { get => _revCdeltaFactor; set { _revCdeltaFactor = Math.Max(0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Speed-Spike Faktor (x Ø-Speed)", GroupName = "Treiber-Gewichte", Order = 320,
            Description = "Spike, wenn der Bar-Tape-Speed (Ticks/Sekunde) >= Faktor * Durchschnitt im Fenster. (Standard: 1.5)")]
        [Range(1.0, 10.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal RevSpeedFactor { get => _revSpeedFactor; set { _revSpeedFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Exhaustion-Faktor", GroupName = "Treiber-Gewichte", Order = 321,
            Description = "Exhaustion feuert, wenn Aggressor-Volumen am Extrem <= Faktor * Ø-Level-Volumen. Niedriger = strenger (nur wirklich duennes Volumen). Standard: 0.5 = wie bisher.")]
        [Range(0.1, 1.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.1, 1.0, Step = 0.05, DisplayFormat = "0.00")]
        public decimal RevExhFactor { get => _revExhFactor; set { _revExhFactor = Math.Clamp(value, 0.1m, 1.0m); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls-Filter (kein Gegentrend-Picken)", GroupName = "Impuls-Filter", Order = 330,
            Description = "In einem gesunden, gerichteten Impuls braucht die Umkehr echte CVD-Divergenz (Absorption allein reicht nicht). Filtert Trend-Picks. Default AUS.")]
        public bool RevImpulseFilter { get => _revImpulseFilter; set { _revImpulseFilter = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls-Effizienz-Schwelle", GroupName = "Impuls-Filter", Order = 332,
            Description = "Ab dieser Effizienz (|Netto-Weg|/Pfad, 0..1) gilt das Bein als gesunder Impuls. Hoeher = nur sehr gerichtete Impulse gelten. (Standard: 0.5)")]
        [Range(0.1, 1.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.1, 1.0, Step = 0.05, DisplayFormat = "0.00")]
        [VisibleWhen(nameof(RevImpulseFilter), true)]
        public decimal RevImpulseEff { get => _revImpulseEff; set { _revImpulseEff = Math.Clamp(value, 0.1m, 1.0m); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls: min. Divergenz (x Ø-Bar-Delta)", GroupName = "Impuls-Filter", Order = 334,
            Description = "Im Impuls muss der CVD-Bruch >= Faktor * Ø-Bar-Delta sein (echte Erschoepfung statt Rauschen). Hoeher = strenger. Nur aktiv mit Impuls-Filter. (Standard: 1.5)")]
        [Range(0.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        [VisibleWhen(nameof(RevImpulseFilter), true)]
        public decimal RevDivMinFactor { get => _revDivMinFactor; set { _revDivMinFactor = Math.Max(0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Folgekerzen-Bestätigung (2-Kerzen)", GroupName = "Bestaetigung & Kanten", Order = 340,
            Description = "Umkehr nur, wenn die naechste Kerze in Umkehr-Richtung schliesst. Hoehere Qualitaet, 1 Bar Verzoegerung.")]
        public bool RevConfirm { get => _revConfirm; set { _revConfirm = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Nur an Range-Kanten (Phase 2b)", GroupName = "Bestaetigung & Kanten", Order = 342,
            Description = "Reiner Display-Filter: zeigt Reversal-Rauten nur, wenn das Extrem nahe einer Range-Kante (High/Low/vPOC) liegt. Default AUS -> aendert nichts.")]
        public bool RevEdgeOnly { get => _revEdgeOnly; set { _revEdgeOnly = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Kanten-Toleranz (Ticks)", GroupName = "Bestaetigung & Kanten", Order = 344,
            Description = "Wie nah das Reversal-Extrem an einer Range-Kante liegen muss (in Ticks).")]
        [Range(0, 100)]
        [VisibleWhen(nameof(RevEdgeOnly), true)]
        public int RevEdgeTolerance { get => _revEdgeTolerance; set { _revEdgeTolerance = Math.Max(0, value); RedrawChart(); } }

        // ── KeyLevels-Konfluenz (reine Markierung) ─────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "KeyLevels-Konfluenz markieren", GroupName = "KeyLevels-Konfluenz", Order = 360,
            Description = "An = Reversals nahe an einem KeyLevel werden hervorgehoben (heller/groesserer Halo + 'KL'-Tag, im Hover das Level). Aendert die Signal-MENGE NICHT. Voraussetzung: in KeyLevels den 'Level-Export' aktivieren (Reiter Sync).")]
        public bool KlConfluence { get => _klConfluence; set { _klConfluence = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "KeyLevels-Toleranz (Ticks)", GroupName = "KeyLevels-Konfluenz", Order = 362,
            Description = "Max. Abstand des Umkehr-Extrems zum KeyLevel, damit es als konfluent gilt. Default 4.")]
        [Range(0, 50)]
        [VisibleWhen(nameof(KlConfluence), true)]
        public int KlTolTicks { get => _klTolTicks; set { _klTolTicks = Math.Max(0, value); RedrawChart(); } }

        // ── Auto-Bridge ────────────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Signal-Export (Auto-Bridge)", GroupName = "Auto-Bridge", Order = 370,
            Description = "An = pro geschlossenem LIVE-Bar wird das Signal (Momentum + Reversal) nach %APPDATA%\\ATAS\\ofs_signals\\<Instrument>.txt geschrieben. Die OrderflowAuto-Strategie liest genau DIESES getunte Signal (kein Hosting, keine Divergenz). Reine Ausgabe, aendert die Signal-Logik nicht.")]
        public bool BridgeExport { get => _bridgeExport; set { _bridgeExport = value; } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Bridge-Kennung (optional)", GroupName = "Auto-Bridge", Order = 371,
            Description = "PFLICHT, wenn mehrere Charts DESSELBEN Instruments exportieren (z.B. Renko24 und 900T auf NQ) - sonst ueberschreiben sie sich gegenseitig! Wird an den Dateinamen gehaengt: Kennung 'renko24' -> ofs_signals\\NQ_renko24.txt. In der Strategie dann bei 'Signal von Instrument' exakt 'NQ_renko24' eintragen. Leer = nur <Instrument>.txt.")]
        [VisibleWhen(nameof(BridgeExport), true)]
        public string BridgeKey { get => _bridgeKey; set { _bridgeKey = value ?? ""; } }

        // ── Backtest-Log ───────────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Backtest-Log (CSV)", GroupName = "Backtest", Order = 380,
            Description = "An = jede Umkehr wird mit allen Treibern + auto-gemessenem Ausgang (MFE/MAE/Net ueber N Bars) in %APPDATA%\\ATAS\\ofs_backtest\\<Instrument>.csv geschrieben. Historie laden -> anhaken -> ganze Historie wird protokolliert. Reine Diagnose, keine Signal-Aenderung.")]
        public bool BtLog { get => _btLog; set { _btLog = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Log-Horizont (Bars)", GroupName = "Backtest", Order = 381,
            Description = "Ueber wie viele geschlossene Bars der Ausgang (MFE/MAE/Net) gemessen wird. Default 15.")]
        [Range(3, 100)]
        [VisibleWhen(nameof(BtLog), true)]
        public int BtHorizon { get => _btHorizon; set { _btHorizon = Math.Max(3, value); RecalculateValues(); } }

        // ── Position-Tool (SL/TP-Boxen an Signalen, rein visuell) ──────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Position-Tool zeichnen", GroupName = "Position-Tool", Order = 390,
            Description = "An = an jedem Signal wird eine Entry-Linie + gruene TP-Zone + rote SL-Zone eingezeichnet und der Ausgang per First-Touch ausgewertet (Rahmen gruen=TP zuerst, rot=SL zuerst). Keine Order.")]
        public bool PosTool { get => _posTool; set { _posTool = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Signal-Quelle", GroupName = "Position-Tool", Order = 389,
            Description = "Welche Signale das Tool auswertet: Reversal (Rauten) oder Signal (Momentum-Dreiecke).")]
        [VisibleWhen(nameof(PosTool), true)]
        public PosSource PosSignalSource { get => _posSource; set { _posSource = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Statistik-Summenzeile", GroupName = "Position-Tool", Order = 396,
            Description = "Zeigt unten links W/L, Trefferquote und Netto-Ticks/Punkte ueber alle geladenen Signale (fuer die aktuellen SL/TP).")]
        [VisibleWhen(nameof(PosTool), true)]
        public bool PosShowStats { get => _posShowStats; set { _posShowStats = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Kosten pro Trade (Ticks)", GroupName = "Position-Tool", Order = 396,
            Description = "Kommission + Slippage pro Trade, wird vom Netto abgezogen (realistischere Zahl). Default 3.")]
        [Range(0.0, 20.0)]
        [VisibleWhen(nameof(PosTool), true)]
        public decimal PosCostTicks { get => _posCostTicks; set { _posCostTicks = Math.Max(0m, value); RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Breakeven bei +Ticks (0=aus)", GroupName = "Position-Tool", Order = 402,
            Description = "Zieht den SL auf Entry, sobald der Trade +X Ticks im Plus war. Verlierer die vorher +X liefen werden dann Scratch (0) statt -SL. 0 = aus.")]
        [Range(0, 500)]
        [VisibleWhen(nameof(PosTool), true)]
        public int PosBeTrigger { get => _posBeTrigger; set { _posBeTrigger = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Ausgang wie Auto-Strategie", GroupName = "Position-Tool", Order = 403,
            Description = "AN = Entry auf dem OPEN des Bars nach dem massgeblichen Close (so steigt die Strategie ein), AUS = auf dem massgeblichen Close selbst. Massgeblicher Close = Close der Bestaetigungskerze (wenn Bestaetigung an) bzw. des Signal-Bars (wenn aus). Fuer exakte Deckung denselben Breakeven-Trigger wie in der Strategie setzen.")]
        [VisibleWhen(nameof(PosTool), true)]
        public bool PosLikeAuto { get => _posLikeAuto; set { _posLikeAuto = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Session-Zeitzone (Std von UTC)", GroupName = "Position-Tool", Order = 397,
            Description = "Verschiebt die Kerzenzeit (UTC) in deine lokale Zeit fuer die Session-Zuordnung. Deutschland Sommer = +2 (CEST).")]
        [Range(-12, 14)]
        [VisibleWhen(nameof(PosTool), true)]
        public int SessTz { get => _sessTz; set { _sessTz = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Asia Start (Std, lokal)", GroupName = "Position-Tool", Order = 398, Description = "Beginn Asia-Session in lokaler Zeit. Default 2 (02:00).")]
        [Range(0, 23)]
        [VisibleWhen(nameof(PosTool), true)]
        public int SessAsia { get => _sessAsia; set { _sessAsia = Math.Clamp(value, 0, 23); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "London Start (Std, lokal)", GroupName = "Position-Tool", Order = 399, Description = "Beginn London-Session (= Ende Asia). Default 8 (08:00).")]
        [Range(0, 23)]
        [VisibleWhen(nameof(PosTool), true)]
        public int SessLon { get => _sessLon; set { _sessLon = Math.Clamp(value, 0, 23); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "New York Start (Std, lokal)", GroupName = "Position-Tool", Order = 400, Description = "Beginn NY-Session (= Ende London). Default 15 (15:00).")]
        [Range(0, 23)]
        [VisibleWhen(nameof(PosTool), true)]
        public int SessNy { get => _sessNy; set { _sessNy = Math.Clamp(value, 0, 23); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "New York Ende (Std, lokal)", GroupName = "Position-Tool", Order = 401, Description = "Ende NY-Session. Default 23 (23:00).")]
        [Range(0, 24)]
        [VisibleWhen(nameof(PosTool), true)]
        public int SessNyEnd { get => _sessNyEnd; set { _sessNyEnd = Math.Clamp(value, 1, 24); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "SL (Ticks)", GroupName = "Position-Tool", Order = 391, Description = "Stop-Abstand vom Entry in Ticks. Default 50.")]
        [Range(1, 1000)]
        [VisibleWhen(nameof(PosTool), true)]
        public int PosSlTicks { get => _posSlTicks; set { _posSlTicks = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "TP (Ticks)", GroupName = "Position-Tool", Order = 392, Description = "Ziel-Abstand vom Entry in Ticks. Default 100.")]
        [Range(1, 2000)]
        [VisibleWhen(nameof(PosTool), true)]
        public int PosTpTicks { get => _posTpTicks; set { _posTpTicks = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Box-Breite (Bars)", GroupName = "Position-Tool", Order = 393, Description = "Wie weit die Boxen nach rechts reichen (in Bars). Default 20.")]
        [Range(3, 300)]
        [VisibleWhen(nameof(PosTool), true)]
        public int PosBoxBars { get => _posBoxBars; set { _posBoxBars = Math.Max(3, value); RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "TP-Zone Farbe", GroupName = "Position-Tool", Order = 394)]
        [VisibleWhen(nameof(PosTool), true)]
        public Color PosTpColor { get => _posTpColor; set { _posTpColor = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "SL-Zone Farbe", GroupName = "Position-Tool", Order = 395)]
        [VisibleWhen(nameof(PosTool), true)]
        public Color PosSlColor { get => _posSlColor; set { _posSlColor = value; RedrawChart(); } }

        // ── Alarm (Telegram) ───────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Umkehr (Telegram)", GroupName = "Alarm", Order = 350,
            Description = "Loest bei bestaetigter Umkehr einen ATAS-Alarm aus (geht ueber die ATAS-Telegram-Anbindung). Nur live, nicht rueckwirkend.")]
        public bool AlertOnReversal { get => _alertOnReversal; set { _alertOnReversal = value; } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Long-Umkehr", GroupName = "Alarm", Order = 352,
            Description = "Alarm nur fuer Long-Umkehren (Boden) senden.")]
        public bool AlertLong { get => _alertLong; set { _alertLong = value; } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Short-Umkehr", GroupName = "Alarm", Order = 354,
            Description = "Alarm nur fuer Short-Umkehren (Top) senden.")]
        public bool AlertShort { get => _alertShort; set { _alertShort = value; } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Alarm-Sound (Datei)", GroupName = "Alarm (global)", Order = 130,
            Description = "Sound-Datei fuer den ATAS-Alarm (z.B. alert1.wav).")]
        public string AlertSound { get => _alertSound; set { _alertSound = value; } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "▶ Test-Alarm jetzt senden", GroupName = "Alarm (global)", Order = 132,
            Description = "Loest sofort einen Test-Alarm aus (zum Pruefen der Telegram-Anbindung, ohne auf eine Umkehr zu warten).")]
        public bool AlertTest
        {
            get => false;   // wie ein Taster: zeigt sich immer als 'aus'
            set
            {
                if (!value) return;
                AlertsEnabled = true;
                try { AddAlert(_alertSound, $"Orderflow TEST-Alarm | {InstrumentInfo?.Instrument} | {DateTime.Now:HH:mm:ss}"); }
                catch { }
            }
        }

        // ── Big-Trade-Levels ───────────────────────────────────────────────
        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Big-Trade-Levels aktiv", GroupName = "Erkennung", Order = 500,
            Description = "Grosse Prints als verteidigte Levels markieren + Alarm beim Re-Test. LIVE-ONLY (braucht Tick-Trade-Daten).")]
        public bool BigEnabled { get => _bigEnabled; set { if (value != _bigEnabled) { _bigEnabled = value; _bigDirty = true; RedrawChart(); } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Einzeltrades (separated)", GroupName = "Erkennung", Order = 502,
            Description = "An = jeder einzelne Print >= Schwelle zaehlt (separated). Aus = kumulativ (aufeinanderfolgende Trades am gleichen Preis zusammengefasst).")]
        public bool BigSeparated { get => _bigSeparated; set { _bigSeparated = value; } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Min-Kontrakte London", GroupName = "Erkennung", Order = 504,
            Description = "Mindestgroesse eines Big Trades im London-Fenster.")]
        [Range(1, 100000)]
        public int BigMinLondon { get => _bigMinLondon; set { int v = Math.Max(1, value); if (v != _bigMinLondon) { _bigMinLondon = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Min-Kontrakte US/Default", GroupName = "Erkennung", Order = 506,
            Description = "Mindestgroesse ausserhalb des London-Fensters (US-Session etc.).")]
        [Range(1, 100000)]
        public int BigMinDefault { get => _bigMinDefault; set { int v = Math.Max(1, value); if (v != _bigMinDefault) { _bigMinDefault = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "London-Fenster Start (Stunde)", GroupName = "Erkennung", Order = 508,
            Description = "Stunde (Chart-Zeitzone), ab der die London-Schwelle gilt.")]
        [Range(0, 23)]
        public int BigLondonStartHour { get => _bigLondonStartHour; set { int v = Math.Clamp(value, 0, 23); if (v != _bigLondonStartHour) { _bigLondonStartHour = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "London-Fenster Ende (Stunde)", GroupName = "Erkennung", Order = 510,
            Description = "Stunde (Chart-Zeitzone), bis zu der die London-Schwelle gilt.")]
        [Range(0, 23)]
        public int BigLondonEndHour { get => _bigLondonEndHour; set { int v = Math.Clamp(value, 0, 23); if (v != _bigLondonEndHour) { _bigLondonEndHour = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Arm-Distanz (Ticks)", GroupName = "Re-Test & Hit", Order = 520,
            Description = "So weit muss der Preis vom Level weg sein, bevor ein erneutes Anlaufen als Re-Test/Hit zaehlt.")]
        [Range(0, 1000)]
        public int BigArmTicks { get => _bigArmTicks; set { int v = Math.Max(0, value); if (v != _bigArmTicks) { _bigArmTicks = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Hit-Toleranz (Ticks)", GroupName = "Re-Test & Hit", Order = 522,
            Description = "Wie nah der Preis ans Level kommen muss, damit es als getroffen gilt (auch fuers Zusammenfassen am gleichen Preis).")]
        [Range(0, 100)]
        public int BigHitTolerance { get => _bigHitTolerance; set { int v = Math.Max(0, value); if (v != _bigHitTolerance) { _bigHitTolerance = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Alarm bei Level-Hit (Telegram)", GroupName = "Re-Test & Hit", Order = 524,
            Description = "ATAS-/Telegram-Alarm ausloesen, wenn ein Big-Trade-Level erneut angelaufen wird.")]
        public bool BigAlertOnHit { get => _bigAlertOnHit; set { _bigAlertOnHit = value; } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Levels nach Hit behalten (gestrichelt)", GroupName = "Darstellung", Order = 530,
            Description = "An = gehittete Levels bleiben als gedimmte, gestrichelte Referenz sichtbar. Aus = werden nach dem Hit entfernt.")]
        public bool BigKeepAfterHit { get => _bigKeepAfterHit; set { _bigKeepAfterHit = value; RedrawChart(); } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Max. sichtbare Levels", GroupName = "Darstellung", Order = 532,
            Description = "Obergrenze gleichzeitig gezeichneter Big-Trade-Levels. Aeltere fallen raus, wenn ueberschritten. Niedriger = weniger Zumuellung.")]
        [Range(1, 200)]
        public int BigMaxLevels
        {
            get => _bigMaxLevels;
            set
            {
                _bigMaxLevels = Math.Clamp(value, 1, 200);
                lock (_bigLock)
                {
                    while (_bigLevels.Count > _bigMaxLevels && _bigLevels.Count > 0)
                        _bigLevels.RemoveAt(0);
                }
                RedrawChart();
            }
        }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Alle Big-Levels loeschen", GroupName = "Darstellung", Order = 534,
            Description = "Button: entfernt alle aktuell gezeichneten Big-Trade-Levels. (Bei Chart-Reload werden sie aus der Historie neu aufgebaut.)")]
        public bool BigClearAll
        {
            get => false;
            set
            {
                if (!value) return;
                lock (_bigLock) { _bigLevels.Clear(); }
                RedrawChart();
            }
        }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Farbe Big-Buy-Level", GroupName = "Darstellung", Order = 540,
            Description = "Farbe der Big-Buy-Level (Kaeufer-verteidigt).")]
        public Color ColorBigBuy { get => _colorBigBuy; set { _colorBigBuy = value; RedrawChart(); } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Farbe Big-Sell-Level", GroupName = "Darstellung", Order = 542,
            Description = "Farbe der Big-Sell-Level (Verkaeufer-verteidigt).")]
        public Color ColorBigSell { get => _colorBigSell; set { _colorBigSell = value; RedrawChart(); } }

        // ── Imbalance-Zonen ────────────────────────────────────────────────
        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Imbalance-Zonen aktiv", GroupName = "Erkennung", Order = 600,
            Description = "Gestapelte Footprint-Imbalances (Bid/Ask-Dominanz) als verteidigte Preiszonen markieren. Aus historischen Candle-Clustern -> ueberlebt Reload. Default aus.")]
        public bool ImbZonesEnabled { get => _imbZonesEnabled; set { _imbZonesEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Min-Stack (konsekutive Level)", GroupName = "Erkennung", Order = 602,
            Description = "So viele direkt benachbarte Preislevel muessen dieselbe Seite imbalanced sein, damit eine Zone entsteht (Footprint-Standard). Standard: 3.")]
        [Range(2, 20)]
        [NumericEditor(NumericEditorTypes.TrackBar, 2, 20, Step = 1)]
        public int ImbZoneMinStack { get => _imbZoneMinStack; set { _imbZoneMinStack = Math.Clamp(value, 2, 20); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Docht-Rand ignorieren (Level)", GroupName = "Erkennung", Order = 603,
            Description = "So viele Preislevel am Kerzen-Hoch UND -Tief werden ignoriert (Imbalances direkt am Docht = Finished-Auction-Tips, uninteressant). Gilt auch fuer den Reversal-Imbalance-Flip. Standard: 1.")]
        [Range(0, 10)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0, 10, Step = 1)]
        public int ImbSkipEdgeLevels { get => _imbSkipEdgeLevels; set { _imbSkipEdgeLevels = Math.Clamp(value, 0, 10); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Single Prints / Volumen-Voids einbeziehen", GroupName = "Erkennung", Order = 605,
            Description = "An = Level ohne fairen Handel (kein/duennes Volumen) UEBERBRUECKEN den Stack (Ineffizienz), statt ihn zu unterbrechen. Ein Level mit echtem zweiseitigem Handel beendet die Zone. Default an.")]
        public bool ImbIncludeVoids { get => _imbIncludeVoids; set { _imbIncludeVoids = value; RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Void-Schwelle (Volumen)", GroupName = "Erkennung", Order = 607,
            Description = "Ein Level gilt als Void, wenn sein Gesamtvolumen (Bid+Ask) <= diesem Wert ist. 0 = nur echte 0/0-Level. Hoeher = auch sehr duenne Level als Void werten. Standard: 0.")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0, 100, Step = 1)]
        [VisibleWhen(nameof(ImbIncludeVoids), true)]
        public int ImbVoidThreshold { get => _imbVoidThreshold; set { _imbVoidThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Imbalance-Ratio", GroupName = "Erkennung", Order = 604,
            Description = "Ask[p] >= Ratio * Bid[p-Tick] (Buy) bzw. umgekehrt. 0 auf der Gegenseite = extreme Imbalance (Single Print). Standard: 3.0.")]
        [Range(1.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 20.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal ImbZoneRatio { get => _imbZoneRatio; set { _imbZoneRatio = Math.Max(1m, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Max. sichtbare Zonen", GroupName = "Erkennung", Order = 606,
            Description = "Obergrenze gleichzeitig gehaltener Zonen. Aeltere fallen raus. Standard: 20.")]
        [Range(1, 200)]
        public int ImbZoneMaxZones { get => _imbZoneMaxZones; set { _imbZoneMaxZones = Math.Clamp(value, 1, 200); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Arm-Distanz (Ticks)", GroupName = "Re-Test & Hit", Order = 610,
            Description = "So weit muss der Preis von der Zone weg sein, bevor ein erneutes Anlaufen als Re-Test/Hit zaehlt. Standard: 8.")]
        [Range(0, 1000)]
        public int ImbZoneArmTicks { get => _imbZoneArmTicks; set { _imbZoneArmTicks = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Alarm bei Zonen-Hit (Telegram)", GroupName = "Re-Test & Hit", Order = 612,
            Description = "ATAS-/Telegram-Alarm ausloesen, wenn der Preis eine offene Imbalance-Zone wieder anlaeuft.")]
        public bool ImbZoneAlertOnHit { get => _imbZoneAlertOnHit; set { _imbZoneAlertOnHit = value; } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Farbe Buy-Zone", GroupName = "Darstellung", Order = 620,
            Description = "Fuellfarbe fuer Buy-Imbalance-Zonen (Ask-Dominanz).")]
        public Color ColorImbBuy { get => _colorImbBuy; set { _colorImbBuy = value; RedrawChart(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Farbe Sell-Zone", GroupName = "Darstellung", Order = 622,
            Description = "Fuellfarbe fuer Sell-Imbalance-Zonen (Bid-Dominanz).")]
        public Color ColorImbSell { get => _colorImbSell; set { _colorImbSell = value; RedrawChart(); } }

        // ── Balance-Range ──────────────────────────────────────────────────
        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Balance-Range zeichnen", GroupName = "Referenzband", Order = 430,
            Description = "Value Area (VAH/VAL/vPOC) ueber ein rollendes Fenster zeichnen.")]
        public bool RangeEnabled { get => _rangeEnabled; set { _rangeEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Range Lookback (Bars)", GroupName = "Referenzband", Order = 432,
            Description = "Anzahl Bars fuer das Volumen-Profil der Value Area.")]
        [Range(10, 2000)]
        public int RangeLookback { get => _rangeLookback; set { _rangeLookback = Math.Max(10, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Value-Area Anteil (%)", GroupName = "Referenzband", Order = 434,
            Description = "Anteil des Volumens in der Value Area (Standard 70%). (Standard: 70)")]
        [Range(30, 95)]
        [NumericEditor(NumericEditorTypes.TrackBar, 30.0, 95.0, Step = 5.0)]
        public int RangeValuePct { get => _rangeValuePct; set { _rangeValuePct = Math.Clamp(value, 30, 95); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Linien nach rechts verlaengern", GroupName = "Referenzband", Order = 436,
            Description = "VAH/VAL/vPOC-Linien nach rechts bis zum aktuellen Bar verlaengern.")]
        public bool RangeExtendRight { get => _rangeExtendRight; set { _rangeExtendRight = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range-Band", GroupName = "Farben", Order = 450,
            Description = "Fuellfarbe des Referenzband-Bereichs (Value Area).")]
        public Color ColorRangeBand { get => _colorRangeBand; set { _colorRangeBand = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range-Raender", GroupName = "Farben", Order = 452,
            Description = "Farbe der Referenzband-Raender (VAH/VAL).")]
        public Color ColorRangeEdge { get => _colorRangeEdge; set { _colorRangeEdge = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe vPOC", GroupName = "Farben", Order = 454,
            Description = "Farbe der vPOC-Linie im Referenzband.")]
        public Color ColorRangePoc { get => _colorRangePoc; set { _colorRangePoc = value; RedrawChart(); } }

        // ── Range-Detektor ─────────────────────────────────────────────────
        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Range-Detektor aktiv", GroupName = "Detektor", Order = 400,
            Description = "Diskrete Konsolidierungen (Balance nach Imbalance) erkennen und als Box markieren.")]
        public bool DetectorEnabled { get => _detectorEnabled; set { _detectorEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Detektor Lookback (Bars)", GroupName = "Detektor", Order = 402,
            Description = "Wie viele Bars der Detektor rueckwaerts nach Konsolidierungen absucht.")]
        [Range(20, 5000)]
        public int DetectorLookback { get => _detectorLookback; set { _detectorLookback = Math.Max(20, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Min-Bars pro Range", GroupName = "Detektor", Order = 404,
            Description = "So viele Bars muss der Preis im engen Band bleiben, damit es als Balance zaehlt.")]
        [Range(3, 200)]
        public int DetectorMinBars { get => _detectorMinBars; set { _detectorMinBars = Math.Max(3, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Breiten-Faktor (x ATR)", GroupName = "Detektor", Order = 406,
            Description = "Max. Range-Hoehe = Faktor * ATR (stabil, kein Drift). Kleiner = engere Balances. (Standard: 3.0)")]
        [Range(0.5, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.5, 20.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal DetectorWidthFactor { get => _detectorWidthFactor; set { _detectorWidthFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Breiten-ATR Periode", GroupName = "Detektor", Order = 408,
            Description = "Periode fuer die ATR, die die Range-Breite bestimmt (fix je Range -> kein Repaint).")]
        [Range(2, 200)]
        public int DetectorAtrPeriod { get => _detectorAtrPeriod; set { _detectorAtrPeriod = Math.Max(2, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Volumen-Akzeptanz (Auktions-Balance)", GroupName = "Detektor", Order = 410,
            Description = "Range nur gueltig, wenn sie genug Volumen UND einen klaren vPOC hat (echte Balance statt nur 'Preis war eng').")]
        public bool DetectorVolumeFilter { get => _detectorVolumeFilter; set { _detectorVolumeFilter = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Klarer vPOC Faktor", GroupName = "Detektor", Order = 412,
            Description = "POC-Level-Volumen muss >= Faktor * Ø Level-Volumen sein (gepeakte Verteilung). Hoeher = strenger. (Standard: 1.5)")]
        [Range(1.0, 10.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        [VisibleWhen(nameof(DetectorVolumeFilter), true)]
        public decimal DetectorPocFactor { get => _detectorPocFactor; set { _detectorPocFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Min-Volumen Faktor (vs Umfeld)", GroupName = "Detektor", Order = 414,
            Description = "Range-Ø-Bar-Volumen muss >= Faktor * Umfeld-Ø-Bar-Volumen sein (kein toter Drift). 0 = aus. (Standard: 0.7)")]
        [Range(0.0, 5.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 5.0, Step = 0.25, DisplayFormat = "0.00")]
        [VisibleWhen(nameof(DetectorVolumeFilter), true)]
        public decimal DetectorMinVolFactor { get => _detectorMinVolFactor; set { _detectorMinVolFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Boxen zusammenführen (Merging)", GroupName = "Detektor", Order = 416,
            Description = "Benachbarte, preislich ueberlappende Balances zu einer Box vereinen (gegen Aufsplittung).")]
        public bool DetectorMerge { get => _detectorMerge; set { _detectorMerge = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Merge max. Lücke (Bars)", GroupName = "Detektor", Order = 418,
            Description = "Hoechstens so viele Bars duerfen zwischen zwei Ranges liegen, damit sie vereint werden.")]
        [Range(0, 200)]
        [VisibleWhen(nameof(DetectorMerge), true)]
        public int DetectorMergeGapBars { get => _detectorMergeGapBars; set { _detectorMergeGapBars = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Break Up", GroupName = "Farben", Order = 456,
            Description = "Farbe fuer nach oben aufgeloeste (gebrochene) Detektor-Boxen.")]
        public Color ColorBreakUp { get => _colorBreakUp; set { _colorBreakUp = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Break Down", GroupName = "Farben", Order = 458,
            Description = "Farbe fuer nach unten aufgeloeste (gebrochene) Detektor-Boxen.")]
        public Color ColorBreakDn { get => _colorBreakDn; set { _colorBreakDn = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range aktiv/flat", GroupName = "Farben", Order = 460,
            Description = "Farbe fuer aktive/laufende Detektor-Boxen (noch nicht gebrochen).")]
        public Color ColorFlat { get => _colorFlat; set { _colorFlat = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Schriftgroesse", GroupName = "HUD & Panel", Order = 114,
            Description = "Schriftgroesse des HUD-Panels.")]
        [Range(8, 30)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 30); BuildFonts(); RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Oben Links (aus = Oben Rechts)", GroupName = "HUD & Panel", Order = 116,
            Description = "HUD oben links statt oben rechts andocken.")]
        public bool TopLeft { get => _topLeft; set { _topLeft = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Abstand X (px)", GroupName = "HUD & Panel", Order = 118,
            Description = "Horizontaler Abstand des HUD vom Chartrand (px).")]
        [Range(0, 600)]
        public int OffsetX { get => _offsetX; set { _offsetX = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Abstand Y (px)", GroupName = "HUD & Panel", Order = 120,
            Description = "Vertikaler Abstand des HUD vom Chartrand (px).")]
        [Range(0, 600)]
        public int OffsetY { get => _offsetY; set { _offsetY = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Marker-Abstand (Ticks)", GroupName = "Marker", Order = 214,
            Description = "Abstand der Marker vom Kerzen-Extrem in Ticks.")]
        [Range(0, 100)]
        public int MarkerTickOffset { get => _markerTickOffset; set { _markerTickOffset = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Bull", GroupName = "Marker", Order = 216,
            Description = "Farbe der Bull-Signal-Marker.")]
        public Color ColorBull { get => _colorBull; set { _colorBull = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Bear", GroupName = "Marker", Order = 218,
            Description = "Farbe der Bear-Signal-Marker.")]
        public Color ColorBear { get => _colorBear; set { _colorBear = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Neutral", GroupName = "Marker", Order = 220,
            Description = "Farbe fuer neutrale/schwache Signale.")]
        public Color ColorNeutral { get => _colorNeutral; set { _colorNeutral = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Hintergrund", GroupName = "HUD & Panel", Order = 122,
            Description = "Hintergrundfarbe des HUD-Panels.")]
        public Color ColorBackground { get => _colorBackground; set { _colorBackground = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Farbe Reversal Long", GroupName = "Farben", Order = 360,
            Description = "Farbe der Long-Reversal-Rauten (Boden).")]
        public Color ColorRevBull { get => _colorRevBull; set { _colorRevBull = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Farbe Reversal Short", GroupName = "Farben", Order = 362,
            Description = "Farbe der Short-Reversal-Rauten (Top).")]
        public Color ColorRevBear { get => _colorRevBear; set { _colorRevBear = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public OrderflowSignal() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;
            AlertsEnabled = true;   // ATAS-Alarme aktiv (Telegram laeuft ueber die ATAS-Anbindung)

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
            // Ab hier ist die Historie nachgeholt -> ab jetzt duerfen Alarme feuern
            // (waehrend der obigen Schleife war _histDone == false = stumm).
            _histDone = true;

            if (bar != CurrentBar - 1)
                return;

            // Big-Trade-Levels aus der Historie rekonstruieren (einmal pro Laden),
            // damit sie ein Chart-Reload / Hot-Reload ueberleben (Live-Stream wird
            // fuer die Historie nicht neu abgespielt).
            if (_bigEnabled && CurrentBar > 1 && (!_bigReqDone || _bigDirty))
            {
                _bigReqDone = true;
                _bigDirty = false;
                RequestBigLevelHistory();
            }

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
            _revPendBar = -1; _revPendVal = 0; _freshRev = 0;
            _hoverRevBar = -1;
            _revDbgByBar.Clear();
            _btPending.Clear();
            _posPend.Clear(); _posOutcome.Clear();
            Array.Clear(_psW, 0, 3); Array.Clear(_psL, 0, 3); Array.Clear(_psAmb, 0, 3); Array.Clear(_psBe, 0, 3); Array.Clear(_psOpen, 0, 3); Array.Clear(_psNet, 0, 3);
            _posResolved.Clear(); Array.Clear(_psMaxW, 0, 3); Array.Clear(_psMaxL, 0, 3); Array.Clear(_psMaxDD, 0, 3); _posStreakN = -1; _posStreakCost = -1m;
            _histDone = false;   // erst nach erneutem Historien-Nachladen wieder alarmieren
            // _bigReqDone NICHT zuruecksetzen: Levels ueberleben einen reinen Recalc.
            // Neu-Anfrage nur bei frischer Instanz (_bigReqDone==false) oder _bigDirty.
            _rangeValid = false;
            _detRanges.Clear();
            _lastDetBar = -1;
            _candActive = false;
            _imbZones.Clear();   // werden aus der Historie (Candle-Cluster) neu erkannt
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

            UpdateBacktestLog(bar, c);   // offene Backtest-Eintraege mit diesem Bar fortschreiben/abschliessen
            ResolvePosPending(bar, c);   // offene Position-Tool-Trades per First-Touch aufloesen

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
            {
                _lastSignalBar = bar;
                if (_posSource == PosSource.Signal) AddPosPending(bar, c, GetSignal(bar));   // Position-Tool: Momentum-Signal
            }

            int rawRev = RevEvaluate(bar, c, signedMld, _cumDeltaRun);   // setzt _revCand
            RevDbg rawDbg = _revCand;
            if (rawRev != 0 && !PassesRevFilters(c, rawRev, rawDbg))
                rawRev = 0;   // harte Test-Filter (AC# / KL-Konfluenz)
            _freshRev = 0;   // wird von EmitReversal gesetzt, wenn in diesem Bar eine Umkehr emittiert wird

            if (_revConfirm)
            {
                // REPAINT-FREI: die Folgekerze der Vorbar-Kandidatin ist JETZT (bar) voll geschlossen.
                if (_revPendBar == bar - 1 && _revPendVal != 0)
                {
                    if (RevConfirmed(bar - 1, Math.Sign(_revPendVal)))
                        EmitReversal(bar - 1, _revPendVal, _revPendDbg);
                    else
                        SetRevSignal(bar - 1, 0);   // Bestaetigung endgueltig gescheitert
                }
                // aktuelle Rohkandidatin fuer die naechste Runde vormerken; `bar` selbst noch offen.
                _revPendBar = bar; _revPendVal = rawRev; _revPendDbg = rawDbg;
                SetRevSignal(bar, 0);
            }
            else
            {
                // ohne Bestaetigung: sofort emittieren (nutzt nur <=bar -> ebenfalls deterministisch).
                EmitReversal(bar, rawRev, rawDbg);
            }

            // Imbalance-Zonen: erst offene Zonen gegen diesen Bar pruefen, dann neue erkennen.
            ProcessImbZones(bar, c, detectNew: true);

            // Auto-Bridge: die in DIESEM Bar frisch (bestaetigt) emittierte Umkehr rausschreiben (nur live).
            if (_bridgeExport && _histDone)
                WriteBridge(bar, c, GetSignal(bar), _freshRev);
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

            // Big-Trade-Levels: Re-Test/Hit gegen den Live-Bar pruefen (Alarm).
            CheckBigLevelHits(c, last);
            // Imbalance-Zonen: Live-Approach/Fill gegen den Live-Bar (keine neuen Zonen live).
            ProcessImbZones(last, c, detectNew: false);
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

        // Oeffentliche Accessoren, damit eine hostende Strategy dieselben Signale lesen kann (Single Source of Truth).
        public int ReversalSignalAt(int bar) => GetRevSignal(bar);      // signierter Reversal-Score (>0 Long, <0 Short)
        public int MomentumSignalAt(int bar) => GetSignal(bar);         // signierter Momentum-Signal-Score

    }
}
