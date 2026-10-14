# Project Structure

The solution is organized around a small clean-architecture style split:

- `DocumentRagSystem.Core` contains domain models, interfaces, and orchestration services.
- `DocumentRagSystem.Infrastructure` implements integrations such as PDF extraction, embeddings, LLM calls, repositories, and Qdrant.
- `DocumentRagSystem.WebApi` exposes the browser UI and HTTP API.
- `DocumentRagSystem.Worker` runs background ingestion and maintenance tasks.
- `DocumentRagSystem.Evaluation` runs automated benchmark Q&A evaluations against the API and generates quality PDF reports.
- `tests` contains unit, integration, and end-to-end test projects.

## Root

| Path | Purpose |
| --- | --- |
| `DocumentRagSystem.slnx` | Visual Studio solution file. Projects and solution folders are declared here. |
| `README.md` | Quick project overview, prerequisites, run commands, API summary, and test commands. |
| `docs/` | Longer documentation and runbooks. |
| `docker-compose.yml` | Local container orchestration for WebApi, Worker, and Qdrant. |
| `data/` | Source PDF data used by the Worker ingestion flow. |
| `src/` | Application source projects. |
| `tests/` | Test projects and shared test data. |

## Core Project

Path: `src/DocumentRagSystem.Core`

Core owns the application contracts and domain logic. It should not know about Gemini, Qdrant, ASP.NET Core, or file-system hosting details.

Important folders:

| Path | Purpose |
| --- | --- |
| `Models/` | Domain records such as `Document` and `DocumentChunk`. |
| `Interfaces/` | Contracts used by the application, for example `IVectorStore`, `IEmbeddingService`, `ILlmService`, and `IDocumentProcessor`. |
| `Services/` | Core orchestration and utility services such as `DocumentProcessor`, `ChunkingService`, and `DocumentQueue`. |

Typical dependency direction:

```text
WebApi / Worker -> Core interfaces and services
Infrastructure -> Core interfaces and models
Core -> no project-specific dependencies
```

## Infrastructure Project

Path: `src/DocumentRagSystem.Infrastructure`

Infrastructure provides concrete implementations for external systems and technical concerns.

Important folders:

| Path | Purpose |
| --- | --- |
| `Embeddings/` | Gemini embedding service implementation. |
| `Llm/` | Gemini answer-generation service and prompt construction. |
| `Repositories/` | In-memory document repository implementation. |
| `TextExtractors/` | PDF text extraction using PdfPig. |
| `VectorStores/` | Qdrant vector store implementation. |

The prompt used to generate answers is currently built in:

```text
src/DocumentRagSystem.Infrastructure/Llm/GeminiLlmService.cs
```

## WebApi Project

Path: `src/DocumentRagSystem.WebApi`

WebApi hosts the HTTP endpoints and static browser UI.

Important files and folders:

| Path | Purpose |
| --- | --- |
| `Program.cs` | Dependency injection, middleware, static file hosting, and minimal API endpoints. |
| `DTOs/` | Request and response DTOs, including query models and citation models. |
| `HostedServices/` | Background queue worker used by WebApi upload processing. |
| `Middleware/` | Global exception handling. |
| `wwwroot/` | Static browser UI. |
| `MarkdownTableFixer.cs` | Markdown cleanup helper before rendering answer or chunk text. |

Main endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | Health check. |
| `GET /api/documents` | Lists known documents (supports `page`, `pageSize`, and `search` query parameters). |
| `POST /api/documents/upload` | Uploads and processes PDF documents (supports synchronous and `?background=true`). |
| `DELETE /api/documents/{id}` | Deletes a document: removes physical PDF file, SQLite record, and Qdrant chunks. |
| `GET /api/documents/{fileName}/status` | Checks indexing status and detected language for a specific file. |
| `POST /api/query` | Runs semantic search and asks Gemini for an answer. |
| `GET /api/query/stream` | Server-Sent Events (SSE) streaming endpoint for real-time answer generation. |
| `GET /databeregning` | Retrieves calculation history from ECIT Automate workqueue robot. |
| `POST /databeregning` | Submits new calculation with fan specifications to workqueue robot. |
| `GET /databeregningsform` | Serves standalone databeregning HTML form. |
| `/uploads/...` | Serves uploaded PDFs so citations can link back to documents. |

## Worker Project

Path: `src/DocumentRagSystem.Worker`

Worker hosts background ingestion and cleanup logic.

Important files and folders:

| Path | Purpose |
| --- | --- |
| `Program.cs` | Worker dependency injection and hosted-service setup. |
| `Worker.cs` | Main ingestion worker (conditional startup seeding via `SEED_DATA=true`). |
| `HostedServices/PeriodicCleanupService.cs` | Periodic cleanup of temporary upload files. |

The Worker indexes PDFs from `data/Generelt/` (when `SEED_DATA=true`) and writes embeddings into Qdrant using the shared Core and Infrastructure services.

## Tests

Path: `tests`

The solution includes 215+ automated tests across the test projects:

| Project | Purpose |
| --- | --- |
| `UnitTests` | Fast tests for chunking, document processing, language detection (18+ languages), markdown table formatting, and specialized workflows. |
| `IntegrationTests` | Tests Qdrant vector store behavior, point insertion, chunk deletion, and payload preservation using Testcontainers. |
| `E2ETests` | Exercises full ASP.NET Core API flows (upload, deletion, pagination, query, authentication). |
| `TestData/` | Shared test files such as `sample.pdf` and Danish companion sample. |

## Where To Put New Code

Use these guidelines when adding features:

- Put domain models and interfaces in `DocumentRagSystem.Core`.
- Put external system implementations in `DocumentRagSystem.Infrastructure`.
- Put HTTP endpoints, request/response DTOs, and browser UI changes in `DocumentRagSystem.WebApi`.
- Put scheduled ingestion and maintenance behavior in `DocumentRagSystem.Worker`.
- Add tests in the smallest test project that covers the behavior.

## Where To Put New Documentation

- Put quick-start changes in the root `README.md`.
- Put detailed runbooks, design notes, and troubleshooting pages in `docs/`.
- Add every new docs page to `docs/README.md`.
- Add new docs files to `DocumentRagSystem.slnx` under the `/docs/` folder so they appear in Visual Studio Solution Explorer.
