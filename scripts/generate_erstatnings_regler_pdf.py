import os
import sys
from fpdf import FPDF
from fpdf.enums import XPos, YPos

class ErstatningsReglerPDF(FPDF):
    def __init__(self):
        super().__init__(orientation='P', unit='mm', format='A4')
        self.set_auto_page_break(auto=True, margin=15)
        
        # Load Arial fonts
        font_dir = r'C:\Windows\Fonts'
        self.add_font('Arial', '', os.path.join(font_dir, 'arial.ttf'))
        self.add_font('Arial', 'B', os.path.join(font_dir, 'arialbd.ttf'))
        self.add_font('Arial', 'I', os.path.join(font_dir, 'ariali.ttf'))
        self.add_font('Arial', 'BI', os.path.join(font_dir, 'arialbi.ttf'))
        
        # Color Palette
        self.c_primary = (27, 54, 93)      # Deep Navy #1B365D
        self.c_secondary = (43, 108, 176)  # Slate Blue #2B6CB0
        self.c_dark = (45, 55, 72)         # Charcoal Text #2D3748
        self.c_muted = (100, 116, 139)     # Slate Muted #64748B
        self.c_light_bg = (248, 250, 252)  # Light Card Bg #F8FAFC
        self.c_alt_row = (241, 245, 249)   # Alt Row #F1F5F9
        self.c_border = (226, 232, 240)    # Border #E2E8F0
        self.c_alert_bg = (254, 242, 242)  # Red Light #FEF2F2
        self.c_alert_border = (239, 68, 68)# Red Border #EF4444
        self.c_alert_text = (153, 27, 27)  # Red Dark #991B1B
        self.c_ec_bg = (240, 253, 244)     # Green Light #F0FDF4
        self.c_ec_border = (34, 197, 94)   # Green Border #22C55E
        self.c_ec_text = (22, 101, 52)     # Green Dark #166534

    def header(self):
        if self.page_no() == 1:
            return
        self.set_font('Arial', 'I', 8)
        self.set_text_color(*self.c_muted)
        self.cell(130, 6, 'ebm-papst Ventilator Erstatnings- og Kompatibilitetsregler', border=0, align='L', new_x=XPos.RIGHT, new_y=YPos.TOP)
        self.set_font('Arial', '', 8)
        self.cell(0, 6, f'Side {self.page_no()}', border=0, align='R', new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.set_draw_color(*self.c_border)
        self.set_line_width(0.3)
        self.line(self.l_margin, 13, 210 - self.r_margin, 13)
        self.ln(3)

    def footer(self):
        self.set_y(-12)
        self.set_font('Arial', '', 7.5)
        self.set_text_color(*self.c_muted)
        self.set_draw_color(*self.c_border)
        self.set_line_width(0.3)
        self.line(self.l_margin, 297 - 13, 210 - self.r_margin, 297 - 13)
        self.cell(130, 6, 'ebm-papst Document RAG System — Teknisk Dokumentation', border=0, align='L', new_x=XPos.RIGHT, new_y=YPos.TOP)
        self.cell(0, 6, f'Side {self.page_no()}', border=0, align='R', new_x=XPos.LMARGIN, new_y=YPos.NEXT)

    def section_header(self, number, title):
        self.set_font('Arial', 'B', 12)
        self.set_text_color(*self.c_primary)
        self.cell(0, 7, f"{number}. {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.set_draw_color(*self.c_secondary)
        self.set_line_width(0.5)
        self.line(self.l_margin, self.get_y(), self.l_margin + 55, self.get_y())
        self.ln(2.5)

    def subsection_header(self, title):
        self.set_font('Arial', 'B', 9.5)
        self.set_text_color(*self.c_secondary)
        self.cell(0, 5.5, title, border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)

    def body_p(self, text):
        self.set_font('Arial', '', 8.8)
        self.set_text_color(*self.c_dark)
        self.multi_cell(0, 4.4, text)
        self.ln(1.5)

    def bullet(self, bold_prefix, text, indent=4):
        self.set_x(self.l_margin + indent)
        self.set_font('Arial', 'B', 8.5)
        self.set_text_color(*self.c_primary)
        self.write(4.3, "•  " + bold_prefix + ": ")
        self.set_font('Arial', '', 8.5)
        self.set_text_color(*self.c_dark)
        self.write(4.3, text + "\n")
        self.ln(1)

    def sub_bullet(self, code_badge, title, text, indent=8):
        self.set_x(self.l_margin + indent)
        self.set_font('Arial', 'B', 8.3)
        self.set_text_color(*self.c_secondary)
        self.write(4.0, f"[{code_badge}] {title}: ")
        self.set_font('Arial', '', 8.3)
        self.set_text_color(*self.c_dark)
        self.write(4.0, text + "\n")
        self.ln(0.8)

    def alert_box(self, title, text):
        self.set_fill_color(*self.c_alert_bg)
        self.set_draw_color(*self.c_alert_border)
        self.set_line_width(0.4)
        
        start_y = self.get_y()
        w = 210 - self.l_margin - self.r_margin
        
        self.set_xy(self.l_margin + 3, start_y + 2.5)
        self.set_font('Arial', 'B', 9)
        self.set_text_color(*self.c_alert_text)
        self.cell(0, 4.5, f"[OBS]  {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)
        
        self.set_x(self.l_margin + 3)
        self.set_font('Arial', '', 8.3)
        self.multi_cell(w - 6, 4.0, text)
        
        end_y = self.get_y() + 2.5
        box_h = end_y - start_y
        self.rect(self.l_margin, start_y, w, box_h, 'DF')
        
        # Redraw text over background
        self.set_xy(self.l_margin + 3, start_y + 2.5)
        self.set_font('Arial', 'B', 9)
        self.set_text_color(*self.c_alert_text)
        self.cell(0, 4.5, f"[OBS]  {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)
        
        self.set_x(self.l_margin + 3)
        self.set_font('Arial', '', 8.3)
        self.multi_cell(w - 6, 4.0, text)
        self.set_y(end_y + 2)

    def ec_callout_box(self, title, items):
        self.set_fill_color(*self.c_ec_bg)
        self.set_draw_color(*self.c_ec_border)
        self.set_line_width(0.4)
        
        w = 210 - self.l_margin - self.r_margin
        start_y = self.get_y()
        
        # Calculate height roughly
        self.set_xy(self.l_margin + 3, start_y + 2.5)
        self.set_font('Arial', 'B', 9)
        self.set_text_color(*self.c_ec_text)
        self.cell(0, 4.5, f"[EC-OPGRADERING]  {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)
        
        for b_text, n_text in items:
            self.set_x(self.l_margin + 4)
            self.set_font('Arial', 'B', 8.3)
            self.set_text_color(*self.c_ec_text)
            self.write(4.0, "• " + b_text + ": ")
            self.set_font('Arial', '', 8.3)
            self.set_text_color(*self.c_dark)
            self.write(4.0, n_text + "\n")
            self.ln(0.8)
            
        end_y = self.get_y() + 2.5
        box_h = end_y - start_y
        self.rect(self.l_margin, start_y, w, box_h, 'DF')
        
        # Redraw
        self.set_xy(self.l_margin + 3, start_y + 2.5)
        self.set_font('Arial', 'B', 9)
        self.set_text_color(*self.c_ec_text)
        self.cell(0, 4.5, f"[EC-OPGRADERING]  {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)
        
        for b_text, n_text in items:
            self.set_x(self.l_margin + 4)
            self.set_font('Arial', 'B', 8.3)
            self.set_text_color(*self.c_ec_text)
            self.write(4.0, "• " + b_text + ": ")
            self.set_font('Arial', '', 8.3)
            self.set_text_color(*self.c_dark)
            self.write(4.0, n_text + "\n")
            self.ln(0.8)
            
        self.set_y(end_y + 2)

    def render_table(self, headers, rows, col_widths, col_aligns=None):
        if col_aligns is None:
            col_aligns = ['L'] * len(headers)
        
        self.set_draw_color(*self.c_border)
        self.set_line_width(0.2)
        
        # Header
        self.set_fill_color(*self.c_primary)
        self.set_text_color(255, 255, 255)
        self.set_font('Arial', 'B', 8)
        
        for i, header in enumerate(headers):
            self.cell(col_widths[i], 5.5, header, border=1, align=col_aligns[i], fill=True,
                      new_x=XPos.RIGHT if i < len(headers)-1 else XPos.LMARGIN,
                      new_y=YPos.TOP if i < len(headers)-1 else YPos.NEXT)
                      
        # Rows
        self.set_font('Arial', '', 7.8)
        for r_idx, row in enumerate(rows):
            fill = (r_idx % 2 == 1)
            if fill:
                self.set_fill_color(*self.c_alt_row)
            else:
                self.set_fill_color(255, 255, 255)
                
            self.set_text_color(*self.c_dark)
            for c_idx, cell_text in enumerate(row):
                self.cell(col_widths[c_idx], 5, cell_text, border=1, align=col_aligns[c_idx], fill=True,
                          new_x=XPos.RIGHT if c_idx < len(row)-1 else XPos.LMARGIN,
                          new_y=YPos.TOP if c_idx < len(row)-1 else YPos.NEXT)
        self.ln(2)

def generate_pdf():
    pdf = ErstatningsReglerPDF()
    pdf.add_page()
    
    # Title Banner
    pdf.set_fill_color(*pdf.c_primary)
    pdf.rect(pdf.l_margin, 14, 210 - pdf.l_margin - pdf.r_margin, 22, 'F')
    
    pdf.set_xy(pdf.l_margin + 5, 17)
    pdf.set_font('Arial', 'B', 15)
    pdf.set_text_color(255, 255, 255)
    pdf.cell(0, 7, "Erstatnings- og Kompatibilitetsregler for Ventilatorer", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
    
    pdf.set_xy(pdf.l_margin + 5, 25)
    pdf.set_font('Arial', '', 8.8)
    pdf.set_text_color(203, 213, 225)
    pdf.cell(0, 5, "Teknisk analyse, typenøgleafkodning og GreenTech EC opgraderingsregler (ebm-papst)", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
    
    pdf.set_y(40)
    pdf.body_p(
        "Når en bruger forespørger på en erstatningsventilator til en specifik model, analyserer systemet forespørgslen "
        "via det specialiserede kompatibilitetsworkflow (CompatibilityWorkflowExecutor). Der foretages en systematisk kontrol "
        "af mekaniske mål, aerodynamisk luftretning, elektrisk forsyning, motorteknologi og driftsdata."
    )
    
    # 1. Mekaniske og aerodynamiske dimensioner
    pdf.section_header("1", "Mekaniske & Aerodynamiske Dimensioner")
    
    pdf.bullet("Impellerdiameter (Ø mm)", "Udledes direkte fra ciffer 4-6 i standard typenøglen (f.eks. A3G910... = Ø910 mm, S4E350... = Ø350 mm). Skal passe i montagehul/vægudskæring.")
    pdf.bullet("Ventilatorfamilie & Montageform", "Afgør om ventilatoren er aksial eller centrifugal samt monteringsudførelse:")
    
    pdf.sub_bullet("A", "Aksial grundmodel", "Vingehjul og motor alene uden gitter eller vægring.")
    pdf.sub_bullet("S", "Aksial med beskyttelsesgitter", "Monteret på trådgitter/guard grille til sikker indbygning.")
    pdf.sub_bullet("W", "Aksial i vægring", "Monteret i aerodynamisk vægring/wall ring til plan vægmontering.")
    pdf.sub_bullet("R", "Centrifugal uden ramme", "Enkeltindsugende motoriseret hjul til indbygning i eksisterende kammer.")
    pdf.sub_bullet("K", "Centrifugal RadiPac", "Motoriseret hjul monteret i komplet afstivet bærebuk/ramme med indløbsdyse.")
    pdf.sub_bullet("G / D", "Centrifugal i hus", "Centrifugalblæser i enkelt- (G) eller dobbeltsugende (D) sneglehus.")
    pdf.sub_bullet("8300", "Ny generation RadiPac", "ebm-papsts nyeste EC RadiPac moduler, som erstatter K3G-serien.")
    pdf.ln(1)
    
    # Critical Airflow Callout
    pdf.alert_box(
        "KRITISK REGEL: Luftretning på Aksialventilatorer (12. Ciffer)",
        "På standard 12-cifrede aksialmodeller angiver det 12. ciffer altid luftretningen:\n"
        "• LIGE slutciffer (0, 2, 4, 6, 8) = Luftretning 'A' (indsugning over ribber/bærearme).\n"
        "• ULIGE slutciffer (1, 3, 5, 7, 9) = Luftretning 'V' (udblæsning over ribber/bærearme).\n"
        "Luftretning A og V blæser 180° modsat hinanden. Modeller med modsat luftretning er 100% INKOMPATIBLE "
        "som direkte drop-in erstatninger (f.eks. er A6E450-AP02-01 [V] og A6E450-AP02-02 [A] inkompatible)."
    )
    
    pdf.ln(1)
    
    # 2. Elektriske data og motorteknologi
    pdf.section_header("2", "Elektriske Data & Motorteknologi")
    
    pdf.bullet("Motorteknologi", "Bestemmer motortype og forsyningsinfrastruktur:")
    pdf.sub_bullet("3G / EC", "GreenTech EC", "Elektronisk kommuteret motor med integreret elektronik og trinløs styring.")
    pdf.sub_bullet("AC ..E", "1-faset AC", "Asynkronmotor til 1~ 230 VAC forsyning (f.eks. 4E350 = 4-polet 1~ AC).")
    pdf.sub_bullet("AC ..D", "3-faset AC", "Asynkronmotor til 3~ 400 VAC forsyning (f.eks. 4D350 = 4-polet 3~ AC).")
    pdf.sub_bullet("DC", "Kompaktmotor", "Elektronisk DC motor (12, 24 eller 48 VDC) med integreret print.")
    
    pdf.bullet("Forsyningsspænding & Netfrekvens", "Nominel driftsspænding (f.eks. 230 VAC, 400 VAC eller 380-480 VAC vidtspændingsindgang) samt 50/60 Hz frekvenskompatibilitet.")
    pdf.bullet("Styring & Grænseflader", "0-10 VDC spændingssignal, 4-20 mA strømsløjfe, PWM, Modbus RTU RS485 seriel kommunikation, alarmrelæer og tacho-output.")
    
    pdf.ln(1)
    
    # 3. Aerodynamisk ydelse og driftsbetingelser
    pdf.section_header("3", "Aerodynamisk Ydelse & Driftsbetingelser")
    
    pdf.bullet("Driftspunkt (qv & Δpfs)", "Luftmængde (m³/t) mod statisk tryk (Pa). Erstatningsmodellen skal matche eller overstige det dimensionerende driftspunkt på tryk/flow-karakteristikken.")
    pdf.bullet("Effektoptag & Strømforbrug", "Maksimal optagen effekt (W/kW) og nominel strøm (A) for at garantere mod kabel- og sikringsoverbelastning.")
    pdf.bullet("Kapslingsklasse & Temperatur", "IP-tæthedsgrad (f.eks. IP44, IP54 eller IP55) og omgivelsestemperaturområde (-25 °C til +60 °C).")
    
    pdf.ln(1)
    
    # 4. Erstatningsprioritering: Forsyningsform, Effekt (Watt) & Dimensioner
    pdf.section_header("4", "Erstatningsprioritering: Forsyningsform, Effekt (W) & Dimensioner")
    
    pdf.body_p(
        "Når assistenten (Ava) evaluerer og rangerer mulige erstatninger, gælder følgende ufravigelige regler:"
    )
    
    pdf.bullet("1. Samme Forsyningsform (Spænding & Faser)", "Erstatningen SKAL som udgangspunkt benytte samme forsyning: 1~ 230 VAC, 3~ 400 VAC eller 12/24/48 VDC. En 400 VAC 3-faset model kan ikke anvendes i en 230 VAC installation uden tavleombygning.")
    pdf.bullet("2. Optagen Effekt (W) så tæt på som muligt", "Optagen effekt (Watt) skal ligge så tæt på kildens oprindelige effekt som muligt for at undgå unødig overdimensionering og overbelastning af forsyningskredsløb.")
    pdf.bullet("3. Mekaniske Dimensioner", "Impellerdiameter (Ø mm), dybde og montageramme skal være så tæt på originalen som muligt for sikker mekanisk pasform.")
    pdf.bullet("4. Alternative forslag & Oplysningspligt", "Ava må gerne foreslå en anden erstatning, hvis den optimale mangler, MEN skal altid eksplicit oplyse kunden om samtlige forskelle i spænding, faser, wattforbrug og mål.")
    
    # Example Case Table
    pdf.subsection_header("Praksiseksempel: Erstatning for K3G280PR04I2")
    case_headers = ["Model", "Forsyning", "Effekt", "Vurdering & Begrundelse"]
    case_rows = [
        ["K3G280PR04I2 (Kilde)", "230 VAC (1~)", "750 W", "Referencekilde: RadiPac EC Ø280 mm."],
        ["8300100757", "230 VAC (1~)", "800 W", "OPTIMAL ERSTATNING: Samme 230V forsyning, kun +50 W (+6.7%) afvigelse."],
        ["8300102292", "3~ 400 VAC", "1300 W", "IKKE DIREKTE ERSTATNING: Forkert forsyning (3~ 400V) og overdimensioneret (+550 W)."]
    ]
    pdf.render_table(case_headers, case_rows, [45, 30, 22, 93], ['L', 'C', 'C', 'L'])
    
    pdf.ln(1)
    
    # 5. Systemets svarstruktur og GreenTech opgradering
    pdf.section_header("5", "Systemets Svarstruktur & GreenTech EC Opgradering")
    
    pdf.body_p(
        "Når forespørgslen behandles, opbygges svaret til kunden systematisk i 3 niveauer:"
    )
    
    # Table of response structure
    headers = ["Trin", "Handling", "Kriterie / Resultat"]
    rows = [
        ["1. Database-søgning", "Matcher mod lokale datablade", "Finder direkte drop-in modeller (0 kritiske forskelle, samme spænding & nær watt)"],
        ["2. Typenøgle-mønstre", "Udleder teoretiske erstatninger", "Anbefaler S- eller W-serie, RadiPac 8300, med korrekt A/V ciffer"],
        ["3. EC Opgradering", "EcReplacementAdvisor aktiveres", "Udregner automatisk GreenTech EC alternativ ved ældre AC-modeller"]
    ]
    pdf.render_table(headers, rows, [35, 60, 95], ['L', 'L', 'L'])
    
    # EC GreenTech Box
    pdf.ec_callout_box(
        "Automatisk GreenTech EC Erstatningsanbefaling for AC-Modeller",
        [
            ("Energibesparelse", "Typisk 30-50% lavere energiforbrug ved nominel last og op til 70% ved dellast via trinløs drift."),
            ("Mekanisk 1:1 kompatibilitet", "Identiske monteringsmål, aksialdiametre og boltmønstre (f.eks. S4E350 -> S3G350)."),
            ("Integreret styring", "Trinløs 0-10V og Modbus RTU regulering erstatter støjende trin-trafoer og eksterne frekvensomformere."),
            ("Kompaktblæser erstatning", "Ældre 4650N/4656N AC-blæsere (19W) erstattes med ACi4400 GreenTech EC (4.5W, op til 75% besparelse).")
        ]
    )

    output_dir = r"C:\Users\bbracke\Desktop\EBM\DotNet\docs"
    output_pdf = os.path.join(output_dir, "erstatnings_regler.pdf")
    pdf.output(output_pdf)
    print(f"PDF generated successfully at: {output_pdf}")

if __name__ == "__main__":
    generate_pdf()
