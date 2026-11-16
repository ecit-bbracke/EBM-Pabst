# Erstatnings- og kompatibilitetsregler for ventilatorer (ebm-papst)

Når en bruger beder om en erstatningsventilator til en specifik model inden for ebm-papst domænet, behandles forespørgslen via **kompatibilitets- og erstatningsworkflowet** (*Compatibility Workflow*).

Systemet undersøger og sammenligner følgende parametre på tværs af produktkoder (*typenøglen*), datablade og driftsspecifikationer:

---

## 1. Mekaniske og aerodynamiske dimensioner

* **Impellerdiameter ($\varnothing$ mm)**:
  * Udledes af ciffer 4–6 i standard ebm-papst koder (f.eks. `A3G910...` $\rightarrow \varnothing 910\text{ mm}$, `S4E350...` $\rightarrow \varnothing 350\text{ mm}$, eller kompakte rammemål som $119\times 119\text{ mm}$ og $\varnothing 172\text{ mm}$).
  * Skal matche den eksisterende vægudskæring, montageramme eller kanaldiameter for overhovedet at kunne monteres fysisk.
* **Ventilatorfamilie og montageform**:
  * **Aksialventilatorer**:
    * `A`: Aksial grundmodel (kun motor og vingehjul uden tilbehør).
    * `S`: Aksial med beskyttelsesgitter (*guard grille*).
    * `W`: Aksial i vægring (*wall ring*) til plade- eller vægmontage.
  * **Centrifugalventilatorer**:
    * `R`: Enkeltindsugende centrifugalhjul uden hus/ramme (frit hjul).
    * `K`: RadiPac centrifugalmodul monteret i bære- eller montageramme.
    * `G`: Centrifugal i enkeltsugende sneglehus (*scroll*).
    * `D`: Centrifugal i dobbeltsugende sneglehus.
    * `8300...` serien: Nyeste generation af RadiPac EC-moduler, som erstatter den ældre `K3G...`-serie.
  * **Kompaktblæsere (DC/AC)**: Rammemål, byggedybde og hulafstand (f.eks. 4114, 6314, 3258, ACi4400).
* **Luftretning (Kritisk regel for aksialventilatorer)**:
  * Afkodes ud fra det **12. ciffer** i 12-cifrede produktkoder:
    * **LIGE ciffer** (0, 2, 4, 6, 8) = **Luftretning "A"** (indsugning over ribber/bærearme).
    * **ULIGE ciffer** (1, 3, 5, 7, 9) = **Luftretning "V"** (udblæsning over ribber/bærearme).
  * **Kritisk blokerer**: Luftretning "A" og "V" blæser 180° modsat hinanden. Modeller med modsat luftretning er **100% inkompatible som direkte erstatning**, selvom alle andre cifre og mål er ens (f.eks. kan `A6E450-AP02-01` [V] **ikke** erstatte `A6E450-AP02-02` [A]).

---

## 2. Elektriske data og motorteknologi

* **Motorteknologi**:
  * **EC (elektronisk kommuteret)**: Kodes med `3G`, `1G` eller `2G`. Har integreret frekvensomformer og styreelektronik i motoren.
  * **AC (asynkronmotor)**: Kodes med poltal og fasebogstav:
    * `E` = 1-faset AC ($1\sim 230\text{ V}$, Einphasen).
    * `D` = 3-faset AC ($3\sim 400\text{ V}$, Dreiphasen).
  * **DC**: For kompaktblæsere (f.eks. 12, 24 eller 48 VDC).
* **Forsyningsspænding og netfrekvens**:
  * Nominel driftsspænding (f.eks. $230\text{ VAC}$, $400\text{ VAC}$ eller $380\text{--}480\text{ VAC}$ multispænding) samt frekvens (50/60 Hz).
* **Styresignal og regulering**:
  * **AC-modeller**: Typisk reguleret via trin-transformere eller ekstern frekvensomformer.
  * **EC-modeller**: Trinløs regulering via integreret $0\text{--}10\text{ VDC}$, PWM, $4\text{--}20\text{ mA}$ eller seriel Modbus RTU (RS485).
  * Tilstedeværelse af tacho-udgang (omdrejningssignal) eller alarmrelæ/fejlkontakt.

---

## 3. Aerodynamisk ydelse og driftsbetingelser

