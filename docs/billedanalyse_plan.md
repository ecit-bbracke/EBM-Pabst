# 🎯 Teknisk Plan: Billed- og Måltegningsanalyse for ebm-papst Datablade (Multimodal RAG)

**Dokument:** `billedanalyse_plan.md`  
**Dato:** 17. november 2026  
**Forfatter:** Ranger 1 (Arkitektur & Drift)  
**Status:** Godkendt til implementering  

---

## 1. 📋 Mission & Formål (Executive Summary)

ebm-papsts tekniske datablade er rige på data, der er opdelt i to grundlæggende formater:
1. **Strukturerede tabeller og rå tekst** (Side 1, 2, 3, 5): Nominelle data, elektriske specifikationer, godkendelser og klemrækker. Disse udtrækkes i dag succesfuldt via `PdfTextExtractor` (PdfPig).
2. **Grafiske CAD-måltegninger og kurvegrafer** (Side 4 og 7): Monteringsflanger, boltmønstre, indbygningsdybde, klemkassens orientering, luftmængde- og trykkurver ($q_v$ mod $p_{fs}$).

### Problemstillingen i dag ("The Vector Drawing Blindspot")
Den nuværende tekst-ekstraktor (`PdfPig`) ser kun tekst-strenge. På måltegninger (side 4) ligger målene ($\square 380$, $\square 330$, $\varnothing 11$, $294 \pm 3$) som grafiske vektorer og målepile. Tekst-ekstraktoren opfanger kun fodnoterne (f.eks. *"1 Installed position: shaft horizontal..."* og *"4 Inlet ring with pressure tap (k-factor: 98)"*), men **overser samtlige fysiske dimensioner i millimeter**.

### Formål med denne plan
At opgradere systemet fra ren tekst-RAG til en **Multimodal RAG-arkitektur**, hvor CAD-tegninger og kurveblade automatisk rasteriseres, analyseres af en Computer Vision-model, transformeres til strukturerede tekniske metadata og indekseres i Qdrant og SQLite.

---

## 2. 🏛️ Målarkitektur (End-to-End Flow)

```
┌────────────────────────────────────────────────────────────────────────┐
│                        PDF Upload / Ingestion                          │
│                                                                        │
│   ┌────────────────┐       ┌───────────────────────────────────────┐   │
│   │ Tekstsider     │       │ Grafiske sider (Side 4 måltegning,    │   │
│   │ (Side 1,2,3,5) │       │                 Side 7 kurver)        │   │
│   └───────┬────────┘       └───────────────────┬───────────────────┘   │
│           │                                    │                       │
│           ▼                                    ▼                       │
│   ┌────────────────┐       ┌───────────────────────────────────────┐   │
│   │ PdfPig         │       │ PDF Rasterizer (PDFium / SkiaSharp)   │   │
│   │ TextExtractor  │       │ Konverterer side til 300 DPI PNG      │   │
│   └───────┬────────┘       └───────────────────┬───────────────────┘   │
│           │                                    │                       │
│           │                                    ▼                       │
│           │                ┌───────────────────────────────────────┐   │
│           │                │ Multimodal Vision Model               │   │
│           │                │ (Gemini 2.5 Flash Vision / Claude)    │   │
│           │                │ Dechifrerer mål, flanger og kurver    │   │
│           │                └───────────────────┬───────────────────┘   │
│           │                                    │                       │
│           ▼                                    ▼                       │
│   ┌────────────────────────────────────────────────────────────────┐   │
│   │ Standard Chunks + Berigede Måltegnings-Chunks (Markdown/JSON) │   │
│   └───────────────────────────────┬────────────────────────────────┘   │
│                                   │                                    │
│                                   ▼                                    │
│   ┌────────────────────────────────────────────────────────────────┐   │
│   │ SQLite (Tekniske Metadata) + Qdrant (Hybrid Vektor/Tekst)      │   │
│   └────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 3. 🧩 Detaljerede Implementeringsfaser

### Fase 1: Selektiv Side-detektering & Rasterizer (`PdfPageRasterizer`)
For at undgå unødvendigt token-forbrug og ventetid skal **kun** sider med diagrammer og tegninger sendes til billedanalyse.

1. **Heuristisk side-routing**:
   * Sider, der indeholder overskrifterne `"Product drawing"` eller `"Maßzeichnung"` $\rightarrow$ **Måltegning (Side 4)**.
   * Sider, der indeholder `"Curves: Air performance"` eller `"Kennlinien"` $\rightarrow$ **Kurveblad (Side 7)**.
2. **Rasterisering til billede**:
   * Benytter `PDFtoImage` eller `Docnet.Core` (P/Invoke til Google PDFium) i .NET 10.
   * Genererer en højopløselig PNG ($300\text{ DPI}$) i hukommelsen (`MemoryStream`).

```csharp
public interface IPdfPageRasterizer
{
    Task<byte[]> RasterizePageToPngAsync(Stream pdfStream, int pageNumber, int dpi = 300, CancellationToken ct = default);
    Task<List<int>> IdentifyGraphicalPagesAsync(Stream pdfStream, CancellationToken ct = default);
}
```

---

### Fase 2: Vision Prompt & Ekstraktions-Skema (`TechnicalDrawingVisionAnalyzer`)
Billedet sendes til en multimodal model (f.eks. Gemini Flash via eksisterende `ILlmClient`) med en specialiseret systeminstruktion.

#### Systemprompt for Måltegninger (Side 4)
```text
You are a senior mechanical engineering draughtsman specializing in ebm-papst industrial fan datasheets.
Analyze the provided product drawing (CAD drawing) with absolute metric precision.

