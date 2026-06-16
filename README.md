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

Pro Bar werden sechs Bedingungen geprüft. Jede liefert eine **Richtung** (bullish /
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

| Gruppe | Einstellung |
|---|---|
| Allgemein | Lookback, Signal-Schwelle, Signal-Cooldown, Min-Score, HUD/Marker/Kalibrierung an |
| Kalibrierung | Globaler Perzentil, Advanced-Override, Freeze, Perzentil je Bedingung |
| Bedingungen | Delta / Volumen / Absorption / VWAP / Imbalance (Ratio, Anzahl) / vPOC — je aktiv + Gewicht |
| Darstellung | Schriftgröße, Position, Abstände, Marker-Abstand |
| Farben | Bull / Bear / Neutral / Hintergrund |

Defaults: Gewichte Delta 20 / Volumen 15 / Absorption 20 / VWAP 15 / Imbalance 15 /
vPOC 15 (Summe 100), Signal-Schwelle 50, Min-Score 60, Perzentil 95, Lookback 50.

## Hinweise

- Der **sich bildende Bar repaintet** bis zum Schluss. Marker auf abgeschlossenen Bars sind stabil.
- Absorption ist als **Reversal** modelliert (Aggressor absorbiert → Gegenrichtung).
- Auf Charts ohne Footprint-Daten nutzen die Footprint-Bedingungen Fallbacks (Candle-Delta);
  Imbalance/vPOC benötigen Cluster-Daten und eine gültige Tick-Größe.
- Noch nicht enthalten (bewusst): **Tape / Big Trades** (braucht Tick-Trade-Daten, Phase 3),
  **Finished Business** (unscharf definiert).

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `OrderflowSignal.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikator entfernen + neu hinzufügen (DLL-Reload).

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. **Kein Handelssignal, keine
Anlageberatung** — Nutzung auf eigenes Risiko.
