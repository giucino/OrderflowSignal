# Orderflow Signal (Multi-Condition) — ATAS Indikator

Eigenentwickelter ATAS-Indikator (C#) für Futures (ES/MES, NQ/MNQ). Verrechnet
mehrere **Orderflow-Bedingungen** zu einem **richtungsgewichteten Bull/Bear-Score**
und markiert Konfluenz direkt an der Kerze. Schwellen per **Perzentil-Auto-
Kalibrierung** (rollend, mit Freeze). Für **Tick-Charts (500/900T)**, **Renko** und
**Zeitcharts (M5)**. **Rein informativ — kein Entry-Signal.**

> Teil eines mehrstufigen Projekts (**Stufe 2 — Trigger/Execution**). Setzt unter
> der Kontext-Schicht (Market State / Bias Dashboard, M5/M15) an: M5 = Überblick,
> Tick/Renko = Entry.

## Was er macht

Pro Bar werden bis zu vier Bedingungen geprüft. Jede liefert eine **Richtung**
(bullish / bearish) und steuert ihr **Gewicht** zur jeweiligen Seite bei. Die Summen
ergeben `Bull` vs. `Bear`; ab der **Signal-Schwelle** auf der dominanten Seite feuert
ein Marker.

| # | Bedingung | Metrik | Aktiv wenn | Richtung |
|---|---|---|---|---|
| 1 | **Delta** | \|Candle-Delta\| | ≥ kalibrierte Schwelle | Vorzeichen des Deltas |
| 2 | **Volumen** | Candle-Volumen | ≥ kalibrierte Schwelle | Kerzenkörper (Close vs Open) |
| 3 | **Absorption** | größtes Level-Delta (Ask−Bid je Preislevel) | ≥ kalibrierte Schwelle | **Reversal** gegen den Aggressor (−sign) |
| 4 | **VWAP-Lage** | Close vs Session-VWAP | VWAP vorhanden | über/unter VWAP |

### Perzentil-Auto-Kalibrierung

Statt fester Faktoren werden die Schwellen für Delta, Volumen und Absorption als
**Perzentil der letzten *N* Bars** bestimmt (Default P85, N50) — robust gegen
Ausreißer und instrument-/timeframe-agnostisch. Eine Bedingung feuert, wenn ihre
Metrik in den oberen (100−P)% der jüngsten Bars liegt. (Konzept wie semaPHoreks
Auto-Calibration, aber direkt eingebaut — kein `.sph`-Export/Import.)

- **Rollend (Default):** Schwellen passen sich jeden Bar an.
- **Freeze:** friert die aktuellen Schwellen ein (wie ein festes Session-Template,
  z.B. getrennt für London / New York).
- **Basic / Advanced:** ein globaler Perzentil-Wert, optional pro Bedingung übersteuerbar.

### Warum Renko funktioniert

Absorption ist **footprint-basiert** (Level-Delta), nicht range-basiert — daher
**range-unabhängig** und auch auf Renko/Range-Charts gültig. Renko-Bricks tragen
echtes Volumen/Delta/Cluster pro Brick.

- **Session-VWAP** ankert täglich über `IsNewSession` (läuft in London **und** NY).
- **Bull/Bear statt Roh-Count:** Konfluenz *in eine Richtung*.
- **Signal-Cooldown** (Bars zwischen Markern) als Rausch-Bremse.

Anzeige: **HUD** (Bull/Bear-Punkte, Signal-Flag, Tags `Δ▲ Vol▲ Abs▼ VW▲`, kalibrierte
Schwellen wie ein „Result"-Panel) **+ Pfeil-Marker** an den Kerzen mit Konfluenz.

## Empfohlener Chart

**Tick 500/900T**, **Renko (z.B. 8R)** oder **M5**. **Nicht** auf Volumen-Bars
(Volumen je Bar konstant → Volumen-Perzentil bedeutungslos); davor warnt das HUD.

> Kalibrierung zuerst auf **MES** (liquide, ruhiger), dann Portierung auf NQ/MNQ —
> Code identisch, ggf. nur Perzentil/Lookback anpassen.

## Einstellungen (Kurzüberblick)

| Gruppe | Einstellung |
|---|---|
| Allgemein | Lookback, Signal-Schwelle, Signal-Cooldown, HUD/Marker/Kalibrierung an |
| Kalibrierung | Globaler Perzentil, Advanced-Override, Freeze, Perzentil je Bedingung |
| Bedingung: Delta / Volumen / Absorption / VWAP | je aktiv + Gewicht |
| Darstellung | Schriftgröße, Position, Abstände, Marker-Abstand |
| Farben | Bull / Bear / Neutral / Hintergrund |

Default-Gewichte: Delta 30, Volumen 20, Absorption 25, VWAP 25 (Summe 100),
Signal-Schwelle 50, Perzentil 85, Lookback 50. An echten Sessions kalibrieren.

## Hinweise

- Der **sich bildende Bar repaintet** bis zum Schluss (Signal kann kippen). Marker
  auf abgeschlossenen Bars sind stabil.
- Absorption ist als **Reversal** modelliert (Aggressor absorbiert → Gegenrichtung).
- Auf Charts ohne Footprint-Daten nutzt Absorption das Candle-Delta als Fallback.

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `OrderflowSignal.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikatorliste aktualisieren.

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. **Kein Handelssignal, keine
Anlageberatung** — Nutzung auf eigenes Risiko.
