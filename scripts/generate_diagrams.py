import os
from PIL import Image, ImageDraw, ImageFont

def draw_arrow(draw, start, end, color=(71, 85, 105), width=3, arrow_size=10):
    x0, y0 = start
    x1, y1 = end
    draw.line([start, end], fill=color, width=width)
    
    # Calculate arrowhead
    import math
    angle = math.atan2(y1 - y0, x1 - x0)
    
    p1 = (x1 - arrow_size * math.cos(angle - math.pi / 6),
          y1 - arrow_size * math.sin(angle - math.pi / 6))
    p2 = (x1 - arrow_size * math.cos(angle + math.pi / 6),
          y1 - arrow_size * math.sin(angle + math.pi / 6))
    
    draw.polygon([end, p1, p2], fill=color)

def generate_pipeline_diagram(output_path):
    W, H = 1600, 1150
    img = Image.new('RGB', (W, H), color=(255, 255, 255))
    draw = ImageDraw.Draw(img)
    
    # Load fonts
    f_title = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 24)
    f_sub = ImageFont.truetype(r'C:\Windows\Fonts\ariali.ttf', 16)
    f_node_b = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 17)
    f_node_t = ImageFont.truetype(r'C:\Windows\Fonts\arial.ttf', 14)
    f_tag = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 13)
    f_arrow = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 13)

    # Title header
    draw.rectangle([0, 0, W, 70], fill=(27, 54, 93))
    draw.text((30, 15), "Document RAG System — Chat Request Architecture & Pipeline", fill=(255, 255, 255), font=f_title)
    draw.text((30, 44), "End-to-end flow from user HTTP/SSE request to verified technical response delivery", fill=(203, 213, 225), font=f_sub)
    
    # 1. User Client Node
    cx = W // 2
    # User box
    draw.rounded_rectangle([cx - 160, 95, cx + 160, 145], radius=10, fill=(238, 242, 255), outline=(79, 70, 229), width=3)
    draw.text((cx, 120), "User / Web Client (Browser)", fill=(67, 56, 202), font=f_node_b, anchor="mm")
    
    # Arrow to API
    draw_arrow(draw, (cx, 145), (cx, 185), color=(79, 70, 229))
    draw.text((cx + 12, 165), "POST /api/query  or  /api/query/stream", fill=(79, 70, 229), font=f_arrow, anchor="lm")
    
    # 2. Web API & Validation
    draw.rounded_rectangle([cx - 200, 185, cx + 200, 235], radius=8, fill=(239, 246, 255), outline=(37, 99, 235), width=2)
    draw.text((cx, 202), "ASP.NET Core Web API & Controller", fill=(30, 64, 175), font=f_node_b, anchor="mm")
    draw.text((cx, 220), "Input Validation via FluentValidation (QueryRequestValidator)", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Arrow to RAG Pipeline
    draw_arrow(draw, (cx, 235), (cx, 275), color=(37, 99, 235))
    draw.text((cx + 12, 255), "Pass to ITechnicalRagOrchestrator", fill=(37, 99, 235), font=f_arrow, anchor="lm")

    # Big Subgraph Container: Technical RAG Pipeline
    pipe_top = 275
    pipe_bottom = 1000
    draw.rounded_rectangle([60, pipe_top, W - 60, pipe_bottom], radius=16, fill=(248, 250, 252), outline=(148, 163, 184), width=2)
    draw.rectangle([80, pipe_top - 14, 480, pipe_top + 16], fill=(27, 54, 93))
    draw.text((95, pipe_top + 1), "MULTI-STAGE TECHNICAL RAG PIPELINE", fill=(255, 255, 255), font=f_tag, anchor="lm")
    
    # Stage 1: Load Conversation State
    s1_y = 310
    draw.rounded_rectangle([cx - 220, s1_y, cx + 220, s1_y + 48], radius=8, fill=(254, 243, 199), outline=(217, 119, 6), width=2)
    draw.text((cx, s1_y + 16), "1. Load Conversation State", fill=(146, 64, 14), font=f_node_b, anchor="mm")
    draw.text((cx, s1_y + 34), "IConversationStateStore retrieves entities, constraints, validated facts", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    draw_arrow(draw, (cx, s1_y + 48), (cx, s1_y + 78), color=(217, 119, 6))
    
    # Stage 2: Conversational Query Refiner
    s2_y = s1_y + 78
    draw.rounded_rectangle([cx - 240, s2_y, cx + 240, s2_y + 48], radius=8, fill=(224, 242, 254), outline=(2, 132, 199), width=2)
    draw.text((cx, s2_y + 16), "2. Conversational Query Refiner", fill=(3, 105, 161), font=f_node_b, anchor="mm")
    draw.text((cx, s2_y + 34), "Resolves pronouns/anaphoras -> Unambiguous EffectiveQuestion", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Parallel Split to 3a and 3b
    s3_top = s2_y + 75
    draw_arrow(draw, (cx, s2_y + 48), (cx, s3_top - 5), color=(2, 132, 199))
    draw.text((cx, s3_top - 16), "Parallel Task Execution", fill=(100, 116, 139), font=f_arrow, anchor="mm")
    
    # Parallel Container
    draw.rounded_rectangle([100, s3_top, W - 100, s3_top + 90], radius=12, fill=(241, 245, 249), outline=(203, 213, 225), width=2)
    draw.text((120, s3_top + 14), "CONCURRENT EXECUTION (LATENCY OPTIMIZATION)", fill=(100, 116, 139), font=f_tag, anchor="lm")
    
    # 3a: Input Governor
    draw.rounded_rectangle([130, s3_top + 28, cx - 20, s3_top + 78], radius=8, fill=(243, 232, 255), outline=(147, 51, 234), width=2)
    draw.text(( (130 + cx - 20)//2, s3_top + 43 ), "3a. Input Governor (Intent & Entities)", fill=(107, 33, 168), font=f_node_b, anchor="mm")
    draw.text(( (130 + cx - 20)//2, s3_top + 63 ), "Classifies 1 of 8 intents; extracts technical specs", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # 3b: Vector Search
    draw.rounded_rectangle([cx + 20, s3_top + 28, W - 130, s3_top + 78], radius=8, fill=(220, 252, 231), outline=(22, 163, 74), width=2)
    draw.text(( (cx + 20 + W - 130)//2, s3_top + 43 ), "3b. Qdrant Vector Store Search", fill=(21, 128, 61), font=f_node_b, anchor="mm")
    draw.text(( (cx + 20 + W - 130)//2, s3_top + 63 ), "Gemini embeddings (gemini-001) + Cosine search", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Join into Workflow Router
    s4_y = s3_top + 115
    draw.line([( (130 + cx - 20)//2, s3_top + 78 ), ( (130 + cx - 20)//2, s4_y - 12 )], fill=(147, 51, 234), width=2)
    draw.line([( (cx + 20 + W - 130)//2, s3_top + 78 ), ( (cx + 20 + W - 130)//2, s4_y - 12 )], fill=(22, 163, 74), width=2)
    draw.line([( (130 + cx - 20)//2, s4_y - 12 ), ( (cx + 20 + W - 130)//2, s4_y - 12 )], fill=(71, 85, 105), width=2)
    draw_arrow(draw, (cx, s4_y - 12), (cx, s4_y), color=(71, 85, 105))
    
    # Stage 4 & 5: Workflow Router & Executor
    draw.rounded_rectangle([cx - 250, s4_y, cx + 250, s4_y + 48], radius=8, fill=(239, 246, 255), outline=(37, 99, 235), width=2)
    draw.text((cx, s4_y + 16), "4 & 5. Workflow Router -> Specialized Executor", fill=(30, 64, 175), font=f_node_b, anchor="mm")
    draw.text((cx, s4_y + 34), "SimpleRag | Comparison | Compatibility | Diagnostic | Calculation | Overview", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    draw_arrow(draw, (cx, s4_y + 48), (cx, s4_y + 78), color=(37, 99, 235))
    
    # Stage 6: Evidence Evaluator (with Loop)
    s6_y = s4_y + 78
    draw.rounded_rectangle([cx - 240, s6_y, cx + 240, s6_y + 52], radius=8, fill=(254, 243, 199), outline=(217, 119, 6), width=2)
    draw.text((cx, s6_y + 17), "6. Evidence Evaluator (Claim-Level Auditing)", fill=(146, 64, 14), font=f_node_b, anchor="mm")
    draw.text((cx, s6_y + 36), "Extracts claims: SUPPORTED | DERIVED | CONFLICTING | MISSING", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Evidence Iterative Loop Box (Right Side)
    loop_x0, loop_y0, loop_x1, loop_y1 = W - 370, s6_y - 8, W - 110, s6_y + 60
    draw.rounded_rectangle([loop_x0, loop_y0, loop_x1, loop_y1], radius=8, fill=(255, 251, 235), outline=(217, 119, 6), width=2)
    draw.text(( (loop_x0+loop_x1)//2, loop_y0 + 16 ), "Iterative Retrieval Loop", fill=(146, 64, 14), font=f_node_b, anchor="mm")
    draw.text(( (loop_x0+loop_x1)//2, loop_y0 + 34 ), "Targeted query -> Qdrant search", fill=(180, 83, 9), font=f_node_t, anchor="mm")
    draw.text(( (loop_x0+loop_x1)//2, loop_y0 + 50 ), "(Max 3 Iterations)", fill=(217, 119, 6), font=f_tag, anchor="mm")
    
    # Loop arrows
    draw_arrow(draw, (cx + 240, s6_y + 20), (loop_x0, s6_y + 20), color=(217, 119, 6))
    draw.text((cx + 245, s6_y + 10), "Missing / Conflicting", fill=(180, 83, 9), font=f_arrow, anchor="lm")
    draw.line([( (loop_x0+loop_x1)//2, loop_y1 ), ( (loop_x0+loop_x1)//2, s6_y + 70 )], fill=(217, 119, 6), width=2)
    draw.line([( (loop_x0+loop_x1)//2, s6_y + 70 ), ( cx + 180, s6_y + 70 )], fill=(217, 119, 6), width=2)
    draw_arrow(draw, (cx + 180, s6_y + 70), (cx + 180, s6_y + 52), color=(217, 119, 6))
    
    draw_arrow(draw, (cx, s6_y + 52), (cx, s6_y + 88), color=(22, 163, 74))
    draw.text((cx - 10, s6_y + 70), "All Claims Verified", fill=(21, 128, 61), font=f_arrow, anchor="rm")
    
    # Stage 7: Answer Composer
    s7_y = s6_y + 88
    draw.rounded_rectangle([cx - 220, s7_y, cx + 220, s7_y + 48], radius=8, fill=(224, 242, 254), outline=(2, 132, 199), width=2)
    draw.text((cx, s7_y + 16), "7. Answer Composer (Synthesis)", fill=(3, 105, 161), font=f_node_b, anchor="mm")
    draw.text((cx, s7_y + 34), "Grounds answer strictly in evidence; generates Markdown tables", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    draw_arrow(draw, (cx, s7_y + 48), (cx, s7_y + 78), color=(2, 132, 199))
    
    # Stage 8: Output Governor (with Regen Loop)
    s8_y = s7_y + 78
    draw.rounded_rectangle([cx - 240, s8_y, cx + 240, s8_y + 52], radius=8, fill=(254, 226, 226), outline=(220, 38, 38), width=2)
    draw.text((cx, s8_y + 17), "8. Output Governor & Safety Guardrail", fill=(153, 27, 27), font=f_node_b, anchor="mm")
    draw.text((cx, s8_y + 36), "Validates composed text vs sources: APPROVE | REGENERATE | FAIL_SAFE", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Regen Loop Box (Left Side)
    r_x0, r_y0, r_x1, r_y1 = 110, s8_y - 8, 370, s8_y + 60
    draw.rounded_rectangle([r_x0, r_y0, r_x1, r_y1], radius=8, fill=(254, 242, 242), outline=(220, 38, 38), width=2)
    draw.text(( (r_x0+r_x1)//2, r_y0 + 16 ), "Bounded Regeneration", fill=(153, 27, 27), font=f_node_b, anchor="mm")
    draw.text(( (r_x0+r_x1)//2, r_y0 + 34 ), "Issues feedback -> Re-prompt LLM", fill=(185, 28, 28), font=f_node_t, anchor="mm")
    draw.text(( (r_x0+r_x1)//2, r_y0 + 50 ), "(Max 2 Attempts -> Fail-Safe)", fill=(220, 38, 38), font=f_tag, anchor="mm")
    
    # Regen loop arrows
    draw_arrow(draw, (cx - 240, s8_y + 20), (r_x1, s8_y + 20), color=(220, 38, 38))
    draw.text((cx - 245, s8_y + 10), "Unsupported Claim", fill=(185, 28, 28), font=f_arrow, anchor="rm")
    draw.line([( (r_x0+r_x1)//2, r_y1 ), ( (r_x0+r_x1)//2, s8_y + 70 )], fill=(220, 38, 38), width=2)
    draw.line([( (r_x0+r_x1)//2, s8_y + 70 ), ( cx - 180, s8_y + 70 )], fill=(220, 38, 38), width=2)
    draw_arrow(draw, (cx - 180, s8_y + 70), (cx - 180, s8_y + 52), color=(220, 38, 38))
    
    draw_arrow(draw, (cx, s8_y + 52), (cx, s8_y + 88), color=(22, 163, 74))
    draw.text((cx + 10, s8_y + 70), "Approved / Fail-Safe", fill=(21, 128, 61), font=f_arrow, anchor="lm")
    
    # Stage 9: Save State & Execution Trace
    s9_y = s8_y + 88
    draw.rounded_rectangle([cx - 220, s9_y, cx + 220, s9_y + 48], radius=8, fill=(254, 243, 199), outline=(217, 119, 6), width=2)
    draw.text((cx, s9_y + 16), "9. Update State & Record Execution Trace", fill=(146, 64, 14), font=f_node_b, anchor="mm")
    draw.text((cx, s9_y + 34), "Persists updated constraints; logs millisecond timings & prompts", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Out of container to Delivery
    draw_arrow(draw, (cx, s9_y + 48), (cx, 1025), color=(37, 99, 235))
    
    # Stage 10: Client Delivery
    draw.rounded_rectangle([cx - 240, 1025, cx + 240, 1075], radius=10, fill=(238, 242, 255), outline=(79, 70, 229), width=3)
    draw.text((cx, 1043), "10. Client Delivery (JSON / SSE Stream)", fill=(67, 56, 202), font=f_node_b, anchor="mm")
    draw.text((cx, 1061), "POST /api/query (HTML + Citations)  or  POST /api/query/stream (Real-time SSE)", fill=(71, 85, 105), font=f_node_t, anchor="mm")
    
    # Final arrow back to user
    draw.line([(cx + 240, 1050), (W - 30, 1050)], fill=(79, 70, 229), width=2)
    draw.line([(W - 30, 1050), (W - 30, 120)], fill=(79, 70, 229), width=2)
    draw_arrow(draw, (W - 30, 120), (cx + 160, 120), color=(79, 70, 229))
    draw.text((W - 35, 580), "Rendered Response Delivered to Client", fill=(79, 70, 229), font=f_arrow, anchor="rm")
    
    img.save(output_path, quality=95)
    print(f"Pipeline diagram saved to {output_path}")

def generate_case_e_diagram(output_path):
    W, H = 1600, 960
    cx = W // 2
    img = Image.new('RGB', (W, H), color=(255, 255, 255))
    draw = ImageDraw.Draw(img)
    
    f_title = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 24)
    f_sub = ImageFont.truetype(r'C:\Windows\Fonts\ariali.ttf', 15)
    f_box_title = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 16)
    f_item_b = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 14)
    f_item_t = ImageFont.truetype(r'C:\Windows\Fonts\arial.ttf', 13)
    f_badge = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 13)
    f_banner = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 18)

    # Title header
    draw.rectangle([0, 0, W, 70], fill=(27, 54, 93))
    draw.text((30, 15), "Case E: Worst-Case Bounded Scenario (21 Gemini Requests Breakdown)", fill=(255, 255, 255), font=f_title)
    draw.text((30, 44), "Simultaneous multi-turn context + 3 iterative vector retrievals + 2 regeneration attempts -> Fail-Safe exit", fill=(203, 213, 225), font=f_sub)
    
    # 5 Phase Cards Layout (Grid / Flow)
    # Phase 1: Top Left
    p1_box = [60, 90, 520, 370]
    draw.rounded_rectangle(p1_box, radius=12, fill=(248, 250, 252), outline=(2, 132, 199), width=2)
    draw.rectangle([p1_box[0], p1_box[1], p1_box[2], p1_box[1] + 36], fill=(2, 132, 199))
    draw.text((p1_box[0] + 15, p1_box[1] + 18), "PHASE 1: Turn Resolution & Intent", fill=(255, 255, 255), font=f_box_title, anchor="lm")
    
    draw.text((p1_box[0] + 15, p1_box[1] + 55), "[1] ConversationalQueryRefiner (1 LLM)", fill=(3, 105, 161), font=f_item_b)
    draw.text((p1_box[0] + 15, p1_box[1] + 75), "Resolves multi-turn context, constraints, pronouns.", fill=(71, 85, 105), font=f_item_t)
    
    draw.text((p1_box[0] + 15, p1_box[1] + 110), "[2] InputGovernor (1 LLM)", fill=(3, 105, 161), font=f_item_b)
    draw.text((p1_box[0] + 15, p1_box[1] + 130), "Classifies intent, extracts entities & requirements.", fill=(71, 85, 105), font=f_item_t)
    
    draw.text((p1_box[0] + 15, p1_box[1] + 165), "[3] GeminiEmbeddingService (1 Embedding)", fill=(21, 128, 61), font=f_item_b)
    draw.text((p1_box[0] + 15, p1_box[1] + 185), "Embeds query for initial Qdrant vector retrieval.", fill=(71, 85, 105), font=f_item_t)
    
    # Subtotal Badge
    draw.rounded_rectangle([p1_box[0] + 15, p1_box[3] - 45, p1_box[2] - 15, p1_box[3] - 12], radius=6, fill=(224, 242, 254), outline=(2, 132, 199), width=1)
    draw.text(( (p1_box[0] + p1_box[2])//2, p1_box[3] - 28 ), "Phase 1 Subtotal: 2 LLMs + 1 Embedding", fill=(3, 105, 161), font=f_badge, anchor="mm")

    # Phase 2: Top Middle
    p2_box = [560, 90, 1020, 370]
    draw.rounded_rectangle(p2_box, radius=12, fill=(248, 250, 252), outline=(37, 99, 235), width=2)
    draw.rectangle([p2_box[0], p2_box[1], p2_box[2], p2_box[1] + 36], fill=(37, 99, 235))
    draw.text((p2_box[0] + 15, p2_box[1] + 18), "PHASE 2: Initial Workflow Draft", fill=(255, 255, 255), font=f_box_title, anchor="lm")
    
    draw.text((p2_box[0] + 15, p2_box[1] + 65), "[4] WorkflowExecutor (1 LLM)", fill=(30, 64, 175), font=f_item_b)
    draw.text((p2_box[0] + 15, p2_box[1] + 90), "Generates initial technical draft from the 1st chunk set.", fill=(71, 85, 105), font=f_item_t)
    draw.text((p2_box[0] + 15, p2_box[1] + 115), "Applied to SimpleRag, Diagnostic, Design, Calculation.", fill=(71, 85, 105), font=f_item_t)
    
    # Subtotal Badge
    draw.rounded_rectangle([p2_box[0] + 15, p2_box[3] - 45, p2_box[2] - 15, p2_box[3] - 12], radius=6, fill=(239, 246, 255), outline=(37, 99, 235), width=1)
    draw.text(( (p2_box[0] + p2_box[2])//2, p2_box[3] - 28 ), "Phase 2 Subtotal: 1 LLM", fill=(30, 64, 175), font=f_badge, anchor="mm")

    # Phase 4: Top Right
    p4_box = [1060, 90, 1540, 370]
    draw.rounded_rectangle(p4_box, radius=12, fill=(248, 250, 252), outline=(147, 51, 234), width=2)
    draw.rectangle([p4_box[0], p4_box[1], p4_box[2], p4_box[1] + 36], fill=(147, 51, 234))
    draw.text((p4_box[0] + 15, p4_box[1] + 18), "PHASE 4: Answer Synthesis", fill=(255, 255, 255), font=f_box_title, anchor="lm")
    
    draw.text((p4_box[0] + 15, p4_box[1] + 65), "[14] AnswerComposer (1 LLM)", fill=(107, 33, 168), font=f_item_b)
    draw.text((p4_box[0] + 15, p4_box[1] + 90), "Synthesizes comprehensive markdown response.", fill=(71, 85, 105), font=f_item_t)
    draw.text((p4_box[0] + 15, p4_box[1] + 115), "Integrates all verified claims & citation links.", fill=(71, 85, 105), font=f_item_t)
    draw.text((p4_box[0] + 15, p4_box[1] + 140), "Strictly preserves user question language.", fill=(71, 85, 105), font=f_item_t)
    
    # Subtotal Badge
    draw.rounded_rectangle([p4_box[0] + 15, p4_box[3] - 45, p4_box[2] - 15, p4_box[3] - 12], radius=6, fill=(243, 232, 255), outline=(147, 51, 234), width=1)
    draw.text(( (p4_box[0] + p4_box[2])//2, p4_box[3] - 28 ), "Phase 4 Subtotal: 1 LLM", fill=(107, 33, 168), font=f_badge, anchor="mm")

    # Arrows connecting Top Row
    draw_arrow(draw, (p1_box[2], 230), (p2_box[0], 230), color=(71, 85, 105))
    draw_arrow(draw, (p2_box[2], 230), (cx, 400), color=(71, 85, 105))

    # Phase 3: Bottom Left (Iterative Evidence Loop)
    p3_box = [60, 410, 880, 770]
    draw.rounded_rectangle(p3_box, radius=12, fill=(255, 251, 235), outline=(217, 119, 6), width=2)
    draw.rectangle([p3_box[0], p3_box[1], p3_box[2], p3_box[1] + 36], fill=(217, 119, 6))
    draw.text((p3_box[0] + 15, p3_box[1] + 18), "PHASE 3: Iterative Evidence Loop (Max 3 Iterations)", fill=(255, 255, 255), font=f_box_title, anchor="lm")
    
    # 3 Iterations breakdown
    iters_text = [
        ("Iteration 1", "[5] EvidenceEvaluator (1 LLM) -> [6] Targeted Query Gen (1 LLM) -> [7] Embedding (1 Embed) -> [8] Redraft (1 LLM)"),
        ("Iteration 2", "[9] EvidenceEvaluator (1 LLM) -> [10] Targeted Query Gen (1 LLM) -> [11] Embedding (1 Embed) -> [12] Redraft (1 LLM)"),
        ("Iteration 3", "[13] EvidenceEvaluator (1 LLM) -> [14] Targeted Query Gen (1 LLM) -> [15] Embedding (1 Embed) -> [16] Redraft (1 LLM)")
    ]
    
    cur_y = p3_box[1] + 50
    for it_name, it_desc in iters_text:
        draw.text((p3_box[0] + 15, cur_y), it_name + ":", fill=(146, 64, 14), font=f_item_b)
        draw.text((p3_box[0] + 15, cur_y + 20), it_desc, fill=(71, 85, 105), font=f_item_t)
        cur_y += 58
        
    draw.text((p3_box[0] + 15, cur_y + 10), "Trigger: Claims flagged as MISSING or CONFLICTING after each evaluation.", fill=(180, 83, 9), font=f_item_b)
    draw.text((p3_box[0] + 15, cur_y + 30), "Safeguard: Loop terminates when no new claims are missing or max iterations reached.", fill=(71, 85, 105), font=f_item_t)
    
    # Subtotal Badge
    draw.rounded_rectangle([p3_box[0] + 15, p3_box[3] - 45, p3_box[2] - 15, p3_box[3] - 12], radius=6, fill=(254, 243, 199), outline=(217, 119, 6), width=1)
    draw.text(( (p3_box[0] + p3_box[2])//2, p3_box[3] - 28 ), "Phase 3 Subtotal: 9 LLMs + 3 Embeddings (12 Requests)", fill=(146, 64, 14), font=f_badge, anchor="mm")

    # Phase 5: Bottom Right (Output Governor & Regeneration)
    p5_box = [920, 410, 1540, 770]
    draw.rounded_rectangle(p5_box, radius=12, fill=(254, 242, 242), outline=(220, 38, 38), width=2)
    draw.rectangle([p5_box[0], p5_box[1], p5_box[2], p5_box[1] + 36], fill=(220, 38, 38))
    draw.text((p5_box[0] + 15, p5_box[1] + 18), "PHASE 5: Output Governor & Regeneration Loop", fill=(255, 255, 255), font=f_box_title, anchor="lm")
    
    p5_steps = [
        ("Attempt 1 Validation & Regen", "[17] OutputGovernor (1 LLM) -> [18] Regeneration Prompt (1 LLM)", "Detects unverified claim; re-prompts Answer Composer with issue feedback."),
        ("Attempt 2 Validation & Regen", "[19] OutputGovernor (1 LLM) -> [20] Regeneration Prompt (1 LLM)", "Second audit; re-prompts model to resolve remaining factual inconsistencies."),
        ("Max Attempts Reached: Fail-Safe", "[21] Fail-Safe Prompt (1 LLM)", "Max attempts exceeded without 100% verification -> Polite fail-safe to tech support.")
    ]
    
    cur_y = p5_box[1] + 50
    for s_title, s_calls, s_sub in p5_steps:
        draw.text((p5_box[0] + 15, cur_y), s_title + ":", fill=(153, 27, 27), font=f_item_b)
        draw.text((p5_box[0] + 15, cur_y + 20), s_calls, fill=(185, 28, 28), font=f_item_b)
        draw.text((p5_box[0] + 15, cur_y + 38), s_sub, fill=(71, 85, 105), font=f_item_t)
        cur_y += 62
        
    # Subtotal Badge
    draw.rounded_rectangle([p5_box[0] + 15, p5_box[3] - 45, p5_box[2] - 15, p5_box[3] - 12], radius=6, fill=(254, 226, 226), outline=(220, 38, 38), width=1)
    draw.text(( (p5_box[0] + p5_box[2])//2, p5_box[3] - 28 ), "Phase 5 Subtotal: 5 LLMs (5 Requests)", fill=(153, 27, 27), font=f_badge, anchor="mm")

    # Arrow from Phase 3 to Phase 4
    draw_arrow(draw, (p3_box[2], 520), (p4_box[0] + 100, 370), color=(71, 85, 105))
    # Arrow from Phase 4 to Phase 5
    draw_arrow(draw, ( (p4_box[0]+p4_box[2])//2, 370 ), ( (p5_box[0]+p5_box[2])//2, 410 ), color=(71, 85, 105))

    # Bottom Grand Total Banner
    b_y = 800
    draw.rounded_rectangle([60, b_y, W - 60, b_y + 110], radius=14, fill=(27, 54, 93), outline=(43, 108, 176), width=3)
    draw.text((cx, b_y + 35), "GRAND TOTAL: 4 EMBEDDING CALLS + 17 GENERATIVE LLM CALLS = 21 GEMINI REQUESTS", fill=(255, 255, 255), font=f_banner, anchor="mm")
    draw.text((cx, b_y + 70), "Strict Mathematical Bounds: MaxRetrievalIterations = 3  |  MaxRegenerationAttempts = 2  |  Zero Unverified Claims", fill=(203, 213, 225), font=f_item_t, anchor="mm")

    img.save(output_path, quality=95)
    print(f"Case E diagram saved to {output_path}")

if __name__ == "__main__":
    os.makedirs("docs/diagrams", exist_ok=True)
    generate_pipeline_diagram("docs/diagrams/pipeline_flowchart.png")
    generate_case_e_diagram("docs/diagrams/case_e_flowchart.png")