Extract all mechanical and electrical interface dimensions into structured JSON:
{
  "mounting_flange": {
    "outer_dimensions_mm": "string (e.g. 380x380)",
    "outer_shape": "square | circular | rectangular",
    "mounting_hole_pitch_mm": "string (e.g. 330x330)",
    "hole_diameter_mm": number (e.g. 11.0),
    "hole_count": number (e.g. 4),
    "flange_lip_depth_mm": number (e.g. 9.5),
    "edge_clearance_mm": number (e.g. 25.0)
  },
  "inlet_nozzle": {
    "throat_diameter_mm": number (e.g. 253.0),
    "nozzle_protrusion_mm": number (e.g. 15.0),
    "flowgrid_mount_holes": "string (e.g. 4x Ø4.5 at 30°)",
    "k_factor": number (e.g. 98)
  },
  "axial_depth": {
    "total_installation_depth_mm": number (e.g. 294.0),
    "depth_tolerance_mm": number (e.g. 3.0),
    "depth_behind_plate_mm": number (e.g. 229.9)
  },
  "cables": [
    {
      "designation": "PWR | CTRL",
      "cable_type": "string (e.g. PVC AWG18)",
      "conductor_count": number,
      "length_mm": number (e.g. 1000),
      "strip_length_mm": number (e.g. 85),
      "wire_ferrule": boolean
    }
  ],
  "permitted_mounting_positions": [
    "Shaft horizontal with struts vertical",
    "Rotor bottom"
  ],
  "markdown_summary_da": "Kompakt, præcis dansk sammenfatning af samtlige monterings- og flangemål til montøren."
}
```

---

### Fase 3: Strukturerede Modeller & DTO'er

```csharp
public record MountingFlangeSpecs(
    string OuterDimensionsMm,
    string HolePitchMm,
    double HoleDiameterMm,
    int HoleCount,
    double EdgeClearanceMm,
    double? FlangeLipDepthMm
);

public record InletNozzleSpecs(
    double ThroatDiameterMm,
    double ProtrusionDepthMm,
    double? KFactor,
    string? AccessoryMounting
);

