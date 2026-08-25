import os
import glob
import json
import requests
import urllib3
import re
from fpdf import FPDF

# Disable SSL warnings
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

API_BASE = "https://localhost:7251"

class EvaluationPDF(FPDF):
    def header(self):
        if self.page_no() == 1:
            return  # No header on cover page
        self.set_font("helvetica", "B", 8)
        self.set_text_color(100, 110, 120)
        self.cell(0, 10, "Document RAG System - Evaluation Report", align="L")
        self.set_font("helvetica", "I", 8)
        self.cell(0, 10, f"Page {self.page_no()}", align="R")
        self.ln(10)
        self.set_draw_color(200, 200, 200)
        self.line(10, 18, 200, 18)
        self.ln(5)

    def footer(self):
        if self.page_no() == 1:
            return  # No footer on cover page
        self.set_y(-15)
        self.set_font("helvetica", "I", 8)
        self.set_text_color(128, 128, 128)
        self.cell(0, 10, "Confidential - For Internal Evaluation Only", align="C")

def sanitize_text(text):
    if not text:
        return ""
    replacements = {
        "\u2013": "-",
        "\u2014": "-",
        "\u2018": "'",
        "\u2019": "'",
        "\u201c": '"',
        "\u201d": '"',
        "\u2022": "*",
        "\u2026": "...",
        "\u00a0": " ",
        "\u2212": "-",
        "\u2192": "->",
        "\u00b0": " degrees ",
        "\u00b1": "+/-",
        "\u00b2": "2",
        "\u00b3": "3",
    }
    for k, v in replacements.items():
        text = text.replace(k, v)
    
    # We can safely encode to cp1252 and decode back to cp1252.
    # It preserves Danish characters like æ, ø, å perfectly!
    return text.encode("cp1252", errors="replace").decode("cp1252")

def html_to_plain_text(html):
    if not html:
        return ""
    # Pre-process list items
    html = re.sub(r'<li>\s*<strong>(.*?)</strong>', r'  * \1', html)
    html = re.sub(r'<li>\s*<b>(.*?)</b>', r'  * \1', html)
    html = re.sub(r'<li>', r'  * ', html)
    html = re.sub(r'</li>', r'\n', html)
    # Paragraphs and line breaks
    html = re.sub(r'<p>', r'', html)
    html = re.sub(r'</p>', r'\n\n', html)
    html = re.sub(r'<br\s*/?>', r'\n', html)
    # Bold / Strong
    html = re.sub(r'<strong>(.*?)</strong>', r'\1', html)
    html = re.sub(r'<b>(.*?)</b>', r'\1', html)
    # Strip any other tags
    html = re.sub(r'<[^>]+>', '', html)
    # Clean up multiple newlines
    html = re.sub(r'\n{3,}', '\n\n', html)
    return html.strip()

def get_uploaded_documents():
    try:
        r = requests.get(f"{API_BASE}/api/documents", verify=False, timeout=10)
        if r.status_code == 200:
            return r.json()
    except Exception as e:
        print("Error fetching documents:", e)
    return []

def upload_pdf(pdf_path):
    filename = os.path.basename(pdf_path)
    print(f"Uploading and processing {filename}...")
    try:
        with open(pdf_path, "rb") as f:
            files = {"file": (filename, f, "application/pdf")}
            r = requests.post(f"{API_BASE}/api/documents/upload", files=files, verify=False, timeout=120)
            if r.status_code == 200:
                print(f"Successfully processed {filename}")
                return r.json()
            else:
                print(f"Failed to process {filename}: {r.status_code} - {r.text}")
    except Exception as e:
        print(f"Exception uploading {filename}: {e}")
    return None

