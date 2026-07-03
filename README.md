# Orderflow Signal (Multi-Condition) — ATAS Indikator

Eigenentwickelter ATAS-Indikator (C#) für Futures (ES/MES, NQ/MNQ). Verrechnet
**sechs Orderflow-Bedingungen** zu einem **richtungsgewichteten Bull/Bear-Score**
(normiert 0–100 %) und markiert Konfluenz direkt an der Kerze — mit Stärke-Zahl am
Marker. Schwellen per **Perzentil-Auto-Kalibrierung** (rollend, mit Freeze). Für
**Tick-Charts (500/900T)**, **Renko** und **Zeitcharts (M5)**. **Rein informativ —
kein Entry-Signal.**

> Teil eines mehrstufigen Projekts (**Stufe 2 — Trigger/Execution**). Setzt unter
> der Kontext-Schicht (Market State / Bias Dashboard, M5/M15) an: M5 = Überblick,
> Tick/Renko = Entry.

## Was er macht

Pro Bar werden bis zu sieben Bedingungen geprüft (Tape standardmäßig aus). Jede liefert eine **Richtung** (bullish /
bearish) und steuert ihr **Gewicht** zur jeweiligen Seite bei. Die Summen werden auf
den Anteil der **aktiven** Gewichte normiert (0–100 %); ab der **Signal-Schwelle** auf
der dominanten Seite feuert ein Marker, dessen **Stärke-Zahl** den Score zeigt.

| # | Bedingung | Metrik | Aktiv wenn | Richtung | Gew. |
|---|---|---|---|---|---|
| 1 | **Delta** | \|Candle-Delta\| | ≥ Perzentil-Schwelle | Vorzeichen des Deltas | 20 |
| 2 | **Volumen** | Candle-Volumen | ≥ Perzentil-Schwelle | Kerzenkörper (Close vs Open) | 15 |
| 3 | **Absorption** | größtes Level-Delta (Ask−Bid je Preislevel) | ≥ Perzentil-Schwelle | **Reversal** gegen den Aggressor (−sign) | 20 |
| 4 | **VWAP-Lage** | Close vs Session-VWAP | VWAP vorhanden | über/unter VWAP | 15 |
| 5 | **Imbalance** | diagonale Ask/Bid-Imbalances (Ratio, gestapelt) | ≥ Mindest-Anzahl auf einer Seite | dominante Stapelseite | 15 |
| 6 | **vPOC-in-Wick** | POC-Lage zum Kerzenkörper | POC im Docht | unterer Docht = bull, oberer = bear | 15 |
| 7 | **Tape / Big Trades** ⚡ | einzelne Cumulative-Trades ≥ Mindestgröße | Big Trade vorhanden | Buy = bull, Sell = bear | 15 |

⚡ **Tape ist LIVE-ONLY** (erfasst Trades erst ab dem Laden vorwärts, keine Historie) und
deshalb **default AUS**. Zum Live-Traden einschalten + `Tape Mindest-Kontrakte` pro Instrument
tunen. Die orthogonale Bedingung (Trade-*Größe*, nicht Aggregat).

Gewichte summieren sich auf 100; der Score ist auf die aktiven Gewichte normiert,
bleibt also 0–100 % egal welche/wie viele Bedingungen aktiv sind.

### Perzentil-Auto-Kalibrierung

Schwellen für Delta, Volumen und Absorption = **Perzentil der letzten *N* Bars**
(Default P95, N50) — robust gegen Ausreißer, instrument-/timeframe-agnostisch. Eine
Bedingung feuert, wenn ihre Metrik in den oberen (100−P)% der jüngsten Bars liegt.
(Konzept wie semaPHoreks Auto-Calibration, aber direkt eingebaut — kein `.sph`-Export/
Import; läuft live auf jedem Chart, kein Template-Sprawl.)

- **Rollend (Default):** Schwellen passen sich jeden Bar an.
- **Freeze:** friert die aktuellen Schwellen ein (wie ein Session-Template, z.B. London / NY).
- **Basic / Advanced:** ein globaler Perzentil, optional pro Bedingung übersteuerbar.

### Reversal-Engine (getrennt vom Momentum)

Die sechs Bedingungen oben modellieren **Momentum/Continuation** — an echten Umkehrpunkten
heben sich Momentum (bearish am Tief) und Umkehr-Tells gegenseitig auf → kein Signal. Die
**Reversal-Engine** rechnet diese Umkehr-Logik **getrennt** und zeichnet einen eigenen
**Rauten-Marker** (◆, türkis = Long-Umkehr am Tief, orange = Short-Umkehr am Hoch):

- Greift nur an **lokalen Extrema** (neues Tief/Hoch über `Reversal Lookback` Bars).
- **Kumulative Delta-Divergenz:** tieferes Tief, aber das CVD macht ein höheres Tief
  (netto-Kaufen während des Falls = Verkäufer erschöpfen, *obwohl* der Preis noch fällt).
  Orderflow läuft dem Preis voraus → präziser als preisbasierte Range-/Balance-Tools.
- **Echte Absorption am Extrem:** großes Aggressor-Delta **und** das Extrem wurde
  *abgelehnt* (Close in der gegenüberliegenden Bar-Hälfte → Aggressor getrappt).
- **vPOC-im-Docht:** Rejection.
- **Exhaustion:** dünnes Aggressor-Volumen am Extrem (Bid am Tief / Ask am Hoch).

**Pflicht-Treiber:** Eine Umkehr feuert nur, wenn **CVD-Divergenz ODER echte Absorption**
vorliegt (die zwei Reversal-Archetypen: graduelle Erschöpfung vs. Klimax/Absorption).
vPOC und Exhaustion sind nur *Bestätigung*. So fallen Trend-„Treppen" weg, die weder
Divergenz noch echte Absorption haben.

**Impuls-Filter gegen Gegentrend-Picken** (`Impuls-Filter`, Toggle, **Default AUS**):
Misst die **Effizienz** des Beins ins Extrem (|Netto-Weg| / Pfadlänge). Ist das ein
gesunder, gerichteter Impuls (Effizienz ≥ `Impuls-Effizienz-Schwelle`), reicht Absorption
allein nicht — es braucht dann echte **CVD-Divergenz**, und die muss **signifikant** sein
(CVD-Bruch ≥ `min. Divergenz` × Ø-Bar-Delta, kein Rauschen). Filtert Konter-Trend-Picks in
laufenden Trends heraus, ohne echte Erschöpfungs-Umkehren zu verlieren. Wirkt rückwirkend
(Historie wird neu berechnet).

**Telegram-Alarm bei Umkehr** (`Alarm bei Umkehr`, Default aus): löst bei **bestätigter**
Umkehr einen ATAS-Alarm aus (läuft über die native ATAS-Telegram-Anbindung), optional nur
Long- oder nur Short-Umkehren. Nur live ab dem Laden, nicht rückwirkend (Anti-Spam-Latch).

**2-Kerzen-Bestätigung** (`Folgekerzen-Bestätigung`, Default an): Eine Umkehr wird erst
gültig, wenn die **Folgekerze in Umkehr-Richtung schließt** (Long-Raute: nächste Kerze
höher; Short: tiefer). Kandidaten, die sofort weiterliefen, fallen raus — 1 Bar Verzögerung
für deutlich höhere Qualität. Live bleibt die Raute „pending", bis die Bestätigungskerze schließt.

Eigener Score (0–100 %), feuert ab `Reversal-Schwelle`. Gewichte je Teil-Bedingung einstellbar.

### Range-Detektor (H-Range-Stil)

Sucht **diskrete Konsolidierungen** (Balance nach Imbalance) und markiert jede als **Box**,
gefärbt nach **Ausbruchsrichtung**: grün = hoch ausgebrochen, rot = runter, grau = noch aktiv.

**Geometrisch gehärtet (zuverlässig, kein Repaint):**
- **Inkrementell + eingefroren:** abgeschlossene Ranges werden **fixiert** und nie neu gerechnet
  → keine sich verschiebende Historie. Nur die aktive Range wächst live.
- **Break nur per Close:** eine Range bricht erst, wenn eine Bar **außerhalb des Bandes schließt** —
  Docht-Ausbrüche brechen sie nicht. Break-Farbe ist immer per Close bestätigt.
- **Stabile ATR-Breite:** Bandhöhe = `Breiten-Faktor` × ATR (`Breiten-ATR Periode`), **je Range bei
  Start fixiert** → kein driftendes Maß. `Min-Bars pro Range` Bars Mindestdauer.

**Auktions-validiert (echte Balance statt nur „Preis war eng"):**
- **Volumen-Akzeptanz** (`Volumen-Akzeptanz`, Default an): Range nur gültig bei **klarem vPOC**
  (POC-Level-Vol ≥ `Klarer-vPOC-Faktor` × Ø Level-Vol = gepeakte Verteilung) **und** **genug Volumen**
  (Range-Ø-Bar-Vol ≥ `Min-Volumen-Faktor` × Umfeld). Jede Box bekommt ihre **vPOC-Linie** (gestrichelt).
- **Box-Merging** (`Boxen zusammenführen`, Default an): benachbarte (≤ `Merge max. Lücke`) und
  preislich überlappende (≥ 50 %) Balances werden zu **einer** Box vereint; vPOC über die ganze Spanne.

### Balance-Range (Value Area, optionale Referenz)

Zeichnet alternativ **eine** rollende **Value Area** (VAH/VAL/vPOC) als Band — reine Fair-Value-
Referenz, kein diskreter Detektor. `Range Lookback`, `Value-Area Anteil %`. **Default AUS**.

### Phase 2b — Reversals nur an Range-Kanten

`Nur an Range-Kanten` (Toggle, **Default AUS**): reiner **Display-Filter** — bei „an" werden nur
Reversal-Rauten gezeigt, deren Extrem innerhalb `Kanten-Toleranz` (Ticks) an einer **Range-Kante**
(High / Low / vPOC) liegt = Sniper-Entries an den Balance-Kanten. Ändert die gespeicherten
Signale **nicht**; aus = bestehendes Verhalten unverändert.

### Big-Trade-Levels (verteidigte Preise + Re-Test-Alarm)

Markiert **große Einzeltrades** (separated Prints, nicht kumulativ) als horizontale **Levels** —
der Preis, an dem ein großer Player Position aufgebaut/verteidigt hat. Grün = Big-Buy, rot =
Big-Sell; die **Kontraktzahl** steht am Level.

- **Session-abhängige Schwelle:** `Min-Kontrakte London` (z.B. 20–30) im London-Fenster
  (`London-Fenster Start/Ende`), sonst `Min-Kontrakte US/Default` (z.B. 40). Der Indikator wählt
  automatisch nach Uhrzeit.
- **Re-Test-Alarm:** Verlässt der Preis das Level um `Arm-Distanz` (Ticks) und **läuft es später
  wieder an** (`Hit-Toleranz`), feuert ein einmaliger **Telegram-Alarm** (`Alarm bei Level-Hit`).
- **Nach Hit behalten** (`Levels nach Hit behalten`, Default an): getroffene Levels bleiben als
  **gedimmte, gestrichelte** Referenz sichtbar — man sieht, ob der Preis dort hält oder bricht.
- **Historien-Rekonstruktion:** Die Levels werden beim Laden aus historischen Trades neu aufgebaut
  (`RequestForCumulativeTrades`) → sie **überleben Chart-Reload / Hot-Reload** (nicht nur live).
- **Aufräumen:** `Max. sichtbare Levels` begrenzt die Anzahl; **Ctrl + Links-Klick** auf eine Linie
  löscht sie einzeln; Button `Alle Big-Levels löschen` räumt alle weg. Big-Buy und Big-Sell am
  **selben Preis** werden zu **einer** Linie zusammengefasst (Label zeigt Buy/Sell-Split).

### Signalqualität

- **Min-Score-Filter** (Default 60 %): blendet schwache Signale (z.B. nur VWAP + eine
  Bedingung) aus, zeigt nur Konfluenz aus mehreren ausgerichteten Bedingungen.
- **Signal-Cooldown** (Bars zwischen Markern) als Rausch-Bremse.
- **Bull/Bear statt Roh-Count:** Konfluenz *in eine Richtung*, nicht nur „X Lichter".

### Warum Renko funktioniert

Absorption, Imbalance und vPOC sind **footprint-basiert** (Level-Daten), nicht
range-basiert — daher **range-unabhängig** und auch auf Renko/Range-Charts gültig.
Session-VWAP ankert täglich über `IsNewSession` (läuft in London **und** NY).

Anzeige: **HUD** (Bull/Bear-%, Signal-Flag, Tags `Δ▲ Vol▲ Abs▼ VW▲ Im▲ vP·`,
kalibrierte Schwellen wie ein „Result"-Panel) **+ Pfeil-Marker** mit Stärke-Zahl.

## Empfohlener Chart

**Tick 500/900T**, **Renko (z.B. 8R)** oder **M5**. **Nicht** auf Volumen-Bars
(Volumen je Bar konstant → Volumen-Perzentil bedeutungslos); davor warnt das HUD.

> Pro Chart die Settings als **ATAS-Vorlage** speichern („Als Vorlage speichern")
> statt eigener Dateien — die Auto-Kalibrierung übernimmt die Schwellen automatisch.
> Erst auf **MES** kalibrieren, dann NQ/MNQ — Code identisch.

## Einstellungen (Kurzüberblick)

Die Settings sind in **Reiter** organisiert (wie ATAS-Standardindikatoren, „Daten"/„Visualization"),
je Reiter aufklappbare **Untergruppen**. Jede Option hat einen **Tooltip**; ausgewählte Tuning-Werte
sind **Schieberegler** (Standardwert im Tooltip vermerkt); abhängige Optionen erscheinen nur, wenn ihr
Schalter aktiv ist.

| Reiter | Untergruppen / Inhalt |
|---|---|
| **Allgemein** | Allgemein (Lookback) · HUD & Panel (HUD an, Kalibrierung, Schrift, Position, Hintergrund) · Alarm (Sound, Test) |
| **Signal** | Signal (Schwelle, Cooldown) · Marker (Signal-Marker an, Min-Score, Abstand, Farben) · Kalibrierung (Perzentil global/Advanced/Freeze) · Bedingung: Delta / Volumen / Absorption / VWAP / Imbalance / vPOC / Tape (je aktiv + Gewicht) |
| **Reversal** | Reversal (aktiv, Reversal-Marker an, Lookback, Schwelle) · Treiber-Gewichte (Divergenz/Absorption/vPOC/Exhaustion/Speed) · Impuls-Filter (Effizienz, min. Divergenz) · Bestätigung & Kanten · Alarm · Farben |
| **Range** | Detektor (aktiv, Lookback, Min-Bars, Breiten-Faktor ×ATR, ATR-Periode, Volumen-Akzeptanz + vPOC-/Min-Vol-Faktor, Merging + Lücke) · Referenzband (Balance-Range, Value-Area %) · Farben |
| **Big Trades** | Erkennung (aktiv, separated, Min-Kontrakte London/US, London-Fenster) · Re-Test & Hit (Arm-Distanz, Hit-Toleranz, Alarm) · Darstellung (nach Hit behalten, Max-Levels, Alle löschen, Farben) |

Defaults: Gewichte Delta 20 / Volumen 15 / Absorption 20 / VWAP 15 / Imbalance 15 /
vPOC 15 (Summe 100), Signal-Schwelle 50, Min-Score 60, Perzentil 95, Lookback 50.
Marker: **Signal- und Reversal-Marker getrennt** schaltbar.

## Hinweise

- Der **sich bildende Bar repaintet** bis zum Schluss. Marker auf abgeschlossenen Bars sind stabil.
- Absorption ist als **Reversal** modelliert (Aggressor absorbiert → Gegenrichtung).
- Auf Charts ohne Footprint-Daten nutzen die Footprint-Bedingungen Fallbacks (Candle-Delta);
  Imbalance/vPOC benötigen Cluster-Daten und eine gültige Tick-Größe.
- Die **Tape-Bedingung** (#7) ist live-only. Die **Big-Trade-Levels** dagegen nutzen bereits
  `RequestForCumulativeTrades` (wie semaPHoreks „Get Historical Data") und überleben Reloads.
- Noch nicht enthalten (bewusst): **Finished Business** (unscharf definiert).

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `OrderflowSignal.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikator entfernen + neu hinzufügen (DLL-Reload).

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. **Kein Handelssignal, keine
Anlageberatung** — Nutzung auf eigenes Risiko.