public record ModuleDimensionsSpecs(
    double TotalDepthMm,
    double DepthBehindPlateMm,
    MountingFlangeSpecs MountingFlange,
    InletNozzleSpecs InletNozzle,
    List<CableSpec> Cables,
    string MarkdownSummaryDanish
);
```

---

### Fase 4: Berigelse af Chunks & Qdrant-indeksering
Når Vision-analysen er gennemført, oprettes en specialiseret chunk for siden, som gemmes i Qdrant med metadata:

* `DocumentId`: F.eks. `10290144`
* `PageNumber`: `4`
* `ChunkType`: `"MechanicalDrawing"`
* `Text`:
  ```markdown
  [ebm-papst 8300100757 / VBH0280CSPFS Mekanisk Måltegning & Flangemål - Side 4]
  - Monteringsflange: Kvadratisk stålplade □ 380 mm × 380 mm.
  - Boltafstand (c-c): □ 330 mm × 330 mm hulmønster.
  - Monteringshuller: 4 stk. Ø 11 mm hjørnehuller (passer til M10 bolte).
  - Kantafstand til bolthul: 25 mm fra hulcentrum til ydre flangekant.
  - Flangebuk/krave: 9,5 mm bagudrettet krave for stabilitet.
  - Indløbsdyse (halsdiameter): Ø 253 mm indsugningshul med 15 mm fremspring.
  - Dybdemål: Total indbygningsdybde 294 mm (±3 mm); dybde bag monteringsplade 229,9 mm (±2 mm).
  - Trykudtag & k-faktor: Integreret trykstuds i dyse med k-faktor = 98 (qv = 98 * sqrt(Δp)).
  - Kabler: Forsyningskabel 1000 mm (5-leder AWG18) og styrekabel 1000 mm (6-leder AWG22) med 8 mm afisolerede tylle-ender.
  - Tilladt montage: Aksel vandret (med bæreramme-stag lodret) eller rotor nedad.
  ```

---

### Fase 5: Kurvebladsanalyse (Side 7 – "Air Performance Curves")
Samme koncept anvendes på driftspunktkurverne på side 7:
1. Siden rasteriseres.
2. Vision-modellen udtrækker de numeriske kurveværdier fra måletabellen nederst (*Measured values 1–16*):
   * Omdrejningstal ($n$), optagen effekt ($P_{ed}$), strømforbrug ($I$), lydeffekt ($L_{wA}$), luftmængde ($q_v$ i $\text{m}^3/\text{t}$ og $\text{cfm}$) samt statisk tryk ($p_{fs}$ i $\text{Pa}$).
3. Dette gør det muligt for systemet at besvare spørgsmål som:
   * *"Hvad er lydniveauet for 8300100757 ved 2000 m³/t og 400 Pa?"*
   * Svar: *"Ved trin 2 (3640 rpm, 3235 m³/t ved 400 Pa) er lydeffekten LwAin 80 dB(A) og optagen effekt 714 W (Kilde: Side 7 Kurveblad)."*

---

## 4. ⚡ Ydelse, Omkostninger & Skalering (Pragmatic Design)

1. **Undgå over-processering**:
   * Sider analyseres **kun én gang** ved første upload eller under baggrunds-batch (`Worker`).
   * Resultatet gemmes permanent i SQLite og Qdrant. Live query-forespørgsler læser udelukkende fra cachen og databasen $\rightarrow$ **0 ms ekstra ventetid for brugeren i chatten**.
2. **Omkostningskontrol (Token-effektivitet)**:
   * Ved hjælp af heuristisk side-detektion sendes kun 2 ud af 7 sider (ca. 28% af PDF'en) til vision-API'et.
   * Med Gemini 2.5 Flash koster en billedside under **0,0005 DKK**, hvilket gør det ekstremt omkostningseffektivt selv for 10.000 datablade.
3. **Graceful Fallback**:
   * Hvis Vision API fejler eller er slået fra, falder systemet fejlfrit tilbage til standard `PdfTextExtractor`, præcis som det fungerer i dag.

---

## 5. 📅 Køreplan (Milestones)

| Trin | Opgave | Forventet leverance |
| :---: | :--- | :--- |
| **M1** | Implementering af `PdfPageRasterizer` (PDFium / SkiaSharp) i `Infrastructure` | PNG-generering i hukommelsen for specifikke sidetal |
| **M2** | Udvikling af `TechnicalDrawingVisionAnalyzer` med Gemini Vision prompt | Udtræk af flanger, dybder og k-faktorer til JSON |
| **M3** | Integration i `DocumentProcessor` og berigelse af side 4-chunks | Qdrant og SQLite opdateres med mekaniske chunks |
| **M4** | E2E-test: Verificering af svar på *"Hvad er flangemålene for 8300100757?"* | Mutti svarer automatisk med $\square 380\text{ mm}$, $\square 330\text{ mm}$ og $\varnothing 11\text{ mm}$ |
| **M5** | Udrulning på produktionsserver | Live funktion tilgængelig i UI |

---

*Plan oprettet af Ranger 1. Klar til eksekvering på ordre, sir!*