def find_matching_pdf(md_filename):
    base_name = os.path.basename(md_filename)
    if base_name.startswith("Spørgsmål_"):
        base_name = base_name[len("Spørgsmål_"):]
    name_without_ext, _ = os.path.splitext(base_name)
    
    data_dir = r"data/Generelt"
    if not os.path.exists(data_dir):
        print(f"Data directory '{data_dir}' does not exist!")
        return None
        
    for file in os.listdir(data_dir):
        f_name, f_ext = os.path.splitext(file)
        if f_name.lower() == name_without_ext.lower() and f_ext.lower() == ".pdf":
            return os.path.join(data_dir, file)
    return None

def query_api(question):
    try:
        payload = {"Question": question}
        r = requests.post(f"{API_BASE}/api/query", json=payload, verify=False, timeout=60)
        if r.status_code == 200:
            return r.json()
        else:
            print(f"Query failed: {r.status_code} - {r.text}")
    except Exception as e:
        print(f"Query exception: {e}")
    return None

def main():
    # 1. Fetch currently uploaded documents to avoid redundant work
    print("Fetching already uploaded documents...")
    uploaded_docs = get_uploaded_documents()
    uploaded_filenames = {doc["fileName"].lower() for doc in uploaded_docs}
    print(f"Already uploaded: {list(uploaded_filenames)}")

    # 2. Find all 10 'Spørgsmål' markdown files in docs/
    md_files = sorted(glob.glob(os.path.join("docs", "Spørgsmål_*.md")))
    if not md_files:
        print("No Spørgsmål files found in 'docs/'!")
        return

    print(f"Found {len(md_files)} Spørgsmål files to process.")

    results = []

    # 3. Process each Spørgsmål document
    for md_file in md_files:
        doc_name = os.path.basename(md_file)
        print(f"\n========================================\nProcessing Spørgsmål document: {doc_name}")
        
        # Match with PDF
        pdf_path = find_matching_pdf(md_file)
        if not pdf_path:
            print(f"Warning: No matching PDF found for {doc_name}")
            continue
            
        pdf_name = os.path.basename(pdf_path)
        print(f"Matching PDF: {pdf_name}")

        # Check if PDF is already uploaded, if not upload it
        if pdf_name.lower() not in uploaded_filenames:
            print(f"PDF '{pdf_name}' not in system. Uploading now...")
            upload_result = upload_pdf(pdf_path)
            if not upload_result:
                print(f"Skipping queries for {doc_name} due to upload failure.")
                continue
        else:
            print(f"PDF '{pdf_name}' already uploaded and indexed.")

        # Read JSON Q&As
        try:
            with open(md_file, "r", encoding="utf-8") as f:
                questions_data = json.load(f)
        except Exception as e:
            print(f"Failed to load JSON from {doc_name}: {e}")
            continue

        qa_pairs = []
        # 4. Query each question and record response
        for q_item in questions_data:
            q_id = q_item.get("id", "")
            question_text = q_item.get("question", "")
            expected_answer = q_item.get("answer", "")
            difficulty = q_item.get("difficulty", "N/A")
            topic = q_item.get("topic", "N/A")

            print(f"Querying Q{q_id}: {question_text[:60]}...")
            api_response = query_api(question_text)
            
            if api_response:
                html_answer = api_response.get("answer", "")
                plain_answer = html_to_plain_text(html_answer)
            else:
                plain_answer = "ERROR: Failed to retrieve answer from local Web API."

            qa_pairs.append({
                "id": q_id,
                "question": question_text,
                "expected_answer": expected_answer,
                "api_answer": plain_answer,
                "difficulty": difficulty,
                "topic": topic
            })

        results.append({
            "document_name": doc_name,
            "pdf_name": pdf_name,
            "qa_pairs": qa_pairs
        })

    # 5. Generate PDF report
    print("\nGenerating evaluation PDF report 'test.pdf'...")
    generate_pdf(results, "test.pdf")

