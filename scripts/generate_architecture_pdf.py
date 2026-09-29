import os
import sys
from fpdf import FPDF
from fpdf.enums import XPos, YPos

class TechRagPDF(FPDF):
    def __init__(self):
        super().__init__(orientation='P', unit='mm', format='A4')
        self.set_auto_page_break(auto=True, margin=15)
        
        # Load Arial fonts
        self.add_font('Arial', '', r'C:\Windows\Fonts\arial.ttf')
        self.add_font('Arial', 'B', r'C:\Windows\Fonts\arialbd.ttf')
        self.add_font('Arial', 'I', r'C:\Windows\Fonts\ariali.ttf')
        self.add_font('Arial', 'BI', r'C:\Windows\Fonts\arialbi.ttf')
        
        # Color Palette
        self.c_primary = (27, 54, 93)     # Deep Navy #1B365D
        self.c_secondary = (43, 108, 176) # Slate Blue #2B6CB0
        self.c_dark = (45, 55, 72)        # Charcoal Text #2D3748
        self.c_muted = (100, 116, 139)    # Slate Muted #64748B
        self.c_light_bg = (248, 250, 252) # Light Card Bg #F8FAFC
        self.c_alt_row = (241, 245, 249)  # Alt Row #F1F5F9
        self.c_border = (226, 232, 240)   # Border #E2E8F0
        self.c_accent = (217, 119, 6)     # Amber Accent #D97706
        self.c_success = (22, 101, 52)    # Green #166534

    def header(self):
        if self.page_no() == 1:
            return
        self.set_font('Arial', 'I', 8)
        self.set_text_color(*self.c_muted)
        self.cell(130, 6, 'Document RAG System — Technical Architecture & Gemini API Analysis', border=0, align='L', new_x=XPos.RIGHT, new_y=YPos.TOP)
        self.set_font('Arial', '', 8)
        self.cell(0, 6, f'Page {self.page_no()}', border=0, align='R', new_x=XPos.LMARGIN, new_y=YPos.NEXT)
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
        self.cell(130, 6, 'ebm-papst Document RAG System Architecture Documentation', border=0, align='L', new_x=XPos.RIGHT, new_y=YPos.TOP)
        self.cell(0, 6, f'Page {self.page_no()}', border=0, align='R', new_x=XPos.LMARGIN, new_y=YPos.NEXT)

    def chapter_title(self, number, title):
        self.set_font('Arial', 'B', 13)
        self.set_text_color(*self.c_primary)
        self.cell(0, 7, f"{number}. {title}", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.set_draw_color(*self.c_secondary)
        self.set_line_width(0.5)
        self.line(self.l_margin, self.get_y(), self.l_margin + 65, self.get_y())
        self.ln(3)

    def section_title(self, title):
        self.set_font('Arial', 'B', 10)
        self.set_text_color(*self.c_secondary)
        self.cell(0, 6, title, border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)

    def body_p(self, text, bold_prefix=""):
        self.set_font('Arial', '', 9)
        self.set_text_color(*self.c_dark)
        if bold_prefix:
            self.set_font('Arial', 'B', 9)
            self.write(4.8, bold_prefix + " ")
            self.set_font('Arial', '', 9)
        self.write(4.8, text + "\n")
        self.ln(1.5)

    def bullet_item(self, bold_text, normal_text, indent=4):
        self.set_left_margin(self.l_margin + indent)
        self.set_font('Arial', 'B', 8.8)
        self.set_text_color(*self.c_primary)
        self.write(4.6, "- " + bold_text + ": ")
        self.set_font('Arial', '', 8.8)
        self.set_text_color(*self.c_dark)
        self.write(4.6, normal_text + "\n")
        self.set_left_margin(self.l_margin - indent)
        self.ln(1.2)

    def callout_box(self, title, items, bg_color=None, border_color=None):
        if bg_color is None: bg_color = self.c_light_bg
        if border_color is None: border_color = self.c_secondary
        
        self.set_fill_color(*bg_color)
        self.set_draw_color(*border_color)
        self.set_line_width(0.3)
        
        box_start = self.get_y()
        self.set_x(self.l_margin + 3)
        self.set_font('Arial', 'B', 9.5)
        self.set_text_color(*self.c_primary)
        self.cell(0, 5.5, title, border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
        self.ln(1)
        
        for b_text, n_text in items:
            self.set_x(self.l_margin + 5)
            self.set_font('Arial', 'B', 8.5)
            self.set_text_color(*self.c_dark)
            self.write(4.4, "* " + b_text + ": ")
            self.set_font('Arial', '', 8.5)
            self.write(4.4, n_text + "\n")
            self.ln(1)
            
        box_end = self.get_y() + 1.5
        box_height = box_end - box_start + 3
        self.rect(self.l_margin, box_start - 1.5, 210 - self.l_margin - self.r_margin, box_height, 'D')
        self.set_y(box_end + 2)

    def render_table(self, headers, rows, col_widths, col_aligns=None):
        if col_aligns is None:
            col_aligns = ['L'] * len(headers)
            
        # Header Row
        self.set_fill_color(*self.c_primary)
        self.set_text_color(255, 255, 255)
        self.set_font('Arial', 'B', 8)
        self.set_draw_color(*self.c_border)
        self.set_line_width(0.2)
        
        for i, header in enumerate(headers):
            self.cell(col_widths[i], 6.5, header, border=1, align=col_aligns[i], fill=True, new_x=XPos.RIGHT, new_y=YPos.TOP)
        self.ln(6.5)
        
        # Rows
        for r_idx, row in enumerate(rows):
            fill = (r_idx % 2 == 1)
            if fill:
                self.set_fill_color(*self.c_alt_row)
            else:
                self.set_fill_color(255, 255, 255)
                
            self.set_text_color(*self.c_dark)
            for c_idx, val in enumerate(row):
                if c_idx == 0 or (r_idx == len(rows) - 1 and "Total" in str(row[0])):
                    self.set_font('Arial', 'B', 7.8)
                else:
                    self.set_font('Arial', '', 7.8)
                self.cell(col_widths[c_idx], 5.6, str(val), border=1, align=col_aligns[c_idx], fill=True, new_x=XPos.RIGHT, new_y=YPos.TOP)
            self.ln(5.6)
        self.ln(2.5)

def build_pdf():
    # Ensure diagrams exist
    from generate_diagrams import generate_pipeline_diagram, generate_case_e_diagram
    os.makedirs("docs/diagrams", exist_ok=True)
    diag1_path = "docs/diagrams/pipeline_flowchart.png"
    diag2_path = "docs/diagrams/case_e_flowchart.png"
    
    if not os.path.exists(diag1_path):
        generate_pipeline_diagram(diag1_path)
    if not os.path.exists(diag2_path):
        generate_case_e_diagram(diag2_path)
        
    pdf = TechRagPDF()
    pdf.set_margin(16)
    
    # =========================================================================
    # PAGE 1: COVER & EXECUTIVE SUMMARY
    # =========================================================================
    pdf.add_page()
    
    # Header Banner
    pdf.set_fill_color(27, 54, 93)
    pdf.rect(0, 0, 210, 36, 'F')
    
    pdf.set_y(7)
    pdf.set_font('Arial', 'B', 16)
    pdf.set_text_color(255, 255, 255)
    pdf.cell(0, 7, "Document RAG System — Technical Architecture", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT, align='L')
    
    pdf.set_font('Arial', '', 9.5)
    pdf.set_text_color(203, 213, 225)
    pdf.cell(0, 5.5, "End-to-End Chat Request Lifecycle, Multi-Stage Pipeline & Gemini API Call Analysis", border=0, new_x=XPos.LMARGIN, new_y=YPos.NEXT, align='L')
    
    pdf.set_y(39)
    
    # Stack Info Box
    pdf.set_fill_color(248, 250, 252)
    pdf.set_draw_color(226, 232, 240)
    pdf.rect(pdf.l_margin, pdf.get_y(), 210 - pdf.l_margin - pdf.r_margin, 12, 'DF')
    pdf.set_xy(pdf.l_margin + 3, pdf.get_y() + 2)
    pdf.set_font('Arial', 'B', 8)
    pdf.set_text_color(71, 85, 105)
    pdf.cell(42, 4, "Backend: ASP.NET Core 9.0", 0)
    pdf.cell(45, 4, "Vector Store: Qdrant", 0)
    pdf.cell(50, 4, "LLM: Google Gemini 3.6 Flash", 0)
    pdf.cell(38, 4, "Embeddings: gemini-001", 0, new_x=XPos.LMARGIN, new_y=YPos.NEXT)
    
    pdf.set_y(54)
    
    pdf.chapter_title("1", "Executive Summary & Core Architecture")
    pdf.body_p(
        "The Document RAG System is a production multi-stage technical retrieval-augmented generation engine engineered "
        "specifically for industrial product documentation (ebm-papst ventilation systems, motors, and drive technology). "
        "The architecture combines deterministic domain heuristics with semantic vector search, specialized workflow routing, "
        "claim-level evidence evaluation, and strict output guardrails to guarantee factual grounding and eliminate hallucinations."
    )
    
    pdf.section_title("Core Architectural Building Blocks")
    pdf.bullet_item("ASP.NET Core Web API", "Provides REST (POST /api/query) and Server-Sent Events (POST /api/query/stream) endpoints with FluentValidation.")
    pdf.bullet_item("TechnicalRagOrchestrator", "Coordinates conversational turn state, parallel execution, routing, evidence verification, and output safety.")
    pdf.bullet_item("ConversationalQueryRefiner", "Resolves multi-turn pronouns/anaphoras, tracks active constraints, and outputs an unambiguous EffectiveQuestion.")
    pdf.bullet_item("InputGovernor & WorkflowRouter", "Classifies query intent across 8 technical domains and routes to dedicated domain workflow executors.")
    pdf.bullet_item("Qdrant Vector Store & Gemini Embeddings", "Generates dense 768-dim embeddings via gemini-embedding-001 and executes cosine similarity searches.")
    pdf.bullet_item("EvidenceEvaluator & Iterative Retrieval", "Extracts claims from drafts (SUPPORTED, DERIVED, CONFLICTING, MISSING) and executes targeted queries for missing data.")
    pdf.bullet_item("AnswerComposer", "Synthesizes final responses in the user's language, generating markdown tables and specifications with citation links.")
    pdf.bullet_item("OutputGovernor", "Audits composed output against retrieved source context chunks with bounded regeneration loops to enforce safety.")
    pdf.bullet_item("IConversationStateStore", "Persists conversation entities, constraints, validated facts, and candidate sets across turns.")

    pdf.ln(2)

    # =========================================================================
    # PAGE 2: VISUAL ARCHITECTURE FLOWCHART
    # =========================================================================
    pdf.add_page()
    pdf.chapter_title("2", "Architecture Flowchart: Chat Request Pipeline")
    pdf.body_p(
        "The diagram below illustrates the end-to-end journey of a user chat request from initial HTTP ingestion "
        "to verified client delivery, highlighting the concurrent execution of governance & search and the two feedback loops:"
    )
    
    pdf.ln(1)
    # Embed the high-resolution pipeline flowchart
    diag_w = 210 - (2 * pdf.l_margin)
    diag_h = diag_w * (1150 / 1600)  # Preserve aspect ratio
    pdf.image(diag1_path, x=pdf.l_margin, y=pdf.get_y(), w=diag_w, h=diag_h)
    pdf.set_y(pdf.get_y() + diag_h + 3)
    
    # =========================================================================
    # PAGE 3: THE 10 STAGES & WORKFLOW EXECUTORS
    # =========================================================================
    pdf.add_page()
    pdf.chapter_title("3", "Detailed Request Lifecycle (The 10 Stages)")
    pdf.body_p("When a user executes a chat query, the system processes it through 10 distinct stages:")
    
    stages = [
        ("Stage 1: HTTP Ingestion & Validation", "Incoming payload (Question, ConversationId) is validated by QueryRequestValidator via FluentValidation. Invalid requests return HTTP 400 immediately."),
        ("Stage 2: State Retrieval", "IConversationStateStore loads the ConversationState for the active ConversationId, restoring past entities, active constraints, and verified technical facts."),
        ("Stage 3: Query Refinement", "ConversationalQueryRefiner resolves context and pronouns (e.g. 'What is its airflow?' -> 'What is the airflow for 4114 N/2 H6PU?'), yielding an unambiguous EffectiveQuestion."),
        ("Stage 4: Parallel Governance & Search", "InputGovernor (intent classification) and QdrantVectorStore.SearchAsync (query embedding + vector search) execute concurrently to minimize latency."),
        ("Stage 5: Workflow Routing & Execution", "WorkflowRouter selects one of 8 specialized executors (e.g., Compatibility runs retrofit rules; Comparison builds multi-product matrix; Overview lists catalog)."),
        ("Stage 6: Evidence Evaluation Loop", "EvidenceEvaluator checks technical claims in the draft. If missing or conflicting claims exist, it formulates targeted vector queries (up to MaxRetrievalIterations = 3)."),
        ("Stage 7: Answer Composition", "AnswerComposer builds the final structured response in the user's language, assembling comparison tables, formulas, and grounded citations."),
        ("Stage 8: Output Governance Loop", "OutputGovernor validates that every claim in the composed answer is supported by source chunks. If unsupported claims exist, triggers bounded regeneration (up to 2 attempts)."),
        ("Stage 9: State Persistence & Tracing", "Updated entities, constraints, and facts are saved back to IConversationStateStore. Complete timing breakdown and prompt traces are logged in ExecutionTrace."),
        ("Stage 10: Client Delivery", "Markdown is converted to HTML via Markdig with CitationDto objects for POST /api/query, or streamed token-by-token via SSE on POST /api/query/stream.")
    ]
    
    for st_title, st_desc in stages:
        pdf.bullet_item(st_title, st_desc, indent=2)
        
    pdf.ln(2)
    
    pdf.callout_box("The 8 Specialized Technical Workflow Executors", [
        ("SimpleRag", "Single-model technical specification lookups and installation procedures."),
        ("Comparison", "Multi-model side-by-side attribute matrices with ebm-papst type key decoding."),
        ("Compatibility", "Retrofit and replacement compatibility checks (electrical, aerodynamic, mechanical)."),
        ("Diagnostic", "Fault isolation, symptom-to-cause mapping, and documented troubleshooting steps."),
        ("Calculation", "Formulas and parameter-driven math for airflow, power, and operating points."),
        ("Overview / Catalog", "Deterministic catalog inventories grouped by fan family and technology."),
        ("Design", "System design recommendations and configuration guidelines."),
        ("Clarification", "Formulates targeted clarifying questions when requirements are ambiguous.")
    ])
    
    # =========================================================================
    # PAGE 4: GEMINI API CALL ACCOUNTING & SCENARIOS
    # =========================================================================
    pdf.add_page()
    pdf.chapter_title("4", "Gemini API Request Breakdown per User Chat")
    pdf.body_p(
        "Each user query consumes a mixture of Google Gemini Embedding API calls (gemini-embedding-001) "
        "and Google Gemini Generative LLM calls (gemini-3.6-flash). "
        "The exact number depends on conversation depth, execution mode, and whether evidence/regeneration loops fire."
    )
    
    headers = ["Pipeline Stage", "Component", "Happy Path (1-Turn)", "Multi-Turn", "Worst-Case (Case E)"]
    rows = [
        ["Vector Search", "GeminiEmbeddingService", "1 Embedding", "1 Embedding", "4 Embeddings"],
        ["Stage 3: Refinement", "ConversationalQueryRefiner", "0 (Heuristic)", "1 LLM", "1 LLM"],
        ["Stage 4: Governance", "InputGovernor", "0-1 LLM", "1 LLM", "1 LLM"],
        ["Stage 5: Workflow", "WorkflowExecutor", "1 LLM", "1 LLM", "1 LLM"],
        ["Stage 6: Evidence", "EvidenceEvaluator", "1 LLM", "1 LLM", "7 LLMs (Eval+TQ+Draft)"],
        ["Stage 7: Composition", "AnswerComposer", "1 LLM", "1 LLM", "1 LLM"],
        ["Stage 8: Output Guard", "OutputGovernor", "1 LLM (0 on Stream)", "1 LLM", "5 LLMs (Val+Regen+Fail)"],
        ["Total API Requests", "Full Pipeline", "4 - 5 Requests", "7 Requests", "21 Requests (Max)"]
    ]
    col_w = [36, 42, 32, 28, 40]
    pdf.render_table(headers, rows, col_w, ['L', 'L', 'C', 'C', 'C'])
    
    pdf.section_title("Operational Scenario Comparison")
    scenarios = [
        ("Scenario A — Single-Turn Happy Path (POST /api/query)", "Consumes 1 Embedding + 3-4 LLMs (Total: 4-5 calls). First-turn queries bypass Query Refinement via regex heuristic fast-path."),
        ("Scenario B — Streaming Query (POST /api/query/stream)", "Consumes 1 Embedding + 2-3 LLMs (Total: 3-4 calls). Streams tokens directly to user with minimal Time-To-First-Token (TTFT)."),
        ("Scenario C — Multi-Turn Conversational Query", "Consumes 1 Embedding + 6 LLMs (Total: 7 calls). QueryRefiner executes an LLM prompt to resolve pronouns and merge active entity constraints."),
        ("Scenario D — Deterministic Domain Workflows (Overview / Replacement)", "Consumes 1-2 calls total. EbmProductCodeParser decodes product type keys deterministically, bypassing LLM generation for standard catalogs."),
        ("Scenario E — Worst-Case Bounded Execution", "Consumes 4 Embeddings + 17 LLMs (Total: 21 calls maximum). Triggered when multi-turn context, missing evidence claims, and output rejections occur simultaneously.")
    ]
    for sc_title, sc_desc in scenarios:
        pdf.bullet_item(sc_title, sc_desc, indent=2)

    # =========================================================================
    # PAGE 5: DEEP-DIVE INTO CASE E & CASE E DIAGRAM
    # =========================================================================
    pdf.add_page()
    pdf.chapter_title("5", "Deep-Dive: Case E Flowchart & Step-by-Step Accounting")
    pdf.body_p(
        "Case E represents the theoretical upper bound (21 Gemini requests) when all 3 safety mechanisms fire: "
        "multi-turn resolution + all 3 iterative retrievals + 2 output regenerations + fail-safe exit."
    )
    
    pdf.ln(1)
    # Embed the Case E diagram
    case_e_w = 210 - (2 * pdf.l_margin)
    case_e_h = case_e_w * (960 / 1600)
    pdf.image(diag2_path, x=pdf.l_margin, y=pdf.get_y(), w=case_e_w, h=case_e_h)
    pdf.set_y(pdf.get_y() + case_e_h + 3)
    
    # Granular Accounting Table for Case E
    case_e_headers = ["Phase", "Sub-Step / Component", "Operation Description", "Gemini API Type", "Calls"]
    case_e_rows = [
        ["Phase 1", "ConversationalQueryRefiner", "Resolve multi-turn constraints & pronouns", "LLM (JSON)", "1"],
        ["Phase 1", "InputGovernor", "Classify intent, extract entities & constraints", "LLM (JSON)", "1"],
        ["Phase 1", "GeminiEmbeddingService", "Generate vector embedding for initial Qdrant search", "Embedding", "1"],
        ["Phase 2", "WorkflowExecutor", "Generate initial draft answer from context chunks", "LLM (Text/JSON)", "1"],
        ["Phase 3", "EvidenceEvaluator (Iter 1-3)", "Evaluate claims against context (3 iterations)", "LLM (JSON)", "3"],
        ["Phase 3", "Targeted Query Gen (Iter 1-3)", "Generate keyword query for missing claim", "LLM (Text)", "3"],
        ["Phase 3", "Targeted Search (Iter 1-3)", "Embed targeted query for supplementary Qdrant search", "Embedding", "3"],
        ["Phase 3", "Intermediate Redrafts (Iter 1-3)", "Re-draft response with expanded chunk set", "LLM (Text)", "3"],
        ["Phase 4", "AnswerComposer", "Synthesize structured final response from verified claims", "LLM (Text)", "1"],
        ["Phase 5", "OutputGovernor (Attempts 1-2)", "Audit proposed answer for unsupported claims", "LLM (JSON)", "2"],
        ["Phase 5", "Regeneration Prompts (1-2)", "Rewrite answer resolving Output Governor issues", "LLM (Text)", "2"],
        ["Phase 5", "Fail-Safe Generator", "Generate polite fallback response directing to tech support", "LLM (Text)", "1"],
        ["Total", "Worst-Case Execution Upper Bound", "4 Embedding Calls + 17 Generative LLM Calls", "All APIs", "21"]
    ]
    case_e_widths = [16, 42, 60, 36, 14]
    pdf.render_table(case_e_headers, case_e_rows, case_e_widths, ['L', 'L', 'L', 'L', 'C'])
    
    pdf.section_title("Engineering & Safety Guardrail Justification")
    pdf.body_p(
        "In industrial engineering, parameters such as voltage (230V vs 400V), airflow direction (A vs V), and physical cutout dimensions "
        "carry physical equipment risk if hallucinated. The system enforces two hard mathematical limits:"
    )
    pdf.bullet_item("MaxRetrievalIterations = 3", "Prevents infinite retrieval loops when information is not in the database.")
    pdf.bullet_item("MaxRegenerationAttempts = 2", "Exits gracefully to FAIL_SAFE (advising the user to contact technical support) rather than presenting ungrounded guesses.")
    
    output_path = os.path.abspath("Technical_RAG_Architecture_and_Gemini_Requests.pdf")
    pdf.output(output_path)
    print(f"PDF successfully generated: {output_path}")

if __name__ == "__main__":
    build_pdf()
