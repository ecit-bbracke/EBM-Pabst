# Document RAG (Retrieval-Augmented Generation) System

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
    "EmbeddingModel": "gemini-embedding-2",
    "LlmModel": "gemini-2.5-flash"
  },
  "Qdrant": {
    "ConnectionString": "http://localhost:6334",
    "CollectionName": "document-chunks"
  }
}
```

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
   docker-compose up --build
   ```
3. The Web API will be available at `http://localhost:8080` (or HTTPS via `8081`).
4. On container startup, the Worker will scan the mapped `data/Generelt` directory and automatically begin processing and indexing all existing PDF data sheets.

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
    *   If `background=true`, the document metadata is instantly saved as `Pending` and queued for asynchronous background processing, returning `202 Accepted`.
    *   Otherwise, the API processes the PDF synchronously and returns the fully indexed document object with `200 OK`.

### 3. Retrieve Ingested Documents
*   **Endpoint:** `GET /api/documents`
*   **Description:** Returns a chronological list of all processed files and their pipeline execution state (`Processed`, `Processing`, `Failed`).

### 4. Query Documents (RAG)
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

## 🧪 Running Tests

The solution features a robust testing suite of unit, integration, and end-to-end tests:

```cmd
dotnet test
```

*   **Unit Tests**: Validate parsing, chunk sequence indexes, and text normalization bounds.
*   **Integration Tests**: Execute vector CRUD transactions against Qdrant (automatically spins up a temporary instance using Testcontainers if Docker is present; skips gracefully with logging if absent).
*   **E2E Tests**: Simulates a client uploading a PDF, waiting for index updates, and executing semantic query pipelines.
