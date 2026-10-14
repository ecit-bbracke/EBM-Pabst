# Document RAG (Retrieval-Augmented Generation) System

[![CI Pipeline](https://github.com/ecit-bbracke/EBM-Pabst/actions/workflows/ci.yml/badge.svg)](https://github.com/ecit-bbracke/EBM-Pabst/actions/workflows/ci.yml)

A robust, enterprise-grade Document Retrieval-Augmented Generation (RAG) system built with **.NET 10**, **Qdrant Vector Database**, and **Google Gemini** (for both embeddings and LLM orchestration). 

This system allows you to automatically ingest high-volumes of PDF technical data, generate semantic text embeddings, store them in a vector database, and perform context-aware queries that include verifiable citations.

---

## 🛠️ Project Structure

The project follows clean architecture principles:
*   **`DocumentRagSystem.Core/`**: Contains core domain models (`Document`, `DocumentChunk`), interface definitions, and the orchestration logic (`DocumentProcessor`, `ChunkingService`).
*   **`DocumentRagSystem.Infrastructure/`**: Handles external integrations—`PdfTextExtractor` (via `PdfPig`), `GeminiEmbeddingService` (via Gemini HTTP API), `GeminiLlmService` (via Gemini HTTP API), and `QdrantVectorStore` (via `Qdrant.Client`).
*   **`DocumentRagSystem.WebApi/`**: Minimal API hosting endpoints for document uploads, metadata inspection, and semantic query endpoints with citations. Includes global exception handling and background queues.
*   **`DocumentRagSystem.Worker/`**: Background service that runs scheduled tasks (like hourly temp-file cleanups) and automatically ingests all technical PDF documents from `data/Generelt/` on startup.
*   **`tests/`**: Consists of standard xUnit suites including Unit tests, Integration tests (using Testcontainers for Qdrant), and end-to-end (E2E) integration flows.
*   **`docs/`**: Project documentation, operational notes, troubleshooting guides, and design notes that are too detailed for this README.

---

## 📚 Documentation

Longer project documentation lives in [`docs/`](docs/README.md). Start there for operational details such as upload storage, document links, and Qdrant metadata.

---

## ⚙️ Configuration & Prerequisites

### Prerequisites
*   **.NET 10 SDK** (to compile and run locally).
*   **Docker Desktop for Windows** (needed to spin up the Qdrant database or run the docker-compose stack).
    *   *Troubleshooting:* If you see the error `'docker' is not recognized as an internal or external command`, Docker Desktop is either not installed, not running, or its executable path is missing from your system `PATH` variable. Install [Docker Desktop for Windows](https://www.docker.com/products/docker-desktop/), restart your terminal, and try again.

### Local Settings (`appsettings.json`)
Both `DocumentRagSystem.WebApi` and `DocumentRagSystem.Worker` rely on `appsettings.json` or environment variables for configuration:

```json
{
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY",
    "EmbeddingModel": "gemini-embedding-001",
    "LlmModel": "gemini-3.6-flash"
  },
  "Authentication": {
    "ApiKey": "YOUR_SECURE_API_KEY"
  },
  "Qdrant": {
    "ConnectionString": "http://localhost:6334",
    "CollectionName": "document-chunks"
  },
  "SisterProject": {
    "BaseUrl": "http://localhost:5224"
  }
}
```

---

## 🔐 Authentication & Access Control

The portal is secured using **Microsoft Identity Framework** (SQLite persistence in `identity.db`) for web browser sessions and **API Key Authentication** for machine-to-machine clients:

### 1. Web Portal & Default Admin
* **Default Administrator:**
  * **Email:** `admin@ebmpabst.dk`
  * **Password:** `Admin123!`
* **Session Persistence:** Persistent HTTP-only secure cookie (`EbmRagAuth`) with sliding expiration.
* **Brute-Force Protection:** Account lockout after 5 consecutive failed login attempts (15-minute lock).
* **Brugeradministration (`/users.html`):** Authenticated administrators can list active accounts, create new employee accounts, unlock locked accounts, and remove users.

### 2. API Key Authentication (Automated / Headless Clients)
API endpoints (`/api/**`) accept an API key via either header:
* `X-Api-Key: <your-configured-api-key>` *(Recommended)*
* `Authorization: Bearer <your-configured-api-key>`
* `Authorization: ApiKey <your-configured-api-key>`

Configure your API key in production via the environment variable in `.env`:
```bash
API_KEY="your-custom-production-api-key"
```
Or in development via User Secrets:
```bash
dotnet user-secrets set "Authentication:ApiKey" "your-secret-key"
```

---

## 🖥️ Web Portal Pages

All portal pages share a consistent, centered 3-column responsive grid layout (`container mx-auto px-6 py-8 grid grid-cols-1 lg:grid-cols-3 gap-8`):

* **Chat Assistent (`/index.html`):** Conversational AI interface supporting multi-turn technical queries, persistent conversation history (1/3 left panel), domain-specific intent routing, and clickable citations (2/3 right panel).
* **Dokumenter (`/documents.html`):** Synchronous & background PDF uploads (1/3 left panel), search filter, page size selector (10, 20, 50, 100), pagination controls, single-document deletion (deletes disk files, SQLite record, and Qdrant chunks), and Danish date timestamps (2/3 right panel).
* **Databeregning (`/databeregning.html`):** Fully integrated energy calculation workspace with calculation history (1/3 left panel), dynamic fan formulas for efficiency and annual kWh consumption, and direct integration with ECIT Automate workqueue robot (2/3 right panel).
* **Brugeradministration (`/users.html`):** Clean administrative user management interface.
* **Login (`/login.html`):** Secure login interface with cookie authentication.

---

## 🌐 Global Language Detection & Companion Document Linking

* Datasheet PDFs typically follow the standard convention `Art_<articleid>-Doc_<documentid>.pdf`.
* During ingestion, documents undergo comprehensive language detection supporting **18+ languages** (English, Danish, German, Spanish, French, Italian, Portuguese, Dutch, Swedish, Norwegian, Polish, Czech, Finnish, Russian, Chinese, Japanese, Korean, Arabic) via character scripts, diacritics, technical wordsets, and LLM classification:
  * **English (`en`)**: Chunked, embedded with `gemini-embedding-001`, and indexed into Qdrant with status `Processed` (displayed as `Behandlet (Engelsk)`).
  * **Non-English**: Saved to disk under `uploads/` with status `Skipped` and stamped with detected language (e.g. `Ikke engelsk (Dansk)`, `Ikke engelsk (Spansk)`).
  * **Scanned / Textless PDFs**: Handled gracefully. If a PDF contains no digital text layer (e.g. scanned image without OCR), it is marked `Failed` with an informative message and returns `400 Bad Request` rather than an unhandled 500 error.
* **Companion Linking:** When a query matches an English document, any non-English document sharing the same `ArticleId` is automatically appended as a companion reference in the citations.
* **Document Overwrite:** Uploading a document with an existing `documentId`, `SourceDocumentId`, or filename automatically replaces the physical file, deletes old chunks from Qdrant, and updates the database record without creating duplicate entries.

---

## 🚀 Running the Application

### Option A: Running with Docker Compose (Recommended)
This is the easiest way to launch the Web API, the Worker, and a local Qdrant database instance side-by-side.

1. Set your Gemini API key as an environment variable:
   * **PowerShell:**
     ```powershell
     $env:GEMINI_API_KEY="YOUR_GEMINI_API_KEY"
     ```
   * **Command Prompt:**
     ```cmd
     set GEMINI_API_KEY=YOUR_GEMINI_API_KEY
     ```
2. Run Docker Compose:
   ```cmd
   docker compose up --build
   ```
3. The Web API will be available at `http://localhost:8080` (or HTTPS via `8081`).
4. **Data Seeding**: By default, automatic seeding of `data/Generelt/` on container startup is disabled (`SEED_DATA=false`). To enable automatic ingestion on startup, set `SEED_DATA=true` in your environment or `.env` file.

### Option B: Running Locally with .NET CLI
If you want to run the services individually on your machine:

1. **Spin up Qdrant** (e.g., via Docker):
   ```cmd
   docker run -p 6333:6333 -p 6334:6334 -v qdrant_storage:/qdrant/storage qdrant/qdrant:v1.10.0
   ```
2. **Launch the Web API**:
   ```cmd
   dotnet run --project src/DocumentRagSystem.WebApi/DocumentRagSystem.WebApi.csproj
   ```
3. **Launch the Worker Service**:
   ```cmd
   dotnet run --project src/DocumentRagSystem.Worker/DocumentRagSystem.Worker.csproj
   ```

### Option C: Deploying to Ubuntu Linux Server (Self-Contained Docker)
For deploying the system to an Ubuntu Linux production or staging server using modern self-contained Docker images:
1. Refer to the comprehensive [Ubuntu Linux Docker Deployment Guide](docs/deployment.md) for full instructions.
2. Deploy directly with a single command from your local Windows machine:
   ```powershell
   .\deploy-to-server.ps1
   ```
3. Or manually trigger deployment via Bash on Linux:
   ```bash
   ./scripts/deploy-to-remote.sh 185.158.62.152 5665 ubuntu
   ```

---

## 📡 API Endpoints

### 1. Health Check
*   **Endpoint:** `GET /health`
*   **Description:** Checks Web API status and system clock.
*   **Response:**
    ```json
    {
      "status": "Healthy",
      "timestamp": "2026-07-03T12:00:00Z"
    }
    ```

### 2. Upload Document
*   **Endpoint:** `POST /api/documents/upload`
*   **Content-Type:** `multipart/form-data`
*   **Parameters:**
    *   `file` (Form File, PDF format only, Required)
    *   `background` (Query parameter, Boolean, Optional)
*   **Behavior**:
    *   If `background=true`, the document metadata is saved as `Pending` and queued for background processing, returning `202 Accepted`.
    *   Otherwise, the API processes the PDF synchronously and returns the document object with `200 OK`.
    *   Re-uploading an existing document ID or filename replaces the file and vector chunks without duplicates.
    *   Textless PDFs return `400 Bad Request` with an explanation.

### 3. Retrieve Ingested Documents (with Paging & Search)
*   **Endpoint:** `GET /api/documents`
*   **Parameters:**
    *   `page` (Query parameter, Integer, Optional, e.g. `1`)
    *   `pageSize` (Query parameter, Integer 1–100, Optional, e.g. `20`)
    *   `search` (Query parameter, String, Optional, filters on filename, articleId, or documentId)
*   **Response (Paged):**
    ```json
    {
      "items": [ ... ],
      "totalCount": 142,
      "page": 1,
      "pageSize": 20,
      "totalPages": 8,
      "hasPreviousPage": false,
      "hasNextPage": true
    }
    ```
    *(Calling without `page`/`pageSize` parameters returns a raw array for backward compatibility).*

### 4. Delete Single Document
*   **Endpoint:** `DELETE /api/documents/{id}`
*   **Description:** Completely deletes the document: removes the physical PDF file from `uploads/`, deletes all vector chunks in Qdrant, and removes the metadata record from SQLite. Returns `204 No Content`.

### 5. Document Processing Status
*   **Endpoint:** `GET /api/documents/{fileName}/status` or `GET /api/documents/status?fileName=...`
*   **Description:** Returns the indexing status (`Pending`, `Processing`, `Processed`, `Skipped`, `Failed`), detected language, article ID, and error message if applicable.

### 6. Databeregning Endpoints
*   **`GET /databeregning`:** Retrieves historical calculation tasks from the ECIT Automate workqueue robot.
*   **`POST /databeregning`:** Creates and submits a new calculation with multi-fan input specs to the robot queue.
*   **`GET /databeregningsform`:** Serves the standalone databeregning HTML form.

### 7. Query Documents (RAG)
*   **Endpoint:** `POST /api/query`
*   **Content-Type:** `application/json`
*   **Payload:**
    ```json
    {
      "question": "What is the operating voltage range of the fan in the US datasheet?"
    }
    ```
*   **Response:**
    ```json
    {
      "answer": "The operating voltage of the specified fan is 115V AC according to page 2 of the data sheet...",
      "citations": [
        {
          "id": "doc1_chunk_3",
          "documentId": "doc1",
          "text": "Nominal voltage 115 VAC, Voltage range 85..125 VAC...",
          "index": 3
        }
      ]
    }
    ```

---

## 🧹 Maintenance & Server Operations

*   **Reset Server Data & Free Disk Space:**
    To completely wipe all uploaded PDFs, reset SQLite, purge Qdrant vectors, and clean up Docker cache/logs on the server:
    * **From Windows (PowerShell):**
      ```powershell
      .\slet_alle_data_på_server.ps1
      ```
    * **Directly on the server (Bash):**
      ```bash
      ./slet_alle_data_på_server.sh
      ```

---

## 🧪 Testing & Evaluation

The solution includes an extensive, multi-tiered test suite (**215+ automated tests** across unit, integration, and E2E suites) as well as an automated benchmark evaluation console app.

### Running Automated Tests

Run the full test suite across all test projects:

```bash
dotnet test
```

Run a specific test category or project:

```bash
# Run Unit Tests only
dotnet test tests/UnitTests/DocumentRagSystem.UnitTests.csproj

# Run Integration Tests only (requires Docker for Qdrant Testcontainers)
dotnet test tests/IntegrationTests/DocumentRagSystem.IntegrationTests.csproj

# Run End-to-End Tests
dotnet test tests/E2ETests/DocumentRagSystem.E2ETests.csproj
```

### Test Suite Architecture

| Test Project | Scope & Coverage | Key Tested Components |
|---|---|---|
| **`tests/UnitTests`** | Core business logic, parsing, chunking, and specialized multi-turn orchestrators | • **Intent & Categorization**: `InputGovernor` intent classification (`SPEC_LOOKUP`, `COMPARISON`, `COMPATIBILITY`, `TROUBLESHOOTING`, `CALCULATION`, `DESIGN`, `PROCEDURE`).<br>• **Domain Parsing**: `EbmProductCodeParser` extracting fan diameter, design codes, voltage keys, and generation numbers.<br>• **Multi-Turn State**: `ConversationalQueryRefiner`, `ConversationStateStore`, follow-up context merging, and pronoun resolution.<br>• **Specialized Workflows**: `ComparisonWorkflowExecutor`, `DiagnosticWorkflowExecutor`, and `TechnicalRagOrchestrator`.<br>• **Extraction & Chunking**: `PdfTextExtractor`, `MarkdownTableFixer`, sliding-window `ChunkingService`, and `DocumentProcessor`. |
| **`tests/IntegrationTests`** | Vector store transactions & persistence | • Vector point insertion, upsert, payload metadata preservation, and semantic similarity search in `QdrantVectorStore` via **Testcontainers** (gracefully skips with logging if Docker daemon is not active). |
| **`tests/E2ETests`** | Full ASP.NET Core pipeline integration | • End-to-end `WebApplicationFactory` tests executing document upload (`POST /api/documents/upload`), queue worker indexing, and grounded multi-turn semantic query verification (`POST /api/query`). |

---

## Continuous Integration (CI)

GitHub Actions workflow is configured in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) to automatically validate every push and pull request:

* **Build & Test**:
  * Sets up .NET 10 SDK with dependency caching.
  * Restores dependencies using `DocumentRagSystem.slnx` and builds the solution in Release mode.
  * Executes Unit, Integration, and E2E test suites with code coverage data collection (`XPlat Code Coverage`).
  * Publishes test result artifacts (`.trx`) and Cobertura code coverage reports.
* **Docker Build & Validation**:
  * Validates Docker Compose configuration (`docker compose config`).
  * Builds multi-stage Docker images for both `DocumentRagSystem.WebApi` and `DocumentRagSystem.Worker` (self-contained `linux-x64`).

---

## 📊 Benchmark Evaluation Tool

The `DocumentRagSystem.Evaluation` tool runs automated regression benchmarks against the running Web API using the curated domain Q&A datasets located in `docs/Spørgsmål_*.md`:

```bash
dotnet run --project src/DocumentRagSystem.Evaluation/DocumentRagSystem.Evaluation.csproj
```

* **Automated Ingestion**: Checks if the required PDF datasheets in `data/Generelt/` are already indexed; uploads missing documents automatically.
* **Batch Query Execution**: Queries all questions per document across difficulty levels (`easy`, `medium`, `hard`) and technical topics.
* **PDF Quality Report**: Generates `test.pdf` featuring cover metadata, question summaries, ground-truth expected answers, and actual RAG generated responses side-by-side.

CLI Options:
```text
  -a, --api-base <url>    Base URL of Web API (default: https://localhost:7251)
  -d, --data-dir <path>   PDF folder path (default: data/Generelt)
  --docs-dir <path>       Evaluation dataset folder (default: docs)
  -o, --output <file>     Output report path (default: test.pdf)
```