def generate_pdf(results, output_path="test.pdf"):
    pdf = EvaluationPDF()
    pdf.set_auto_page_break(auto=True, margin=20)
    
    # Cover page
    pdf.add_page()
    pdf.set_fill_color(26, 54, 93) # Deep Blue
    pdf.rect(0, 0, 210, 297, "F")
    
    pdf.set_y(80)
    pdf.set_font("helvetica", "B", 24)
    pdf.set_text_color(255, 255, 255)
    pdf.cell(0, 15, "Document RAG System", align="C")
    pdf.ln(15)
    
    pdf.set_font("helvetica", "B", 18)
    pdf.cell(0, 12, "Evaluation & Quality Report", align="C")
    pdf.ln(15)
    
    pdf.set_y(130)
    pdf.set_font("helvetica", "", 12)
    pdf.set_text_color(220, 220, 220)
    pdf.cell(0, 10, "A comparative analysis of expected document answers", align="C")
    pdf.ln(10)
    pdf.cell(0, 10, "against local Web API RAG responses.", align="C")
    pdf.ln(15)
    
    pdf.set_y(220)
    pdf.set_font("helvetica", "I", 10)
    pdf.set_text_color(180, 180, 180)
    pdf.cell(0, 8, "Date: July 2026", align="C")
    pdf.ln(8)
    pdf.cell(0, 8, f"Total Documents: {len(results)}", align="C")
    pdf.ln(8)
    total_q = sum(len(doc['qa_pairs']) for doc in results)
    pdf.cell(0, 8, f"Total Questions Evaluated: {total_q}", align="C")
    pdf.ln(10)
    
    # Document Sections
    for doc in results:
        pdf.add_page()
        
        # Section Heading
        pdf.set_font("helvetica", "B", 14)
        pdf.set_text_color(26, 54, 93) # Deep Blue
        pdf.set_x(pdf.l_margin)
        pdf.multi_cell(0, 10, sanitize_text(f"Document: {doc['document_name']}"))
        pdf.ln(3)
        
        for qa in doc['qa_pairs']:
            # Question Header
            pdf.set_font("helvetica", "B", 10)
            pdf.set_text_color(100, 110, 120) # Slate Grey
            difficulty = qa.get('difficulty', 'N/A')
            topic = qa.get('topic', 'N/A')
            header_str = f"Q{qa['id']} - {topic} ({difficulty})"
            pdf.set_x(pdf.l_margin)
            pdf.multi_cell(0, 6, sanitize_text(header_str))
            
            # Question Text
            pdf.set_font("helvetica", "B", 11)
            pdf.set_text_color(30, 30, 30) # Dark text
            pdf.set_x(pdf.l_margin)
            pdf.multi_cell(0, 6, sanitize_text(f"Q: {qa['question']}"))
            pdf.ln(2)
            
            # Expected Answer (from document)
            pdf.set_font("helvetica", "B", 10)
            pdf.set_text_color(100, 110, 120)
            pdf.set_x(pdf.l_margin)
            pdf.cell(0, 6, "Expected Answer (Document):")
            pdf.ln(6)
            
            pdf.set_font("helvetica", "", 10)
            pdf.set_text_color(40, 40, 40)
            pdf.set_fill_color(245, 247, 250)
            pdf.set_x(pdf.l_margin)
            pdf.multi_cell(0, 5, sanitize_text(qa['expected_answer']), fill=True)
            pdf.ln(2)
            
            # RAG Response
            pdf.set_font("helvetica", "B", 10)
            pdf.set_text_color(100, 110, 120)
            pdf.set_x(pdf.l_margin)
            pdf.cell(0, 6, "Web API RAG Response:")
            pdf.ln(6)
            
            pdf.set_font("helvetica", "", 10)
            pdf.set_text_color(40, 40, 40)
            pdf.set_x(pdf.l_margin)
            pdf.multi_cell(0, 5, sanitize_text(qa['api_answer']), fill=True)
            
            pdf.ln(4)
            
            # Draw separator
            pdf.set_draw_color(230, 230, 230)
            pdf.line(pdf.l_margin, pdf.get_y(), 210 - pdf.r_margin, pdf.get_y())
            pdf.ln(3)
            
    pdf.output(output_path)
    print(f"PDF successfully generated at: {output_path}")

if __name__ == "__main__":
    main()