* **Driftspunkt**: Luftmængde ($q_v$ i $\text{m}^3/\text{t}$) mod trykforøgelse ($\Delta p_{fs}$ i $\text{Pa}$) på ventilatorkarakteristikken for at sikre, at erstatningen dækker luftbehovet.
* **Effektoptag og strømforbrug**: Maksimal optagen effekt ($P_1$ i $\text{W}/\text{kW}$) og nominel strøm ($I$ i $\text{A}$) for at sikre, at eksisterende forsyningskabler og sikringer ikke overbelastes.
* **Omgivelsesbetingelser**:
  * Tilladt temperaturområde ($T_{\text{min}}$ til $T_{\text{max}}$, f.eks. $-25^\circ\text{C}$ til $+60^\circ\text{C}$).
  * Kapslingsklasse (IP-grad, f.eks. IP44, IP54 eller IP55).

---

## 4. Erstatningsprioritering: Forsyningsform, Effekt (Watt) & Dimensioner

Når assistenten (Ava) evaluerer og rangerer mulige erstatninger, gælder følgende ufravigelige principper:

1. **Samme forsyningsform (Spænding & Faser)**:
   * Erstatninger **skal** som udgangspunkt benytte samme forsyningsform:
     * **1~ 230 VAC** (enkeltfaset lysnet)
     * **3~ 400 VAC** (trefaset kraftstik)
     * **12, 24 eller 48 VDC** (jævnstrøm)
   * En 3-faset 400 VAC model kan **ikke** benyttes direkte i en installation med 230 VAC 1-faset forsyning uden omfattende ombygning af eltavlen.
2. **Optagen effekt (W) skal være så tæt på som muligt**:
   * Erstatningsmodellens nominelle optagne effekt ($P_1$ i Watt) skal ligge så tæt på den oprindelige models effekt som muligt for at undgå unødig overdimensionering, højere startstrømme og unødigt energiforbrug.
3. **Mekaniske dimensioner**:
   * Impellerdiameter ($\varnothing\text{ mm}$), dybde og montageramme skal være så tæt på originalen som muligt for at sikre mekanisk pasform.
4. **Alternative erstatninger & Oplysningspligt**:
   * Ava må gerne foreslå en alternativ model, hvis den optimale erstatning ikke er tilgængelig, **men skal altid eksplicit oplyse om samtlige forskelle** (f.eks. ændring i forsyningsspænding, faseantal, højere/lavere wattforbrug eller ændrede byggemål).

### 💡 Konkret eksempel: Erstatning for K3G280PR04I2
* **Kildemodel**: `K3G280PR04I2`
  * Forsyning: **230 VAC** (1~)
  * Effekt: **750 W**
  * Type: RadiPac EC centrifugalmodul ($\varnothing 280\text{ mm}$)
* **Kandidat 1 (`8300100757`)** $\rightarrow$ **BEDSTE ERSTATNING**:
  * Forsyning: **230 VAC** (matcher kilden 100%)
  * Effekt: **800 W** (afviger kun med +50 W / ~6.7% fra 750 W)
  * Konklusion: Perfekt match i forsyning og effekt.
* **Kandidat 2 (`8300102292`)** $\rightarrow$ **IKKE DIREKTE ERSTATNING**:
  * Forsyning: **3~ 400 VAC** (kræver 3 faser mod kildens 230V 1-faset forsyning)
  * Effekt: **1300 W** (markant overdimensioneret, +550 W / +73% over kildens 750 W)
  * Konklusion: Kan kun benyttes som alternativ, hvis kunden har 3~ 400V til rådighed, og Ava **skal** udtrykkeligt oplyse om både spændings- og effektafvigelsen.

---

## 5. Sådan opbygger systemet svaret til brugeren

Når en erstatning efterspørges, anvender systemet denne struktur:
1. **Opslag i lokal database**: Først undersøges det, om der findes en eksisterende, direkte "drop-in" model i systemets datablade (med identisk diameter, luftretning, forsyning og nær effekt).
2. **Prioritering og oplysning om forskelle**: Rangér kandidater efter forsyningsmatch og mindste effektafvigelse (Watt). Hvis en alternativ model med afvigelser foreslås, oplyses samtlige forskelle klart.
3. **Teoretiske typenøgle-mønstre**: Findes der ikke en direkte model i databasen, genereres gyldige ebm-papst modelpræfikser baseret på reglerne (f.eks. fra `A...` grundmodel til `S...` med gitter eller `W...` i vægring med korrekt LIGE/ULIGE slutciffer).
4. **GreenTech EC-opgradering (`EcReplacementAdvisor`)**: Ved forespørgsler på ældre **AC-modeller** tilføjes der automatisk et dedikeret afsnit om moderne EC-alternativer (f.eks. fra AC `S4E...` til `S3G...`), som fremhæver:
   * **30–50%+ energibesparelse** (og endnu mere ved dellast via trinløs drift).
   * 100% identiske monterings- og kapslingsmål.
   * Trinløs $0\text{--}10\text{ V}$/Modbus-styring uden behov for eksterne omformere.
